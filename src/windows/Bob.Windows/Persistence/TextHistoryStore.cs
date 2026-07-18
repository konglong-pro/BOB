using System.Text.Json;

namespace Bob.Windows.Persistence;

public sealed record TextHistoryRecord(
    Guid TextId,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset RecordedAt,
    bool Outgoing,
    string Status,
    string PeerName);

public sealed record TextHistorySaveResult(
    TextHistoryRecord Record,
    bool IsNew);

public sealed class TextHistoryStore
{
    private const string HistoryFileName = "text-messages-v1.json";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _historyPath;
    private List<TextHistoryRecord> _records;

    public TextHistoryStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _historyPath = Path.Combine(
            Path.GetFullPath(paths.RootDirectory),
            HistoryFileName);
        _records = Load();
    }

    public IReadOnlyList<TextHistoryRecord> ReadAll()
    {
        lock (_sync)
        {
            return _records.ToArray();
        }
    }

    public IReadOnlyList<TextHistoryRecord> ReadRecent(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_sync)
        {
            return _records
                .TakeLast(limit)
                .ToArray();
        }
    }

    public TextHistorySaveResult Save(TextHistoryRecord record)
    {
        Validate(record);
        lock (_sync)
        {
            var existing = _records.FirstOrDefault(item => item.TextId == record.TextId);
            if (existing is not null)
            {
                if (existing.Text != record.Text
                    || existing.CreatedAt != record.CreatedAt
                    || existing.Outgoing != record.Outgoing)
                {
                    throw new TextHistoryConflictException(record.TextId);
                }

                return new TextHistorySaveResult(existing, IsNew: false);
            }

            var next = Order(_records.Append(record));
            Persist(next);
            _records = next;
            return new TextHistorySaveResult(record, IsNew: true);
        }
    }

    public bool UpdateStatus(Guid textId, string status)
    {
        if (textId == Guid.Empty)
        {
            throw new ArgumentException("Text ID cannot be empty.", nameof(textId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        lock (_sync)
        {
            var index = _records.FindIndex(record => record.TextId == textId);
            if (index < 0)
            {
                return false;
            }

            var current = _records[index];
            if (current.Status == status)
            {
                return true;
            }

            var next = new List<TextHistoryRecord>(_records);
            next[index] = current with { Status = status };
            Persist(next);
            _records = next;
            return true;
        }
    }

    private List<TextHistoryRecord> Load()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<HistoryDocument>(
                File.ReadAllText(_historyPath),
                JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion)
            {
                return [];
            }

            var records = document.Messages ?? [];
            foreach (var record in records)
            {
                Validate(record);
            }

            if (records.Select(record => record.TextId).Distinct().Count() != records.Count)
            {
                return [];
            }

            return Order(records);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException)
        {
            return [];
        }
    }

    private void Persist(IReadOnlyList<TextHistoryRecord> records)
    {
        var directory = Path.GetDirectoryName(_historyPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{HistoryFileName}.{Guid.NewGuid():N}.tmp");

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
                    new HistoryDocument(SchemaVersion, records),
                    JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            throw new TextHistoryStoreException(
                "Unable to persist the text history.",
                exception);
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

    private static List<TextHistoryRecord> Order(IEnumerable<TextHistoryRecord> records) =>
        records
            .OrderBy(record => record.RecordedAt)
            .ThenBy(record => record.CreatedAt)
            .ThenBy(record => record.TextId)
            .ToList();

    private static void Validate(TextHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.TextId == Guid.Empty)
        {
            throw new ArgumentException("Text ID cannot be empty.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.Text))
        {
            throw new ArgumentException("Text cannot be blank.", nameof(record));
        }

        if (record.CreatedAt == default || record.RecordedAt == default)
        {
            throw new ArgumentException("Text timestamps cannot be empty.", nameof(record));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(record.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.PeerName);
    }

    private sealed record HistoryDocument(
        int SchemaVersion,
        IReadOnlyList<TextHistoryRecord>? Messages);
}

public class TextHistoryStoreException(
    string message,
    Exception? innerException = null) : IOException(message, innerException);

public sealed class TextHistoryConflictException(Guid textId) :
    TextHistoryStoreException($"Text ID {textId} conflicts with saved history.");
