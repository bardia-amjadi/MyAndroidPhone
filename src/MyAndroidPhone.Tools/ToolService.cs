using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Processes;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Tools;

public sealed class ToolService : IToolService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<ToolService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ToolInstallation> _installations;

    public ToolService(IProcessRunner runner, ISettingsService settings, AppPaths paths, ILogger<ToolService> logger)
    {
        _runner = runner;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _installations = ToolCatalog.All
            .Select(d => new ToolInstallation(d, ToolStatus.NotChecked, null, null, ToolLocationSource.None, null))
            .ToList();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ToolInstallation> Installations => Volatile.Read(ref _installations);

    public ToolInstallation Get(ToolId id) => Installations.First(i => i.Definition.Id == id);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var detections = ToolCatalog.All.Select(d => DetectAsync(d, cancellationToken)).ToArray();
            var results = await Task.WhenAll(detections).ConfigureAwait(false);
            Volatile.Write(ref _installations, results.ToList());
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<OperationResult<ToolInstallation>> SetConfiguredPathAsync(
        ToolId id, string executablePath, CancellationToken cancellationToken = default)
    {
        var definition = ToolCatalog.Get(id);
        var expectedName = ExecutableFileName(definition);

        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            return OperationResult<ToolInstallation>.Failure(ErrorKind.InvalidInput, "Choose the full path of the executable.");
        }

        if (!string.Equals(Path.GetFileName(executablePath), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<ToolInstallation>.Failure(
                ErrorKind.InvalidInput,
                $"{definition.ShortName} must be selected as '{expectedName}', but '{Path.GetFileName(executablePath)}' was chosen.");
        }

        if (!File.Exists(executablePath))
        {
            return OperationResult<ToolInstallation>.Failure(ErrorKind.NotFound, $"The file does not exist: {executablePath}");
        }

        var probe = await ProbeAsync(definition, executablePath, cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return OperationResult<ToolInstallation>.Failure(probe.Error!);
        }

        await _settings.UpdateAsync(s => s.ConfiguredToolPaths[id.ToString()] = executablePath, cancellationToken).ConfigureAwait(false);
        var installation = await RefreshOneAsync(definition, cancellationToken).ConfigureAwait(false);
        return OperationResult<ToolInstallation>.Success(installation);
    }

    public async Task ClearConfiguredPathAsync(ToolId id, CancellationToken cancellationToken = default)
    {
        await _settings.UpdateAsync(s => s.ConfiguredToolPaths.Remove(id.ToString()), cancellationToken).ConfigureAwait(false);
        await RefreshOneAsync(ToolCatalog.Get(id), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolInstallation> RefreshOneAsync(ToolDefinition definition, CancellationToken cancellationToken)
    {
        var fresh = await DetectAsync(definition, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Installations.Select(i => i.Definition.Id == definition.Id ? fresh : i).ToList();
            Volatile.Write(ref _installations, updated);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return fresh;
    }

    private async Task<ToolInstallation> DetectAsync(ToolDefinition definition, CancellationToken cancellationToken)
    {
        if (_settings.Current.ConfiguredToolPaths.TryGetValue(definition.Id.ToString(), out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            // A configured path is explicit user intent: report it as invalid rather than silently using something else.
            var probe = await ProbeAsync(definition, configured, cancellationToken).ConfigureAwait(false);
            return probe.Succeeded
                ? new ToolInstallation(definition, ToolStatus.Found, configured, probe.Value, ToolLocationSource.Configured, null)
                : new ToolInstallation(definition, ToolStatus.Invalid, configured, null, ToolLocationSource.Configured,
                    $"The selected file no longer works: {probe.ErrorMessage} Choose it again or use automatic detection.");
        }

        string? firstProblem = null;
        string? problemPath = null;
        var problemSource = ToolLocationSource.None;
        foreach (var (path, source) in EnumerateCandidates(definition))
        {
            var probe = await ProbeAsync(definition, path, cancellationToken).ConfigureAwait(false);
            if (probe.Succeeded)
            {
                _logger.LogInformation("Detected {Tool} {Version} at {Path} ({Source})", definition.ShortName, probe.Value, path, source);
                return new ToolInstallation(definition, ToolStatus.Found, path, probe.Value, source, null);
            }

            _logger.LogWarning("Candidate for {Tool} rejected at {Path}: {Reason}", definition.ShortName, path, probe.ErrorMessage);
            firstProblem ??= probe.ErrorMessage;
            problemPath ??= path;
            if (problemSource == ToolLocationSource.None)
            {
                problemSource = source;
            }
        }

        return firstProblem is null
            ? new ToolInstallation(definition, ToolStatus.NotFound, null, null, ToolLocationSource.None,
                $"{definition.ShortName} was not found in the managed tools folder, ANDROID_HOME, PATH or common locations.")
            : new ToolInstallation(definition, ToolStatus.Invalid, problemPath, null, problemSource,
                $"A file named {ExecutableFileName(definition)} was found but could not be verified: {firstProblem}");
    }

    private async Task<OperationResult<string>> ProbeAsync(ToolDefinition definition, string path, CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(new ProcessRunRequest(path, definition.VersionArguments, ProbeTimeout), cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case ProcessRunStatus.ExecutableNotFound:
                return OperationResult<string>.Failure(ErrorKind.NotFound, "The file could not be found.");
            case ProcessRunStatus.StartFailed:
                return OperationResult<string>.Failure(ErrorKind.ProcessFailure, result.FailureMessage ?? "The file could not be started.");
            case ProcessRunStatus.TimedOut:
                return OperationResult<string>.Failure(ErrorKind.Timeout, "It did not answer within 10 seconds.");
            case ProcessRunStatus.Cancelled:
                return OperationResult<string>.Failure(ErrorKind.Cancelled, "The check was cancelled.");
        }

        if (!result.ExitedSuccessfully)
        {
            return OperationResult<string>.Failure(ErrorKind.ProcessFailure, $"It exited with code {result.ExitCode}.");
        }

        // Exit code 0 alone proves nothing: the output has to identify the tool.
        var version = ToolVersionParser.Parse(definition.Id, result.StandardOutput + Environment.NewLine + result.StandardError);
        return version is null
            ? OperationResult<string>.Failure(ErrorKind.InvalidInput, $"Its output does not look like {definition.ShortName}.")
            : OperationResult<string>.Success(version);
    }

    private IEnumerable<(string Path, ToolLocationSource Source)> EnumerateCandidates(ToolDefinition definition)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executable = ExecutableFileName(definition);

        IEnumerable<(string, ToolLocationSource)> Raw()
        {
            foreach (var directory in ManagedDirectories(definition))
            {
                yield return (Path.Combine(directory, executable), ToolLocationSource.ManagedFolder);
            }

            if (definition.Id == ToolId.Adb)
            {
                foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
                {
                    var root = Environment.GetEnvironmentVariable(variable);
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        yield return (Path.Combine(root, "platform-tools", executable), ToolLocationSource.AndroidSdkEnvironment);
                    }
                }
            }

            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return (Path.Combine(directory.Trim().Trim('"'), executable), ToolLocationSource.PathEnvironment);
            }

            if (definition.Id == ToolId.Adb && OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    yield return (Path.Combine(local, "Android", "Sdk", "platform-tools", executable), ToolLocationSource.CommonLocation);
                }
            }
        }

        foreach (var (path, source) in Raw())
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(full) && File.Exists(full))
            {
                yield return (full, source);
            }
        }
    }

    /// <summary>
    /// adb is only taken from a "platform-tools" folder: scrcpy archives bundle their own adb, and mixing adb
    /// versions makes the adb server restart repeatedly.
    /// </summary>
    private IEnumerable<string> ManagedDirectories(ToolDefinition definition)
    {
        var root = _paths.ToolsDirectory;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pattern = definition.Id == ToolId.Adb ? "platform-tools" : "scrcpy*";
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }

    private static string ExecutableFileName(ToolDefinition definition) =>
        definition.ExecutableBaseName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
}
