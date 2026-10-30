using MyAndroidPhone.Core.Files;

namespace MyAndroidPhone.Core.Transfers;

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferState
{
    Queued,
    Running,
    Completed,

    /// <summary>Some files were transferred and some failed or were skipped because of errors.</summary>
    CompletedWithErrors,
    Failed,
    Cancelled
}

/// <param name="SourcePaths">Remote paths for a download, local paths for an upload.</param>
/// <param name="DestinationDirectory">Local folder for a download, remote folder for an upload.</param>
public sealed record TransferRequest(
    string DeviceSerial,
    string DeviceName,
    TransferDirection Direction,
    IReadOnlyList<string> SourcePaths,
    string DestinationDirectory,
    ConflictPolicy Conflict);

public sealed record TransferFailure(string Path, string Message);

public sealed record TransferSnapshot(
    Guid Id,
    TransferRequest Request,
    TransferState State,
    string Title,
    long BytesDone,
    long? BytesTotal,
    int FilesDone,
    int FilesSkipped,
    int FilesFailed,
    int? FilesTotal,
    string? CurrentItem,
    double BytesPerSecond,
    TimeSpan Elapsed,
    IReadOnlyList<TransferFailure> Failures,
    string? Message,
    DateTimeOffset CreatedAt)
{
    public bool IsFinished => State is TransferState.Completed or TransferState.CompletedWithErrors or TransferState.Failed or TransferState.Cancelled;
}

public interface ITransferService
{
    IReadOnlyList<TransferSnapshot> Transfers { get; }

    /// <summary>Raised on a background thread; progress updates are throttled.</summary>
    event EventHandler? Changed;

    Guid Enqueue(TransferRequest request);

    /// <summary>Queues a transfer and waits until it finishes (used by drag-out to Windows Explorer).</summary>
    Task<TransferSnapshot> RunAsync(TransferRequest request, CancellationToken cancellationToken);

    void Cancel(Guid id);

    /// <summary>
    /// Starts a new transfer for a finished one. Transfers are not resumable: files that failed start again from the
    /// beginning, and files that already arrived are skipped.
    /// </summary>
    Guid? Retry(Guid id);

    void ClearFinished();
}
