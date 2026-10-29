namespace MyAndroidPhone.Core.Settings;

/// <summary>
/// Persisted application settings. <see cref="Current"/> is an immutable-by-convention snapshot:
/// change it only through <see cref="UpdateAsync"/>.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Raised on a background thread after a change was written to disk.</summary>
    event EventHandler? Changed;

    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);
}

public interface ISetupStateService
{
    SetupState Current { get; }

    event EventHandler? Changed;

    Task UpdateAsync(Action<SetupState> mutate, CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);
}
