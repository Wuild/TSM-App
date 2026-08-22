using System.IO.Pipes;

namespace TSM.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly FileStream? _instanceLock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task? _listener;
    private int _pendingActivation;

    public SingleInstanceCoordinator(string applicationId)
    {
        var identifier = new string(applicationId.Where(char.IsAsciiLetterOrDigit).ToArray()) + "-" +
                         new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
        _pipeName = identifier + "-activate";
        try
        {
            var lockPath = Path.Combine(Path.GetTempPath(), identifier + "-single-instance.lock");
            _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            IsPrimary = true;
        }
        catch (IOException)
        {
            IsPrimary = false;
        }
        if (IsPrimary)
            _listener = ListenAsync(_cancellation.Token);
    }

    public bool IsPrimary { get; }
    public event EventHandler? ActivationRequested;

    public bool ConsumeActivationRequest() => Interlocked.Exchange(ref _pendingActivation, 0) == 1;

    public void NotifyPrimaryInstance()
    {
        if (IsPrimary)
            return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                    PipeOptions.Asynchronous);
                client.Connect(200);
                client.WriteByte(1);
                client.Flush();
                return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                Thread.Sleep(75);
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_listener is not null)
        {
            try
            {
                _listener.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        _instanceLock?.Dispose();
        _cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                var request = new byte[1];
                if (await server.ReadAsync(request, cancellationToken) == 0)
                    continue;
                Interlocked.Exchange(ref _pendingActivation, 1);
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }
}
