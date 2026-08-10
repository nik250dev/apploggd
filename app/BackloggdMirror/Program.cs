using Avalonia;
using System;
using System.Threading;
using System.Runtime.InteropServices;
using BackloggdMirror.Services;

namespace BackloggdMirror;

sealed class Program
{
    private static Mutex? _mutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance. It matters more than usual here: two copies would both poll for games
        // and both try to write the same session to Backloggd. The GUID keeps the name unique, and
        // holding the Mutex in a static field is what keeps it alive for the process lifetime.
        const string mutexName = "BackloggdMirror-SingleInstance-Mutex-5B9FA4A8-1E4D-4F2A-949B-510065EF9E76";
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        var logger = new AppLogger();

        if (!createdNew)
        {
            // Exit quietly. The user most likely clicked the icon while the app was already running
            // in the tray, so an error dialog would be noise.
            try
            {
                logger.Warning("Another instance of Apploggd is already running. Exiting application.");
            }
            catch
            {
                // Silently ignore logging failures on startup
            }
            _mutex.Dispose();
            return;
        }

        try
        {
            logger.Info("=== Application Started ===");
            logger.Info($"[Program] OS: {RuntimeInformation.OSDescription}; Arch: {RuntimeInformation.OSArchitecture}; Framework: {RuntimeInformation.FrameworkDescription}");
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.Error("[Program] Fatal application error.", ex);
            throw;
        }
        finally
        {
            // Releasing the mutex is what lets the app be reopened. Skipping it on a crash path
            // would leave the name held until the process is reaped, blocking every later launch.
            logger.Info("=== Application Closed ===");
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

