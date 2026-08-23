using Avalonia;
using Avalonia.Media;
using TSM.Services;

namespace TSM;

internal static class Program
{
    private const string LinuxWindowClass = "com.wuild.TSMDesktop";
    internal static SingleInstanceCoordinator? SingleInstance { get; private set; }
    internal static string RenderingBackend { get; private set; } = "Automatic";

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

    public static AppBuilder BuildAvaloniaApp()
    {
        var settings = new SettingsStore(new AppPaths()).Load();
        var builder = AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace();
        return ConfigureRendering(builder.UsePlatformDetect(), settings.HardwareAcceleration);
    }

    private static AppBuilder ConfigureRendering(AppBuilder builder, bool hardwareAcceleration)
    {
        if (hardwareAcceleration)
        {
            RenderingBackend = OperatingSystem.IsLinux()
                ? "X11 · hardware acceleration with software fallback"
                : "Native · hardware acceleration with software fallback";
            return OperatingSystem.IsLinux()
                ? builder.With(new X11PlatformOptions { WmClass = LinuxWindowClass })
                : builder;
        }
        if (OperatingSystem.IsWindows())
        {
            RenderingBackend = "Windows · software rendering";
            return builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }
        if (OperatingSystem.IsMacOS())
        {
            RenderingBackend = "macOS · software rendering";
            return builder.With(new AvaloniaNativePlatformOptions
                { RenderingMode = [AvaloniaNativeRenderingMode.Software] });
        }
        RenderingBackend = "X11 · software rendering";
        return builder.With(new X11PlatformOptions
            { RenderingMode = [X11RenderingMode.Software], WmClass = LinuxWindowClass });
    }
}
