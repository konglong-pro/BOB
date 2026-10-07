using System.Text.Json;
using Bob.Windows.Domain;

namespace Bob.Windows.Persistence;

// Only metadata and reconciliation state are stored here. File bodies stay in their
// original source or in the frozen receive directory.
internal sealed record TransferHistoryRecord(
    TransferOffer Offer,
    TransferDirection Direction,
    TransferState State,
    DateTimeOffset RecordedAt,
    string? SourcePath,
    string? ReceiveDirectory,
    string? PlannedName,
    string? StoredName,
    long BytesTransferred,
    string? LocalSha256,
    TransferDigest? RemoteDigest,
    TransferCompleted? Completed,
    TransferFailure? Failure,
    TransferCanceled? Canceled,
    DateTimeOffset? TerminalAt,
    DateTimeOffset? VerifyingSince,
    bool TerminalAckPending,
    bool CancelPending,
    bool PublishPending,
    bool? FailureIsLocal = null,
    TransferFailure? RemoteFailure = null);

internal sealed class TransferHistoryStore
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public TransferHistoryStore(AppPaths paths)
    {
        _path = Path.Combine(Path.GetFullPath(paths.RootDirectory), "file-transfers-v1.json");
    }

    public IReadOnlyList<TransferHistoryRecord> ReadAll()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize<HistoryDocument>(stream, Options);
            if (document is null || document.SchemaVersion != SchemaVersion
                || document.Transfers is null)
            {
                throw new JsonException("Unsupported transfer history.");
            }

            return document.Transfers;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            // An unreadable outbox must never silently become an empty queue.
            throw new TransferHistoryStoreException("Unable to read transfer history.", exception);
        }
    }

    public void Save(IReadOnlyList<TransferHistoryRecord> records)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = Path.Combine(directory, $".file-transfers-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new HistoryDocument(SchemaVersion, records), Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            throw new TransferHistoryStoreException("Unable to save transfer history.", exception);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record HistoryDocument(int SchemaVersion,
        IReadOnlyList<TransferHistoryRecord>? Transfers);
}

public sealed class TransferHistoryStoreException(string message, Exception? innerException = null)
    : IOException(message, innerException);
