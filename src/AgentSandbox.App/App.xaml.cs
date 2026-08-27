using Microsoft.UI.Xaml;
namespace AgentSandbox.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        UnhandledException += (_, args) => LogStartupFailure("UnhandledException", args.Exception);
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            LogStartupFailure("InitializeComponent", exception);
            throw;
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Services = new AppServices();
            Window = new MainWindow();
            Window.Closed += (_, _) => Services.Dispose();
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Window.Activate();
        }
        catch (Exception exception)
        {
            LogStartupFailure("OnLaunched", exception);
            throw;
        }
    }

    public static AppServices Services { get; private set; } = null!;

    private static void LogStartupFailure(string phase, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentSandbox",
                "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup-crash.log"),
                $"{DateTimeOffset.UtcNow:O} [{phase}] {exception}{Environment.NewLine}");
        }
        catch
        {
            // Never replace the original startup exception with a logging failure.
        }
    }
}
