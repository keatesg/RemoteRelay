using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.ReactiveUI;
using RemoteRelay.Common;

namespace RemoteRelay;

internal class Program
{
    private static readonly string ErrorLogPath = AppPaths.ClientLogPath;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Set up global exception handlers for crash prevention
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine(Common.VersionHelper.GetVersion());
            return;
        }

        try
        {
            BuildAvaloniaApp()
               .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogException("Startup crash", ex);
            throw; // Re-throw to let the OS handle it, but we've logged it
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("Unhandled exception", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved task exception", e.Exception);
        e.SetObserved(); // Prevent crash from unobserved task exceptions
    }

    private static void LogException(string context, Exception ex)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logEntry = $"[{timestamp}] {context}: {ex}\n\n";
            File.AppendAllText(ErrorLogPath, logEntry);
            Console.Error.WriteLine($"[{timestamp}] {context}: {ex.Message}");
        }
        catch
        {
            // Don't throw from the exception handler
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
           .UsePlatformDetect()
           .WithInterFont()
           .LogToTrace().UseSkia().UseReactiveUI();
    }
}