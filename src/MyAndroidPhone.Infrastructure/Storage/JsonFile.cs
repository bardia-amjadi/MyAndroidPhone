using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MyAndroidPhone.Infrastructure.Storage;

/// <summary>Reads and writes JSON state files safely (atomic replace, corrupt files quarantined).</summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Returns the stored value, or a new default instance when the file is missing or unreadable.
    /// A corrupt file is renamed (never deleted) so the user's data can be recovered by hand.
    /// </summary>
    public static T ReadOrDefault<T>(string path, ILogger? logger = null) where T : class, new()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new T();
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "State file {Path} is corrupt; starting from defaults.", path);
            Quarantine(path, logger);
            return new T();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "State file {Path} could not be read; starting from defaults.", path);
            return new T();
        }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken) where T : class
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static T Clone<T>(T value) where T : class, new()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? new T();
    }

    private static void Quarantine(string path, ILogger? logger)
    {
        try
        {
            var target = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not quarantine corrupt state file {Path}.", path);
        }
    }
}
