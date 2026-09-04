using Avalonia;

namespace OpenMusicFlow;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--install-browser", "chromium" or "firefox"])
            return Microsoft.Playwright.Program.Main(["install", args[1]]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
