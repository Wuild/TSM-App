using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using TSM.Views;

namespace TSM.Services;

public sealed record ApplicationError(
    DateTimeOffset Timestamp,
    string Context,
    string ExceptionType,
    string Message,
    string Details,
    string LogPath,
    bool IsFatal);

public static class GlobalErrorService
{
    private const long MaximumLogBytes = 5_000_000;
    private static readonly object LogLock = new();
    private static readonly ConcurrentQueue<ApplicationError> Pending = new();
    private static readonly Dictionary<string, DateTimeOffset> Recent = new(StringComparer.Ordinal);
    private static Func<Window?>? _windowProvider;
    private static string _logPath = string.Empty;
    private static int _initialized;
    private static int _showingDialog;

    public static void Initialize(string logPath)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        _logPath = Path.GetFullPath(logPath);
        RotateLog();
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            var fatal = IsFatal(args.Exception);
            args.Handled = !fatal;
            Report(args.Exception, "User interface", fatal);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception, "Background task");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ??
                            new Exception(args.ExceptionObject?.ToString() ?? "Unknown application failure");
            Report(exception, "Application process", args.IsTerminating);
        };
    }

    public static void SetWindowProvider(Func<Window?> provider)
    {
        _windowProvider = provider;
        ScheduleDialog();
    }

    public static void Report(Exception exception, string context = "Application", bool isFatal = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var report = CreateReport(exception, context, isFatal);
        WriteLog(report);
        if (!ShouldDisplay(report))
            return;
        Pending.Enqueue(report);
        while (Pending.Count > 10)
            Pending.TryDequeue(out _);
        ScheduleDialog();
    }

    private static ApplicationError CreateReport(Exception exception, string context, bool isFatal)
    {
        var source = exception is AggregateException aggregate
            ? aggregate.Flatten()
            : exception;
        var message = string.IsNullOrWhiteSpace(source.Message)
            ? "An unexpected error occurred."
            : source.Message.Trim();
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var details = $"{source}\n\nApplication: TSM Desktop {version}\n" +
                      $"Operating system: {RuntimeInformation.OSDescription}\n" +
                      $"Architecture: {RuntimeInformation.ProcessArchitecture}";
        return new ApplicationError(
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(context) ? "Application" : context.Trim(),
            source.GetType().FullName ?? source.GetType().Name,
            message,
            details,
            _logPath,
            isFatal);
    }

    private static bool ShouldDisplay(ApplicationError report)
    {
        var key = $"{report.Context}\n{report.ExceptionType}\n{report.Message}";
        lock (Recent)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in Recent.Where(entry => now - entry.Value > TimeSpan.FromMinutes(1))
                         .Select(entry => entry.Key).ToArray())
                Recent.Remove(expired);
            if (Recent.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(5))
                return false;
            Recent[key] = now;
            return true;
        }
    }

    private static void ScheduleDialog()
    {
        if (_windowProvider is null || Pending.IsEmpty || Interlocked.CompareExchange(ref _showingDialog, 1, 0) != 0)
            return;
        Dispatcher.UIThread.Post(ShowPendingDialogsAsync, DispatcherPriority.Send);
    }

    private static async void ShowPendingDialogsAsync()
    {
        try
        {
            while (Pending.TryDequeue(out var report))
            {
                var dialog = new ErrorDialog(report);
                var owner = _windowProvider?.Invoke();
                if (owner is not null && owner.IsVisible)
                    await dialog.ShowDialog(owner);
                else
                    await ShowWithoutOwnerAsync(dialog);
            }
        }
        catch (Exception exception)
        {
            WriteLog(CreateReport(exception, "Error dialog", false));
        }
        finally
        {
            Interlocked.Exchange(ref _showingDialog, 0);
            if (!Pending.IsEmpty)
                ScheduleDialog();
        }
    }

    private static Task ShowWithoutOwnerAsync(Window dialog)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => completion.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        return completion.Task;
    }

    private static void WriteLog(ApplicationError report)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
            return;
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                var text = new StringBuilder()
                    .Append('[').Append(report.Timestamp.ToString("O")).Append("] ")
                    .Append(report.IsFatal ? "FATAL " : "ERROR ")
                    .Append(report.Context).AppendLine()
                    .Append(report.Details).AppendLine()
                    .AppendLine()
                    .ToString();
                File.AppendAllText(_logPath, text);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RotateLog()
    {
        try
        {
            if (!File.Exists(_logPath) || new FileInfo(_logPath).Length <= MaximumLogBytes)
                return;
            var previous = _logPath + ".previous";
            File.Move(_logPath, previous, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
