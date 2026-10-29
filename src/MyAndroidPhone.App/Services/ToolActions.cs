using MyAndroidPhone.Core.Tools;

namespace MyAndroidPhone.Desktop.Services;

/// <summary>User-initiated tool actions shared by the Tool Manager and the setup wizard, so the logic exists once.</summary>
public sealed class ToolActions(
    IToolService tools,
    IFilePickerService picker,
    INotificationService notifications,
    IFolderLauncher launcher)
{
    public async Task LocateAsync(ToolId id)
    {
        var definition = ToolCatalog.Get(id);
        var path = await picker.PickExecutableAsync();
        if (path is null)
        {
            return;
        }

        var result = await tools.SetConfiguredPathAsync(id, path);
        if (result.Succeeded)
        {
            notifications.Post(NotificationSeverity.Success, $"{definition.ShortName} is ready", $"Using version {result.Value.Version}.");
        }
        else
        {
            notifications.Post(NotificationSeverity.Error, $"That file cannot be used as {definition.ShortName}", result.ErrorMessage);
        }
    }

    public async Task UseAutomaticDetectionAsync(ToolId id)
    {
        await tools.ClearConfiguredPathAsync(id);
        var definition = ToolCatalog.Get(id);
        notifications.Post(NotificationSeverity.Information, $"{definition.ShortName}: automatic detection", "The selected file was cleared and the tool was detected again.");
    }

    public async Task OpenFolderAsync(string? executablePath)
    {
        var folder = string.IsNullOrEmpty(executablePath) ? null : Path.GetDirectoryName(executablePath);
        if (folder is null || !await launcher.OpenFolderAsync(folder))
        {
            notifications.Post(NotificationSeverity.Warning, "Folder not available", "The folder does not exist or could not be opened.");
        }
    }
}
