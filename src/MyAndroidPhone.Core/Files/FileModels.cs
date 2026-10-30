using MyAndroidPhone.Core.Results;

namespace MyAndroidPhone.Core.Files;

public enum RemoteEntryKind
{
    File,
    Directory,
    Link,
    Other
}

/// <param name="Modified">Device-local time as printed by ls; the device's time zone is not known.</param>
public sealed record RemoteEntry(
    string Name,
    string FullPath,
    RemoteEntryKind Kind,
    long? Size,
    DateTime? Modified,
    string Permissions,
    string Owner,
    string? LinkTarget)
{
    public bool IsDirectory => Kind == RemoteEntryKind.Directory;

    /// <summary>Directories and symbolic links can be opened as folders (a link may turn out to point at a file).</summary>
    public bool CanOpen => Kind is RemoteEntryKind.Directory or RemoteEntryKind.Link;
}

/// <summary>
/// What the app can honestly say about a location. Android keeps most of the file system away from the adb shell
/// user; this is never a promise of access, only an explanation.
/// </summary>
public enum StorageAccess
{
    SharedStorage,
    AppSpecific,
    ShellWorkspace,
    ProtectedAppData,
    System,
    Other
}

/// <param name="WritesBlocked">True when the app refuses to write here (read-only or protected), instead of trying and failing.</param>
public sealed record StorageClassification(StorageAccess Access, string Title, string Explanation, bool WritesBlocked);

public sealed record StorageLocation(string Name, string Path, string IconGlyph, StorageClassification Classification);

public sealed record DirectoryListing(string Path, IReadOnlyList<RemoteEntry> Entries, StorageClassification Classification, string? Warning);

public sealed record SearchResults(IReadOnlyList<RemoteEntry> Entries, bool Truncated, int UnreadableFolders);

public enum ConflictPolicy
{
    Overwrite,
    Skip,
    KeepBoth
}

/// <summary>Everything an on-device file operation needs. Every method targets one explicit device serial.</summary>
public interface IFileExplorerService
{
    Task<IReadOnlyList<StorageLocation>> GetLocationsAsync(string serial, CancellationToken cancellationToken);

    Task<OperationResult<DirectoryListing>> ListAsync(string serial, string path, CancellationToken cancellationToken);

    Task<OperationResult<RemoteEntry>> StatAsync(string serial, string path, CancellationToken cancellationToken);

    Task<OperationResult<string>> CreateFolderAsync(string serial, string parent, string name, CancellationToken cancellationToken);

    Task<OperationResult<string>> RenameAsync(string serial, string path, string newName, CancellationToken cancellationToken);

    Task<OperationResult<string>> DeleteAsync(string serial, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Copies or moves items inside one device. Conflicts are resolved with <paramref name="policy"/>.</summary>
    Task<OperationResult<string>> PasteAsync(
        string serial, IReadOnlyList<string> sources, string destinationDirectory, bool move, ConflictPolicy policy, CancellationToken cancellationToken);

    /// <summary>Returns the names (from <paramref name="names"/>) that already exist in <paramref name="directory"/>.</summary>
    Task<OperationResult<IReadOnlyList<string>>> FindExistingAsync(
        string serial, string directory, IReadOnlyList<string> names, CancellationToken cancellationToken);

    Task<OperationResult<SearchResults>> SearchAsync(string serial, string root, string pattern, CancellationToken cancellationToken);
}
