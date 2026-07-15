using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bob.Windows.Storage;

public sealed class ReceiveDirectorySettings
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _settingsPath;
    private readonly string _defaultReceiveDirectory;
    private string _receiveDirectory;

    public ReceiveDirectorySettings(AppPaths paths)
        : this(paths, GetDefaultReceiveDirectory())
    {
    }

    internal ReceiveDirectorySettings(
        AppPaths paths,
        string defaultReceiveDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultReceiveDirectory);

        _settingsPath = Path.Combine(
            Path.GetFullPath(paths.RootDirectory),
            SettingsFileName);
        _defaultReceiveDirectory = Path.GetFullPath(defaultReceiveDirectory);
        _receiveDirectory = LoadReceiveDirectory() ?? _defaultReceiveDirectory;
    }

    public string ReceiveDirectory
    {
        get
        {
            lock (_sync)
            {
                return _receiveDirectory;
            }
        }
    }

    public void SetReceiveDirectory(string receiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveDirectory);
        var fullPath = Path.GetFullPath(receiveDirectory);

        lock (_sync)
        {
            Directory.CreateDirectory(fullPath);
            ValidateWritableDirectory(fullPath);
            Persist(fullPath);
            _receiveDirectory = fullPath;
        }
    }

    internal static string GetDefaultReceiveDirectory()
    {
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "Could not locate the current user's profile directory.");
        }

        return Path.Combine(userProfile, "Downloads", "BOB");
    }

    private string? LoadReceiveDirectory()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<SettingsDocument>(
                File.ReadAllText(_settingsPath),
                JsonOptions);
            if (string.IsNullOrWhiteSpace(document?.ReceiveDirectory)
                || !Path.IsPathFullyQualified(document.ReceiveDirectory))
            {
                return null;
            }

            return Path.GetFullPath(document.ReceiveDirectory);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static void ValidateWritableDirectory(string directory)
    {
        var probePath = Path.Combine(
            directory,
            $".bob-write-test-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                created = true;
                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            if (created && File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private void Persist(string receiveDirectory)
    {
        var settingsDirectory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(settingsDirectory);
        var temporaryPath = Path.Combine(
            settingsDirectory,
            $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new SettingsDocument(receiveDirectory),
                    JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record SettingsDocument(
        [property: JsonPropertyName("receiveDirectory")] string ReceiveDirectory);
}
