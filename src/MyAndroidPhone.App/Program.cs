using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MyAndroidPhone.Desktop;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) that keeps a single running instance: a second launch
/// hands its activation to the first one and exits, which also covers relaunching the app after setup.
/// </summary>
public static class Program
{
    private const string InstanceKey = "MyAndroidPhone.MainInstance";

    private static IntPtr _redirectEvent = IntPtr.Zero;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            RedirectActivationTo(AppInstance.GetCurrent().GetActivatedEventArgs(), keyInstance);
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static void RedirectActivationTo(AppActivationArguments arguments, AppInstance keyInstance)
    {
        // Redirecting from an STA thread must not block message pumping, hence the event + CoWaitForMultipleObjects.
        _redirectEvent = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(arguments).AsTask().Wait();
            SetEvent(_redirectEvent);
        });

        const uint cwmoDefault = 0;
        const uint infinite = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(cwmoDefault, infinite, 1, new[] { _redirectEvent }, out _);

        try
        {
            using var existing = Process.GetProcessById((int)keyInstance.ProcessId);
            SetForegroundWindow(existing.MainWindowHandle);
        }
        catch (ArgumentException)
        {
            // The first instance exited in the meantime; nothing to bring forward.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
