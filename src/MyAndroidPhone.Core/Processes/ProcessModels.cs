namespace MyAndroidPhone.Core.Processes;

/// <param name="FileName">Executable to start. Never a shell command line.</param>
/// <param name="Arguments">Discrete arguments; they are passed through ArgumentList and never concatenated into a string.</param>
/// <param name="Timeout">Optional hard limit; the process tree is killed when it elapses.</param>
/// <param name="SensitiveArgumentIndexes">Indexes of arguments (e.g. a pairing code) that must be masked in logs.</param>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    string? WorkingDirectory = null,
    IReadOnlyCollection<int>? SensitiveArgumentIndexes = null);

public enum ProcessRunStatus
{
    /// <summary>The process started and exited by itself (the exit code may still be non-zero).</summary>
    Completed,
    TimedOut,
    Cancelled,
    ExecutableNotFound,
    StartFailed
}

public sealed record ProcessRunResult(
    ProcessRunStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string? FailureMessage)
{
    /// <summary>
    /// True only when the process completed with exit code 0. Callers that run adb must still inspect the
    /// output: some adb sub-commands print failures while exiting with 0.
    /// </summary>
    public bool ExitedSuccessfully => Status == ProcessRunStatus.Completed && ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}
