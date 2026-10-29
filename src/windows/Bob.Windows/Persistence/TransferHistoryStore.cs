using System.Text.Json;
using Bob.Windows.Domain;

namespace Bob.Windows.Persistence;

/// <summary>
/// User-visible transfer history. This archive does not restore live protocol state or queues.
/// </summary>
public sealed class TransferHistoryStore
{
    private const string HistoryFileName = "transfer-history-v1.json";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _historyPath;
    private readonly Dictionary<Guid, TransferSnapshot> _records;

    public string? ReadWarning { get; private set; }

    public TransferHistoryStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _historyPath = Path.Combine(Path.GetFullPath(paths.RootDirectory), HistoryFileName);
        _records = Load();
    }

    public IReadOnlyList<TransferSnapshot> ReadAll()
    {
        lock (_sync)
        {
            return Order(_records.Values).ToArray();
        }
    }

    public void Save(TransferSnapshot snapshot)
    {
        if (ReadWarning is not null)
            throw new TransferHistoryStoreException(ReadWarning);
        Validate(snapshot);
        snapshot = SanitizeLocalPath(snapshot);
        lock (_sync)
        {
            if (_records.TryGetValue(snapshot.TransferId, out var existing))
            {
                if (snapshot.Revision <= existing.Revision)
                {
                    return;
                }

                // Progress remains live in the coordinator. Avoid rewriting the full archive
                // on every byte sample; state transitions persist the last sampled byte count.
                if (snapshot.State == existing.State
                    && snapshot.LocalPath == existing.LocalPath
                    && snapshot.StoredName == existing.StoredName
                    && snapshot.ErrorCode == existing.ErrorCode
                    && snapshot.ErrorMessage == existing.ErrorMessage)
                {
                    return;
                }
            }

            var next = new Dictionary<Guid, TransferSnapshot>(_records)
            {
                [snapshot.TransferId] = snapshot
            };
            Persist(Order(next.Values).ToArray());
            _records[snapshot.TransferId] = snapshot;
        }
    }

    private Dictionary<Guid, TransferSnapshot> Load()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<HistoryDocument>(
                File.ReadAllText(_historyPath), JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion || document.Transfers is null)
            {
                throw new JsonException("Unsupported transfer archive schema.");
            }

            var result = new Dictionary<Guid, TransferSnapshot>();
            foreach (var saved in document.Transfers)
            {
                Validate(saved);
                var snapshot = SanitizeLocalPath(saved);
                if (snapshot.State is not (TransferState.Completed or TransferState.Failed or TransferState.Canceled))
                {
                    snapshot = snapshot with
                    {
                        State = TransferState.Failed,
                        ErrorCode = "network_interrupted",
                        ErrorMessage = "Interrupted when BOB stopped. Select the content again to resend."
                    };
                    snapshot = SanitizeLocalPath(snapshot);
                }

                if (!result.TryAdd(snapshot.TransferId, snapshot))
                {
                    throw new JsonException("Duplicate transfer archive ID.");
                }
            }

            return result;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException)
        {
            ReadWarning = "Transfer history could not be read. The saved archive has been kept.";
            return [];
        }
    }

    private void Persist(IReadOnlyList<TransferSnapshot> records)
    {
        var directory = Path.GetDirectoryName(_historyPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{HistoryFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new HistoryDocument(SchemaVersion, records), JsonOptions);
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
            throw new TransferHistoryStoreException("Unable to persist the transfer history.", exception);
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

    private static IEnumerable<TransferSnapshot> Order(IEnumerable<TransferSnapshot> snapshots) =>
        snapshots.OrderBy(item => item.RecordedAt).ThenBy(item => item.TransferId);

    private static TransferSnapshot SanitizeLocalPath(TransferSnapshot snapshot)
    {
        if (snapshot.Direction == TransferDirection.AndroidToWindows
            && snapshot.State != TransferState.Completed)
        {
            return snapshot with { LocalPath = null };
        }

        if (snapshot.LocalPath is not null
            && (!Path.IsPathFullyQualified(snapshot.LocalPath)
                || Path.GetFileName(snapshot.LocalPath).StartsWith(".bob-", StringComparison.OrdinalIgnoreCase)
                    && snapshot.LocalPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
        {
            return snapshot with { LocalPath = null };
        }

        return snapshot;
    }

    private static void Validate(TransferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TransferId == Guid.Empty
            || snapshot.Revision < 0
            || snapshot.CreatedAt == default
            || snapshot.RecordedAt == default
            || snapshot.BytesTransferred < 0
            || snapshot.Size < 0
            || !Enum.IsDefined(snapshot.State)
            || !Enum.IsDefined(snapshot.Direction)
            || !Enum.IsDefined(snapshot.Kind))
        {
            throw new ArgumentException("Transfer history contains invalid metadata.", nameof(snapshot));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.MediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.PeerName);
    }

    private sealed record HistoryDocument(int SchemaVersion, IReadOnlyList<TransferSnapshot>? Transfers);
}

public sealed class TransferHistoryStoreException(string message, Exception? innerException = null)
    : IOException(message, innerException);
