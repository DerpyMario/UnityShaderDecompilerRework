using Avalonia;

namespace USCSandbox.Gui;

internal static class Program
{
    // Avalonia needs an STA thread on Windows and has to be initialised before anything
    // touches a control, so keep this method free of app logic.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
