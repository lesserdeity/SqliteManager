using SqliteManager.Core.Data;
using PagedRowCache = SqliteManager.Core.Data.PagedRowCache<object[]>;

namespace SqliteManager.Core.Tests;

public class PagedRowCacheTests
{
    /// <summary>A loader over rows 0..totalRows-1 whose value is the row index.</summary>
    private sealed class FakeLoader(long totalRows)
    {
        public List<long> RequestedOffsets { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public Exception? FailWith { get; set; }

        public async Task<IReadOnlyList<object[]>> Load(long offset, int limit, CancellationToken ct)
        {
            lock (RequestedOffsets)
            {
                RequestedOffsets.Add(offset);
            }

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(ct);
            }

            await Task.Yield();
            if (FailWith is not null)
            {
                throw FailWith;
            }

            var end = Math.Min(totalRows, offset + limit);
            var rows = new List<object[]>();
            for (var i = offset; i < end; i++)
            {
                rows.Add([i]);
            }

            return rows;
        }
    }

    [Fact]
    public async Task Miss_starts_a_load_then_row_is_available()
    {
        var loader = new FakeLoader(1000);
        var cache = new PagedRowCache(loader.Load, pageSize: 100);
        var loaded = new TaskCompletionSource<PageLoadedEventArgs>();
        cache.PageLoaded += (_, e) => loaded.TrySetResult(e);

        Assert.False(cache.TryGetRow(250, out _));
        var args = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, args.PageIndex);
        Assert.Equal(200, args.FirstRowIndex);
        Assert.Equal(100, args.RowCount);
        Assert.True(cache.TryGetRow(250, out var row));
        Assert.Equal(250L, row![0]);
        Assert.Equal([200L], loader.RequestedOffsets);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_page_load_it_once()
    {
        var loader = new FakeLoader(1000) { Gate = new TaskCompletionSource() };
        var cache = new PagedRowCache(loader.Load, pageSize: 100);

        var first = cache.LoadPageAsync(0);
        cache.TryGetRow(5, out _);
        cache.TryGetRow(99, out _);
        var again = cache.LoadPageAsync(0);
        loader.Gate.SetResult();
        await Task.WhenAll(first, again);

        Assert.Equal([0L], loader.RequestedOffsets);
    }

    [Fact]
    public void Seed_makes_rows_available_without_loading()
    {
        var loader = new FakeLoader(1000);
        var cache = new PagedRowCache(loader.Load, pageSize: 2);

        cache.Seed(0, [[10L], [11L]]);

        Assert.True(cache.TryGetRow(1, out var row));
        Assert.Equal(11L, row![0]);
        Assert.Empty(loader.RequestedOffsets);
    }

    [Fact]
    public async Task Last_partial_page_returns_false_beyond_its_rows()
    {
        var loader = new FakeLoader(150);
        var cache = new PagedRowCache(loader.Load, pageSize: 100);

        await cache.LoadPageAsync(1);

        Assert.True(cache.TryGetRow(149, out _));
        Assert.False(cache.TryGetRow(150, out _));
        Assert.Single(loader.RequestedOffsets);
    }

    [Fact]
    public async Task Evicts_least_recently_used_pages()
    {
        var loader = new FakeLoader(10_000);
        var cache = new PagedRowCache(loader.Load, pageSize: 10, maxPages: 3);

        await cache.LoadPageAsync(0);
        await cache.LoadPageAsync(1);
        await cache.LoadPageAsync(2);
        Assert.True(cache.TryGetRow(0, out _)); // Touch page 0 so page 1 is the oldest.
        await cache.LoadPageAsync(3);

        Assert.Equal(3, cache.CachedPageCount);
        Assert.True(cache.TryGetRow(5, out _));
        Assert.True(cache.TryGetRow(25, out _));
        Assert.True(cache.TryGetRow(35, out _));
        Assert.False(cache.TryGetRow(15, out _)); // Page 1 was evicted; this starts a reload.
    }

    [Fact]
    public async Task Failed_page_raises_event_and_is_not_retried_until_cleared()
    {
        var loader = new FakeLoader(1000) { FailWith = new InvalidOperationException("boom") };
        var cache = new PagedRowCache(loader.Load, pageSize: 100);
        var failed = new TaskCompletionSource<PageLoadFailedEventArgs>();
        cache.PageLoadFailed += (_, e) => failed.TrySetResult(e);

        cache.TryGetRow(0, out _);
        var args = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("boom", args.Error.Message);

        cache.TryGetRow(0, out _);
        await cache.LoadPageAsync(0);
        Assert.Single(loader.RequestedOffsets);

        loader.FailWith = null;
        cache.Clear();
        await cache.LoadPageAsync(0);
        Assert.True(cache.TryGetRow(0, out _));
        Assert.Equal(2, loader.RequestedOffsets.Count);
    }

    [Fact]
    public async Task Clear_cancels_pending_loads_and_discards_their_results()
    {
        var loader = new FakeLoader(1000) { Gate = new TaskCompletionSource() };
        var cache = new PagedRowCache(loader.Load, pageSize: 100);
        var raised = false;
        cache.PageLoaded += (_, _) => raised = true;

        var pending = cache.LoadPageAsync(0);
        cache.Clear();
        await pending;

        Assert.False(raised);
        Assert.Equal(0, cache.CachedPageCount);
    }

    [Fact]
    public async Task Works_with_table_data_service()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY)",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 2500) INSERT INTO t SELECT x FROM n");
        var service = new TableDataService(db.Session);
        var source = await service.OpenAsync("t");
        var cache = new PagedRowCache<TableRow>(
            (offset, limit, ct) => service.GetPageAsync(source, offset, limit, ct),
            pageSize: 1000);

        await cache.LoadPageAsync(2);

        Assert.True(cache.TryGetRow(2499, out var row));
        Assert.Equal(2500L, row!.Values[0]);
        Assert.Equal(new RowKey(2500L), row.Key);
    }
}
