namespace SqliteManager.Core.Data;

/// <summary>Loads rows on demand in fixed-size pages and keeps the most recently used pages.</summary>
public sealed class PagedRowCache<TRow>
    where TRow : class
{
    public delegate Task<IReadOnlyList<TRow>> PageLoader(long offset, int limit, CancellationToken cancellationToken);

    private readonly PageLoader _loader;
    private readonly Dictionary<long, CachedPage> _pages = [];
    private readonly Dictionary<long, Task> _loading = [];
    private readonly HashSet<long> _failed = [];
    private readonly Lock _sync = new();
    private CancellationTokenSource _cts = new();
    private long _accessCounter;

    public PagedRowCache(PageLoader loader, int pageSize = 500, int maxPages = 50)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);
        _loader = loader;
        PageSize = pageSize;
        MaxPages = maxPages;
    }

    public int PageSize { get; }

    public int MaxPages { get; }

    public int CachedPageCount
    {
        get
        {
            lock (_sync)
            {
                return _pages.Count;
            }
        }
    }

    /// <summary>Raised when a page finishes loading. May be raised on any thread.</summary>
    public event EventHandler<PageLoadedEventArgs>? PageLoaded;

    /// <summary>Raised when a page fails to load. A failed page is not retried until <see cref="Clear"/>.</summary>
    public event EventHandler<PageLoadFailedEventArgs>? PageLoadFailed;

    /// <summary>
    /// Returns the row if its page is cached. Otherwise starts loading the page and returns false.
    /// </summary>
    public bool TryGetRow(long rowIndex, out TRow? row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        var pageIndex = rowIndex / PageSize;
        lock (_sync)
        {
            if (_pages.TryGetValue(pageIndex, out var page))
            {
                page.LastAccess = ++_accessCounter;
                var offset = (int)(rowIndex % PageSize);
                row = offset < page.Rows.Count ? page.Rows[offset] : null;
                return row is not null;
            }
        }

        _ = LoadPageAsync(pageIndex);
        row = null;
        return false;
    }

    /// <summary>Adds rows that the caller already loaded.</summary>
    public void Seed(long pageIndex, IReadOnlyList<TRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_sync)
        {
            StorePage(pageIndex, rows);
        }
    }

    /// <summary>Loads a page if it is not cached, loading, or failed.</summary>
    public Task LoadPageAsync(long pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        lock (_sync)
        {
            if (_pages.ContainsKey(pageIndex) || _failed.Contains(pageIndex))
            {
                return Task.CompletedTask;
            }

            if (_loading.TryGetValue(pageIndex, out var existing))
            {
                return existing;
            }

            var task = LoadCoreAsync(pageIndex, _cts.Token);
            if (!task.IsCompleted)
            {
                _loading[pageIndex] = task;
            }

            return task;
        }
    }

    /// <summary>Drops all cached pages and cancels pending loads.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _pages.Clear();
            _loading.Clear();
            _failed.Clear();
        }
    }

    private async Task LoadCoreAsync(long pageIndex, CancellationToken cancellationToken)
    {
        IReadOnlyList<TRow> rows;
        try
        {
            rows = await _loader(pageIndex * PageSize, PageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _loading.Remove(pageIndex);
                _failed.Add(pageIndex);
            }

            PageLoadFailed?.Invoke(this, new PageLoadFailedEventArgs(pageIndex, ex));
            return;
        }

        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _loading.Remove(pageIndex);
            StorePage(pageIndex, rows);
        }

        PageLoaded?.Invoke(this, new PageLoadedEventArgs(pageIndex, pageIndex * PageSize, rows.Count));
    }

    private void StorePage(long pageIndex, IReadOnlyList<TRow> rows)
    {
        _pages[pageIndex] = new CachedPage(rows) { LastAccess = ++_accessCounter };
        while (_pages.Count > MaxPages)
        {
            var oldest = _pages.MinBy(p => p.Value.LastAccess).Key;
            _pages.Remove(oldest);
        }
    }

    private sealed class CachedPage(IReadOnlyList<TRow> rows)
    {
        public IReadOnlyList<TRow> Rows { get; } = rows;

        public long LastAccess { get; set; }
    }
}

public sealed class PageLoadedEventArgs(long pageIndex, long firstRowIndex, int rowCount) : EventArgs
{
    public long PageIndex { get; } = pageIndex;

    public long FirstRowIndex { get; } = firstRowIndex;

    public int RowCount { get; } = rowCount;
}

public sealed class PageLoadFailedEventArgs(long pageIndex, Exception error) : EventArgs
{
    public long PageIndex { get; } = pageIndex;

    public Exception Error { get; } = error;
}
