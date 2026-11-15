using SqliteManager.Core.Jobs;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C stops the running statement; the job is rolled back and recorded as cancelled.
    e.Cancel = true;
    cancel.Cancel();
};

return await CommandLine.RunAsync(args, Console.Out, Console.Error, cancel.Token);
