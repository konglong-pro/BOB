using Bob.Windows.Domain;
using Bob.Windows.Storage;
using System.Net.Http.Headers;

namespace Bob.Windows.Transport;

public sealed class TransferCoordinator
{
    private static readonly TimeSpan AcceptedCrossChannelTimeout = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TransferEntry> _entries = new();
    private readonly Queue<Guid> _outgoingQueue = new();
    private readonly ReceiveDirectorySettings? _receiveDirectorySettings;
    private readonly string? _fixedReceiveDirectory;

    public TransferCoordinator()
        : this(ReceiveDirectorySettings.GetDefaultReceiveDirectory())
    {
    }

    public TransferCoordinator(ReceiveDirectorySettings receiveDirectorySettings)
    {
        ArgumentNullException.ThrowIfNull(receiveDirectorySettings);
        _receiveDirectorySettings = receiveDirectorySettings;
    }

    public TransferCoordinator(string receiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveDirectory);
        _fixedReceiveDirectory = Path.GetFullPath(receiveDirectory);
    }

    public event EventHandler<TransferSnapshot>? TransferChanged;

    public string ReceiveDirectory => GetCurrentReceiveDirectory();

    public IncomingOfferDecision RegisterIncomingOffer(TransferOffer offer)
    {
        TransferSnapshot? snapshot = null;
        IncomingOfferDecision decision;

        lock (_sync)
        {
            ValidateOffer(offer);
            if (_entries.TryGetValue(offer.TransferId, out var existing))
            {
                if (existing.Direction != TransferDirection.AndroidToWindows
                    || existing.Offer != offer)
                {
                    throw ProtocolError(
                        "invalid_metadata",
                        "The transferId was already used with different metadata.",
                        retryable: false,
                        offer.TransferId);
                }

                decision = existing.State switch
                {
                    TransferState.Completed => new IncomingOfferDecision(
                        IncomingOfferReply.Completed,
                        Completed: existing.Completed),
                    TransferState.Failed => new IncomingOfferDecision(
                        IncomingOfferReply.Failed,
                        Failure: existing.Failure),
                    _ => new IncomingOfferDecision(
                        IncomingOfferReply.Accepted,
                        existing.PlannedName)
                };
                return decision;
            }

            if (HasActiveDirectionLocked(TransferDirection.AndroidToWindows))
            {
                var busy = CreateEntry(offer, TransferDirection.AndroidToWindows);
                var failure = SetFailureLocked(
                    busy,
                    "direction_busy",
                    "Another phone-to-computer transfer is active.",
                    retryable: true,
                    localTerminal: true);
                _entries.Add(offer.TransferId, busy);
                snapshot = CreateSnapshot(busy);
                decision = new IncomingOfferDecision(
                    IncomingOfferReply.Failed,
                    Failure: failure);
            }
            else
            {
                var entry = CreateEntry(offer, TransferDirection.AndroidToWindows);
                entry.ReceiveDirectory = GetCurrentReceiveDirectory();
                try
                {
                    Directory.CreateDirectory(entry.ReceiveDirectory);
                    entry.PlannedName = TransferFileNames.ChooseAvailableName(
                        entry.ReceiveDirectory,
                        TransferFileNames.Sanitize(offer.Name));
                    entry.PartPath = Path.Combine(
                        entry.ReceiveDirectory,
                        $".bob-{offer.TransferId:D}.part");
                    using (new FileStream(
                        entry.PartPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                    }

                    entry.State = TransferState.Accepted;
                    _entries.Add(offer.TransferId, entry);
                    snapshot = CreateSnapshot(entry);
                    decision = new IncomingOfferDecision(
                        IncomingOfferReply.Accepted,
                        entry.PlannedName);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryDelete(entry.PartPath);
                    var failure = SetFailureLocked(
                        entry,
                        exception is UnauthorizedAccessException ? "permission_denied" : "io_error",
                        "Windows could not prepare the receive file.",
                        retryable: true,
                        localTerminal: true);
                    _entries.Add(offer.TransferId, entry);
                    snapshot = CreateSnapshot(entry);
                    decision = new IncomingOfferDecision(
                        IncomingOfferReply.Failed,
                        Failure: failure);
                }
            }
        }

        Publish(snapshot);
        return decision;
    }

    public TransferOffer EnqueueOutgoing(string sourcePath, TransferKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The selected source file no longer exists.", fullPath);
        }

        var offer = new TransferOffer(
            Guid.NewGuid(),
            RetryOf: null,
            kind,
            TransferFileNames.Sanitize(info.Name),
            info.Length,
            TransferFileNames.GuessMediaType(fullPath, kind),
            DateTimeOffset.UtcNow);
        TransferSnapshot snapshot;

        lock (_sync)
        {
            var entry = CreateEntry(offer, TransferDirection.WindowsToAndroid);
            entry.SourcePath = fullPath;
            _entries.Add(offer.TransferId, entry);
            _outgoingQueue.Enqueue(offer.TransferId);
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
        return offer;
    }

    public TransferOffer? TryOfferNextOutgoing()
    {
        TransferSnapshot? snapshot = null;
        TransferOffer? offer = null;

        lock (_sync)
        {
            if (HasActiveDirectionLocked(TransferDirection.WindowsToAndroid))
            {
                return null;
            }

            while (_outgoingQueue.Count > 0)
            {
                var transferId = _outgoingQueue.Dequeue();
                if (!_entries.TryGetValue(transferId, out var entry)
                    || entry.State != TransferState.Queued)
                {
                    continue;
                }

                entry.State = TransferState.Offered;
                snapshot = CreateSnapshot(entry);
                offer = entry.Offer;
                break;
            }
        }

        Publish(snapshot);
        return offer;
    }

    public void AcceptOutgoing(Guid transferId, string plannedName)
    {
        if (string.IsNullOrWhiteSpace(plannedName))
        {
            throw ProtocolError(
                "invalid_metadata",
                "transfer.accepted plannedName is required.",
                retryable: false,
                transferId);
        }

        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (entry.State is TransferState.Accepted
                or TransferState.Transferring
                or TransferState.Verifying)
            {
                if (!string.Equals(entry.PlannedName, plannedName, StringComparison.Ordinal))
                {
                    throw ProtocolError(
                        "invalid_metadata",
                        "A duplicate accepted message changed plannedName.",
                        retryable: false,
                        transferId);
                }

                return;
            }

            EnsureState(entry, TransferState.Offered);
            entry.PlannedName = plannedName;
            entry.State = TransferState.Accepted;
            entry.AcceptedSignal.TrySetResult();
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    public IncomingContent BeginIncomingContent(Guid transferId, long? contentLength)
    {
        TransferSnapshot? snapshot = null;
        IncomingContent? content = null;
        TransferProtocolException? error = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.AndroidToWindows);
            EnsureState(entry, TransferState.Accepted);
            if (entry.Offer.Size is not null && contentLength != entry.Offer.Size)
            {
                throw ProtocolError(
                    "size_mismatch",
                    "Content-Length does not match the accepted offer.",
                    retryable: true,
                    transferId);
            }

            try
            {
                var stream = new FileStream(
                    entry.PartPath!,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);
                stream.SetLength(0);
                entry.State = TransferState.Transferring;
                entry.BytesTransferred = 0;
                snapshot = CreateSnapshot(entry);
                content = new IncomingContent(stream, entry.Offer.Size);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetFailureLocked(
                    entry,
                    exception is UnauthorizedAccessException ? "permission_denied" : "io_error",
                    "Windows could not open the temporary receive file.",
                    retryable: true,
                    localTerminal: true);
                TryDelete(entry.PartPath);
                snapshot = CreateSnapshot(entry);
                error = ProtocolError(
                    entry.Failure!.Code,
                    entry.Failure.Message,
                    entry.Failure.Retryable,
                    transferId);
            }
        }

        Publish(snapshot);
        if (error is not null)
        {
            throw error;
        }

        return content!;
    }

    public void CompleteIncomingContent(Guid transferId, long bytes, string sha256)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.AndroidToWindows);
            EnsureState(entry, TransferState.Transferring);
            if (entry.Offer.Size is not null && entry.Offer.Size != bytes)
            {
                SetFailureLocked(
                    entry,
                    "size_mismatch",
                    "The received byte count does not match the offer.",
                    retryable: true,
                    localTerminal: true);
                TryDelete(entry.PartPath);
                snapshot = CreateSnapshot(entry);
            }
            else
            {
                entry.BytesTransferred = bytes;
                entry.LocalSha256 = sha256;
                entry.State = TransferState.Verifying;
                snapshot = CreateSnapshot(entry);
            }
        }

        Publish(snapshot);
        if (snapshot.State == TransferState.Failed)
        {
            throw ProtocolError(
                snapshot.ErrorCode!,
                snapshot.ErrorMessage!,
                retryable: true,
                transferId);
        }
    }

    public OutgoingContent BeginOutgoingContent(Guid transferId)
    {
        TransferSnapshot? snapshot = null;
        OutgoingContent? content = null;
        TransferProtocolException? error = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            EnsureState(entry, TransferState.Accepted);

            try
            {
                var source = new FileInfo(entry.SourcePath!);
                if (!source.Exists)
                {
                    throw new FileNotFoundException();
                }

                if (entry.Offer.Size != source.Length)
                {
                    SetFailureLocked(
                        entry,
                        "size_mismatch",
                        "The source file changed after it was offered.",
                        retryable: true,
                        localTerminal: true);
                    snapshot = CreateSnapshot(entry);
                    error = ProtocolError(
                        "size_mismatch",
                        entry.Failure!.Message,
                        retryable: true,
                        transferId);
                }
                else
                {
                    var stream = new FileStream(
                        source.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 128 * 1024,
                        useAsync: true);
                    entry.State = TransferState.Transferring;
                    entry.BytesTransferred = 0;
                    snapshot = CreateSnapshot(entry);
                    content = new OutgoingContent(
                        stream,
                        entry.Offer.Name,
                        entry.Offer.MediaType,
                        source.Length);
                }
            }
            catch (TransferProtocolException exception)
            {
                error = exception;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var code = exception is UnauthorizedAccessException
                    ? "permission_denied"
                    : "source_missing";
                SetFailureLocked(
                    entry,
                    code,
                    "The selected source file cannot be opened.",
                    retryable: true,
                    localTerminal: true);
                snapshot = CreateSnapshot(entry);
                error = ProtocolError(
                    code,
                    entry.Failure!.Message,
                    retryable: true,
                    transferId);
            }
        }

        Publish(snapshot);
        if (error is not null)
        {
            throw error;
        }

        return content!;
    }

    public async Task<OutgoingContent> BeginOutgoingContentAsync(
        Guid transferId,
        CancellationToken cancellationToken)
    {
        Task? acceptedSignal = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (entry.State == TransferState.Offered)
            {
                acceptedSignal = entry.AcceptedSignal.Task;
            }
            else if (entry.State != TransferState.Accepted)
            {
                EnsureState(entry, TransferState.Accepted);
            }
        }

        if (acceptedSignal is not null)
        {
            try
            {
                await acceptedSignal.WaitAsync(
                    AcceptedCrossChannelTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw ProtocolError(
                    "invalid_state",
                    "Transfer has not been accepted.",
                    retryable: false,
                    transferId);
            }
        }

        return BeginOutgoingContent(transferId);
    }

    public TransferDigest CompleteOutgoingContent(Guid transferId, long bytes, string sha256)
    {
        TransferSnapshot snapshot;
        TransferDigest? digest = null;
        TransferProtocolException? error = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            EnsureState(entry, TransferState.Transferring);
            if (entry.Offer.Size != bytes)
            {
                SetFailureLocked(
                    entry,
                    "size_mismatch",
                    "The source byte count changed during transfer.",
                    retryable: true,
                    localTerminal: true);
                snapshot = CreateSnapshot(entry);
                error = ProtocolError(
                    "size_mismatch",
                    entry.Failure!.Message,
                    retryable: true,
                    transferId);
            }
            else
            {
                entry.BytesTransferred = bytes;
                entry.LocalSha256 = sha256;
                entry.State = TransferState.Verifying;
                snapshot = CreateSnapshot(entry);
                digest = new TransferDigest(transferId, "sha-256", sha256, bytes);
            }
        }

        Publish(snapshot);
        if (error is not null)
        {
            throw error;
        }

        return digest!;
    }

    public IncomingDigestDecision ApplyIncomingDigest(TransferDigest digest)
    {
        TransferSnapshot? snapshot = null;
        IncomingDigestDecision decision;
        lock (_sync)
        {
            var entry = GetEntryLocked(digest.TransferId);
            EnsureDirection(entry, TransferDirection.AndroidToWindows);
            if (entry.RemoteDigest is not null && entry.RemoteDigest != digest)
            {
                throw ProtocolError(
                    "invalid_metadata",
                    "A duplicate digest changed immutable fields.",
                    retryable: false,
                    digest.TransferId);
            }

            if (entry.State == TransferState.Completed)
            {
                return new IncomingDigestDecision(Completed: entry.Completed);
            }

            if (entry.State == TransferState.Failed)
            {
                return new IncomingDigestDecision(Failure: entry.Failure);
            }

            EnsureState(entry, TransferState.Verifying);
            entry.RemoteDigest = digest;

            string? failureCode = null;
            string? failureMessage = null;
            if (!string.Equals(digest.Algorithm, "sha-256", StringComparison.Ordinal))
            {
                failureCode = "invalid_metadata";
                failureMessage = "Only sha-256 transfer digests are supported.";
            }
            else if (digest.Bytes != entry.BytesTransferred
                || (entry.Offer.Size is not null && digest.Bytes != entry.Offer.Size))
            {
                failureCode = "size_mismatch";
                failureMessage = "The digest byte count does not match the received content.";
            }
            else if (!string.Equals(digest.Value, entry.LocalSha256, StringComparison.Ordinal))
            {
                failureCode = "checksum_mismatch";
                failureMessage = "SHA-256 values differ.";
            }

            if (failureCode is not null)
            {
                var failure = SetFailureLocked(
                    entry,
                    failureCode,
                    failureMessage!,
                    retryable: failureCode != "invalid_metadata",
                    localTerminal: true);
                TryDelete(entry.PartPath);
                snapshot = CreateSnapshot(entry);
                decision = new IncomingDigestDecision(Failure: failure);
            }
            else
            {
                try
                {
                    var storedName = PublishPartLocked(entry);
                    var completed = new TransferCompleted(
                        entry.Offer.TransferId,
                        entry.BytesTransferred,
                        entry.LocalSha256!,
                        storedName,
                        DateTimeOffset.UtcNow);
                    entry.Completed = completed;
                    entry.StoredName = storedName;
                    entry.State = TransferState.Completed;
                    entry.TerminalAckPending = true;
                    snapshot = CreateSnapshot(entry);
                    decision = new IncomingDigestDecision(Completed: completed);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var failure = SetFailureLocked(
                        entry,
                        exception is UnauthorizedAccessException ? "permission_denied" : "io_error",
                        "Windows could not publish the verified file.",
                        retryable: true,
                        localTerminal: true);
                    TryDelete(entry.PartPath);
                    snapshot = CreateSnapshot(entry);
                    decision = new IncomingDigestDecision(Failure: failure);
                }
            }
        }

        Publish(snapshot);
        return decision;
    }

    public void ApplyRemoteCompleted(TransferCompleted completed)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(completed.TransferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (entry.State == TransferState.Completed)
            {
                if (entry.Completed != completed)
                {
                    throw ProtocolError(
                        "invalid_metadata",
                        "A duplicate completed message changed immutable fields.",
                        retryable: false,
                        completed.TransferId);
                }

                return;
            }

            EnsureState(entry, TransferState.Verifying);
            if (completed.Bytes != entry.BytesTransferred
                || completed.Bytes != entry.Offer.Size
                || !string.Equals(completed.Sha256, entry.LocalSha256, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(completed.StoredName))
            {
                throw ProtocolError(
                    "invalid_metadata",
                    "transfer.completed does not match the sent content.",
                    retryable: false,
                    completed.TransferId);
            }

            entry.Completed = completed;
            entry.StoredName = completed.StoredName;
            entry.State = TransferState.Completed;
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    public void ApplyRemoteFailure(TransferFailure failure)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(failure.TransferId);
            if (entry.State == TransferState.Failed)
            {
                if (entry.Failure != failure)
                {
                    throw ProtocolError(
                        "invalid_metadata",
                        "A duplicate failed message changed immutable fields.",
                        retryable: false,
                        failure.TransferId);
                }

                return;
            }

            if (IsTerminal(entry.State))
            {
                throw ProtocolError(
                    "invalid_state",
                    "The transfer is already terminal.",
                    retryable: false,
                    failure.TransferId);
            }

            entry.State = TransferState.Failed;
            entry.Failure = failure;
            entry.TerminalAckPending = false;
            TryDelete(entry.PartPath);
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    public TransferFailure FailLocal(
        Guid transferId,
        string code,
        string message,
        bool retryable)
    {
        TransferSnapshot snapshot;
        TransferFailure failure;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (entry.State == TransferState.Completed)
            {
                throw ProtocolError(
                    "invalid_state",
                    "A completed transfer cannot fail.",
                    retryable: false,
                    transferId);
            }

            if (entry.State == TransferState.Failed)
            {
                TryDelete(entry.PartPath);
                return entry.Failure!;
            }

            failure = SetFailureLocked(
                entry,
                code,
                message,
                retryable,
                localTerminal: true);
            TryDelete(entry.PartPath);
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
        return failure;
    }

    public void AcknowledgeTerminal(Guid transferId, TransferState state)
    {
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (entry.State != state || !IsTerminal(state))
            {
                throw ProtocolError(
                    "invalid_state",
                    "terminalAck does not match the stored terminal state.",
                    retryable: false,
                    transferId);
            }

            entry.TerminalAckPending = false;
        }
    }

    public void ReportProgress(Guid transferId, long bytes)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (entry.State != TransferState.Transferring || bytes < entry.BytesTransferred)
            {
                return;
            }

            entry.BytesTransferred = bytes;
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    public void ApplyRemoteProgress(Guid transferId, long bytes, long? total)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (entry.State is not (TransferState.Transferring or TransferState.Verifying))
            {
                throw ProtocolError(
                    "invalid_state",
                    "Progress is only valid while content is transferring or verifying.",
                    retryable: false,
                    transferId);
            }

            if (total != entry.Offer.Size)
            {
                throw ProtocolError(
                    "invalid_metadata",
                    "transfer.progress total does not match the offered size.",
                    retryable: false,
                    transferId);
            }

            entry.BytesTransferred = Math.Max(entry.BytesTransferred, bytes);
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    private static void ValidateOffer(TransferOffer offer)
    {
        if (offer.TransferId == Guid.Empty
            || offer.RetryOf == Guid.Empty
            || string.IsNullOrWhiteSpace(offer.Name)
            || offer.Size < 0
            || string.IsNullOrWhiteSpace(offer.MediaType)
            || !MediaTypeHeaderValue.TryParse(offer.MediaType, out _)
            || offer.CreatedAt == default
            || offer.CreatedAt.Offset != TimeSpan.Zero)
        {
            throw ProtocolError(
                "invalid_metadata",
                "transfer.offer contains invalid metadata.",
                retryable: false,
                offer.TransferId == Guid.Empty ? null : offer.TransferId);
        }
    }

    private string PublishPartLocked(TransferEntry entry)
    {
        var receiveDirectory = entry.ReceiveDirectory
            ?? throw new InvalidOperationException(
                "The incoming transfer does not have a frozen receive directory.");
        while (true)
        {
            var storedName = TransferFileNames.ChooseAvailableName(
                receiveDirectory,
                entry.PlannedName!);
            var destination = TransferFileNames.GetPathInsideRoot(
                receiveDirectory,
                storedName);
            try
            {
                File.Move(entry.PartPath!, destination, overwrite: false);
                return storedName;
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                // A concurrent external writer won the name; choose the next suffix.
            }
        }
    }

    private static TransferEntry CreateEntry(
        TransferOffer offer,
        TransferDirection direction) =>
        new(offer, direction);

    private TransferEntry GetEntryLocked(Guid transferId)
    {
        if (transferId == Guid.Empty || !_entries.TryGetValue(transferId, out var entry))
        {
            throw ProtocolError(
                "transfer_not_found",
                "The transferId is unknown.",
                retryable: false,
                transferId == Guid.Empty ? null : transferId);
        }

        return entry;
    }

    private bool HasActiveDirectionLocked(TransferDirection direction) =>
        _entries.Values.Any(entry =>
            entry.Direction == direction
            && entry.State is TransferState.Offered
                or TransferState.Accepted
                or TransferState.Transferring
                or TransferState.Verifying);

    private static void EnsureDirection(TransferEntry entry, TransferDirection expected)
    {
        if (entry.Direction != expected)
        {
            throw ProtocolError(
                "wrong_direction",
                "The transfer content endpoint was used in the wrong direction.",
                retryable: false,
                entry.Offer.TransferId);
        }
    }

    private static void EnsureState(TransferEntry entry, TransferState expected)
    {
        if (entry.State != expected)
        {
            throw ProtocolError(
                "invalid_state",
                $"Transfer must be {expected.ToString().ToLowerInvariant()}.",
                retryable: false,
                entry.Offer.TransferId);
        }
    }

    private static TransferFailure SetFailureLocked(
        TransferEntry entry,
        string code,
        string message,
        bool retryable,
        bool localTerminal)
    {
        var failure = new TransferFailure(
            entry.Offer.TransferId,
            code,
            message,
            retryable);
        entry.State = TransferState.Failed;
        entry.Failure = failure;
        entry.TerminalAckPending = localTerminal;
        return failure;
    }

    private static TransferSnapshot CreateSnapshot(TransferEntry entry) =>
        new(
            entry.Offer.TransferId,
            ++entry.Revision,
            entry.Direction,
            entry.Offer.Kind,
            entry.Offer.Name,
            entry.Offer.Size,
            entry.Offer.MediaType,
            entry.Offer.CreatedAt,
            entry.State,
            entry.BytesTransferred,
            entry.PlannedName,
            entry.StoredName,
            entry.Failure?.Code,
            entry.Failure?.Message,
            GetLocalPath(entry));

    private static string? GetLocalPath(TransferEntry entry)
    {
        if (entry.Direction == TransferDirection.WindowsToAndroid)
        {
            return entry.SourcePath;
        }

        if (entry.State != TransferState.Completed
            || string.IsNullOrWhiteSpace(entry.ReceiveDirectory)
            || string.IsNullOrWhiteSpace(entry.StoredName))
        {
            return null;
        }

        return TransferFileNames.GetPathInsideRoot(
            entry.ReceiveDirectory,
            entry.StoredName);
    }

    private string GetCurrentReceiveDirectory() =>
        _receiveDirectorySettings?.ReceiveDirectory ?? _fixedReceiveDirectory!;

    private void Publish(TransferSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            TransferChanged?.Invoke(this, snapshot);
        }
    }

    private static bool IsTerminal(TransferState state) =>
        state is TransferState.Completed or TransferState.Failed or TransferState.Canceled;

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static TransferProtocolException ProtocolError(
        string code,
        string message,
        bool retryable,
        Guid? transferId) =>
        new(code, message, retryable, transferId);

    private sealed class TransferEntry
    {
        public TransferEntry(TransferOffer offer, TransferDirection direction)
        {
            Offer = offer;
            Direction = direction;
        }

        public TransferOffer Offer { get; }

        public TransferDirection Direction { get; }

        public TransferState State { get; set; } = TransferState.Queued;

        public long Revision { get; set; }

        public string? SourcePath { get; set; }

        public string? ReceiveDirectory { get; set; }

        public string? PartPath { get; set; }

        public string? PlannedName { get; set; }

        public string? StoredName { get; set; }

        public long BytesTransferred { get; set; }

        public string? LocalSha256 { get; set; }

        public TransferDigest? RemoteDigest { get; set; }

        public TransferCompleted? Completed { get; set; }

        public TransferFailure? Failure { get; set; }

        public bool TerminalAckPending { get; set; }

        public TaskCompletionSource AcceptedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
