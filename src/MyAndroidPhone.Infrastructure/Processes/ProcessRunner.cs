using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Processes;

namespace MyAndroidPhone.Infrastructure.Processes;

/// <summary>
/// The single place external programs are started. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
/// (no shell, no string concatenation), both output streams are read asynchronously so the child can never block
/// on a full pipe, and cancellation or timeout kills the whole process tree.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(5);

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("A file name is required.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        var display = DescribeCommand(request);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(standardOutput, e.Data);
        process.ErrorDataReceived += (_, e) => Append(standardError, e.Data);

        logger.LogDebug("Starting: {Command}", display);

        try
        {
            if (!process.Start())
            {
                return Failed(ProcessRunStatus.StartFailed, "The process did not start.", stopwatch);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            logger.LogWarning("Executable not found: {Command}", display);
            return Failed(ProcessRunStatus.ExecutableNotFound, $"Executable not found: {request.FileName}", stopwatch);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogError(ex, "Could not start: {Command}", display);
            return Failed(ProcessRunStatus.StartFailed, $"Could not start {request.FileName}: {ex.Message}", stopwatch);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Nothing is ever written to stdin; closing it stops children that wait for input.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } timeout)
        {
            limit.CancelAfter(timeout);
        }

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested;
            await KillTreeAsync(process).ConfigureAwait(false);
            logger.LogWarning("{Outcome}: {Command}", timedOut ? "Timed out" : "Cancelled", display);
            return new ProcessRunResult(
                timedOut ? ProcessRunStatus.TimedOut : ProcessRunStatus.Cancelled,
                null,
                Snapshot(standardOutput),
                Snapshot(standardError),
                stopwatch.Elapsed,
                timedOut ? $"The command did not finish within {request.Timeout}." : "The command was cancelled.");
        }

        var result = new ProcessRunResult(
            ProcessRunStatus.Completed,
            process.ExitCode,
            Snapshot(standardOutput),
            Snapshot(standardError),
            stopwatch.Elapsed,
            null);
        logger.LogDebug("Exited with {ExitCode} after {Elapsed} ms: {Command}", result.ExitCode, (long)stopwatch.Elapsed.TotalMilliseconds, display);
        return result;
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }
    }

    private static string Snapshot(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }

    private static ProcessRunResult Failed(ProcessRunStatus status, string message, Stopwatch stopwatch) =>
        new(status, null, string.Empty, string.Empty, stopwatch.Elapsed, message);

    private static async Task KillTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Access denied or already gone; nothing more can be done.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static string DescribeCommand(ProcessRunRequest request)
    {
        var builder = new StringBuilder(request.FileName);
        for (var i = 0; i < request.Arguments.Count; i++)
        {
            builder.Append(' ');
            builder.Append(request.SensitiveArgumentIndexes?.Contains(i) == true ? "<redacted>" : request.Arguments[i]);
        }

        return builder.ToString();
    }
}
