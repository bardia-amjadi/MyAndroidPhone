using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Settings;

namespace MyAndroidPhone.Infrastructure.Storage;

/// <summary>
/// Copy-on-write JSON state: updates are applied to a copy, written to disk, and only then published, so readers
/// never observe a half-applied change and a failed write leaves the previous state intact.
/// </summary>
public abstract class JsonStateService<T> where T : class, new()
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T _current;

    protected JsonStateService(string path, ILogger logger)
    {
        _path = path;
        _current = JsonFile.ReadOrDefault<T>(path, logger);
    }

    public T Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public async Task UpdateAsync(Action<T> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var copy = JsonFile.Clone(_current);
            mutate(copy);
            await JsonFile.WriteAsync(_path, copy, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, copy);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = new T();
            await JsonFile.WriteAsync(_path, fresh, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, fresh);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class SettingsService : JsonStateService<AppSettings>, ISettingsService
{
    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
        : base(paths.SettingsFile, logger)
    {
    }
}

public sealed class SetupStateService : JsonStateService<SetupState>, ISetupStateService
{
    public SetupStateService(AppPaths paths, ILogger<SetupStateService> logger)
        : base(paths.SetupStateFile, logger)
    {
    }
}
