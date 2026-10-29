using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Core.Tools;

/// <summary>Central owner of external tool detection. No other module locates or runs tool discovery itself.</summary>
public interface IToolService
{
    IReadOnlyList<ToolInstallation> Installations { get; }

    event EventHandler? Changed;

    ToolInstallation Get(ToolId id);

    /// <summary>Re-detects every tool. Safe to call repeatedly.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a user-selected executable (its file name must match the tool, and it is run once with its
    /// version argument) and only then persists it.
    /// </summary>
    Task<OperationResult<ToolInstallation>> SetConfiguredPathAsync(ToolId id, string executablePath, CancellationToken cancellationToken = default);

    /// <summary>Removes the user-selected path and falls back to automatic detection.</summary>
    Task ClearConfiguredPathAsync(ToolId id, CancellationToken cancellationToken = default);
}
