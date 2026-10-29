namespace MyAndroidPhone.Core.Logging;

/// <summary>In-memory tail of the application log, used by the diagnostics module.</summary>
public interface IRecentLogSource
{
    IReadOnlyList<string> GetRecentLines();
}
