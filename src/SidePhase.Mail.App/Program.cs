using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace SidePhase.Mail.App;

/// <summary>
/// Entry point. SidePhase Mail runs as a single instance per Windows user and data folder: starting it again (from the
/// Start menu, a mailto: link or a notification) hands the request to the copy that is already running and exits.
/// Two copies working on the same mail cache could otherwise sync over each other or both send the same scheduled
/// message.
/// </summary>
public static class Program
{
    private const uint CWMO_DEFAULT = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint dwFlags, uint dwTimeout, uint cHandles, nint[] pHandles, out uint lpdwIndex);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Decide where data lives before anything (including logging) touches it.
        Infrastructure.Storage.AppDataPaths.UseDefaultRoot(AppIdentity.DataRoot);

        if (RedirectToRunningInstance()) return 0;

        Application.Start(_ignored =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>The key is per data folder, so the portable build and the installed app can run side by side.</summary>
    internal static string InstanceKey => "SidePhaseMail-" + AppIdentity.DataScope;

    /// <summary>True if another instance is running and has been given this activation.</summary>
    private static bool RedirectToRunningInstance()
    {
        try
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (main.IsCurrent) return false;

            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            AllowSetForegroundWindow(main.ProcessId);

            // The redirect must not block this STA thread's message pump, so wait on it with CoWaitForMultipleObjects.
            using var done = new ManualResetEvent(false);
            _ = Task.Run(() =>
            {
                try
                {
                    main.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Log("handing over to the running instance", ex);
                }
                finally
                {
                    done.Set();
                }
            });
            CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
            return true;
        }
        catch (Exception ex)
        {
            // If instancing isn't available, start normally rather than not at all.
            StartupDiagnostics.Log("single-instance check", ex);
            return false;
        }
    }
}
