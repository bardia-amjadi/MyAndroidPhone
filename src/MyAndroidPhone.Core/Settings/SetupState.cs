namespace MyAndroidPhone.Core.Settings;

public enum SetupStep
{
    Welcome = 0,
    Dependencies = 1,
    Legal = 2,
    Finish = 3
}

/// <summary>Persisted after every step so an unexpected shutdown resumes where the user left off.</summary>
public sealed class SetupState
{
    public int SchemaVersion { get; set; } = 1;

    public SetupStep CurrentStep { get; set; } = SetupStep.Welcome;

    public bool Completed { get; set; }

    /// <summary>Version of the legal notices the user accepted; a newer notice version asks again.</summary>
    public string? AcceptedNoticesVersion { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }
}
