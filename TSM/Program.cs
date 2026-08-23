using Avalonia;
using Avalonia.Media;
using TSM.Services;

namespace TSM;

internal static class Program
{
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
        if (CanUseNativeWayland(settings))
        {
            RenderingBackend = "Native Wayland · EGL hardware acceleration";
            return builder.UseWayland().UseSkia().UseHarfBuzz();
        }
        return ConfigureRendering(builder.UsePlatformDetect(), settings.HardwareAcceleration);
    }

    private static bool CanUseNativeWayland(Models.AppSettings settings)
    {
        if (!OperatingSystem.IsLinux() || !settings.HardwareAcceleration || !settings.PreferNativeWayland)
            return false;
        if (!string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                StringComparison.OrdinalIgnoreCase))
            return false;
        var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (string.IsNullOrWhiteSpace(display))
            return false;
        if (Path.IsPathRooted(display))
            return File.Exists(display);
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtimeDirectory) && File.Exists(Path.Combine(runtimeDirectory, display));
    }

    private static AppBuilder ConfigureRendering(AppBuilder builder, bool hardwareAcceleration)
    {
        if (hardwareAcceleration)
        {
            RenderingBackend = OperatingSystem.IsLinux()
                ? "X11 · hardware acceleration with software fallback"
                : "Native · hardware acceleration with software fallback";
            return builder;
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
        return builder.With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
    }
}
