using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace MyAndroidPhone.Desktop.Services;

/// <summary>Marshals work onto the UI thread. Resolved first on the UI thread, which is where the queue is captured.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class UiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _queue.TryEnqueue(() => action());
        }
    }
}

/// <summary>Holds the main window so pickers can parent themselves to it (DI singleton, not static state).</summary>
public sealed class WindowContext
{
    public Window? MainWindow { get; set; }

    public IntPtr Handle => MainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(MainWindow);
}

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record AppNotification(NotificationSeverity Severity, string Title, string Message);

public interface INotificationService
{
    event EventHandler<AppNotification>? Posted;

    void Post(NotificationSeverity severity, string title, string message = "");

    /// <summary>Messages posted before the shell was listening (for example during startup).</summary>
    IReadOnlyList<AppNotification> DrainPending();
}

public sealed class NotificationService : INotificationService
{
    private readonly object _gate = new();
    private readonly List<AppNotification> _pending = new();

    public event EventHandler<AppNotification>? Posted;

    public void Post(NotificationSeverity severity, string title, string message = "")
    {
        var notification = new AppNotification(severity, title, message);
        var handler = Posted;
        if (handler is null)
        {
            lock (_gate)
            {
                _pending.Add(notification);
                if (_pending.Count > 20)
                {
                    _pending.RemoveAt(0);
                }
            }

            return;
        }

        handler(this, notification);
    }

    public IReadOnlyList<AppNotification> DrainPending()
    {
        lock (_gate)
        {
            var copy = _pending.ToList();
            _pending.Clear();
            return copy;
        }
    }
}

public interface IFilePickerService
{
    /// <summary>Lets the user choose an .exe file. Returns null when the picker is cancelled.</summary>
    Task<string?> PickExecutableAsync();

    /// <summary>Lets the user choose where to save a file. Returns null when cancelled.</summary>
    Task<string?> PickSaveFileAsync(string suggestedFileName, string fileTypeDescription, string extension);
}

public sealed class FilePickerService(WindowContext window) : IFilePickerService
{
    public async Task<string?> PickExecutableAsync()
    {
        var handle = window.Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, handle);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName, string fileTypeDescription, string extension)
    {
        var handle = window.Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add(fileTypeDescription, new List<string> { extension });
        InitializeWithWindow.Initialize(picker, handle);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

public interface IFolderLauncher
{
    /// <summary>Opens a folder in Windows Explorer. Returns false when the folder does not exist or could not be opened.</summary>
    Task<bool> OpenFolderAsync(string path);
}

public sealed class FolderLauncher : IFolderLauncher
{
    public async Task<bool> OpenFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        return await Launcher.LaunchFolderPathAsync(path);
    }
}
