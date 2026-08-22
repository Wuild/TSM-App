namespace TSM.Services;

public sealed class BackgroundSyncScheduler(TimeSpan interval) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _runner;

    public TimeSpan Interval { get; } = interval > TimeSpan.Zero
        ? interval
        : throw new ArgumentOutOfRangeException(nameof(interval));

    public void Start(Func<CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_runner is not null)
            return;
        _runner = RunAsync(callback, _cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        if (_runner is not null)
        {
            try
            {
                await _runner;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cancellation.Dispose();
    }

    private async Task RunAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await callback(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }
        }
    }
}
