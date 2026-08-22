using Avalonia;
using Avalonia.Media;
using TSM.Services;

namespace TSM;

internal static class Program
{
    internal static SingleInstanceCoordinator? SingleInstance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        GlobalErrorService.Initialize(new AppPaths().ErrorLogFile);
        using var singleInstance = new SingleInstanceCoordinator("com.wuild.tsm-desktop");
        if (!singleInstance.IsPrimary)
        {
            singleInstance.NotifyPrimaryInstance();
            return;
        }

        SingleInstance = singleInstance;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
