using Bob.Windows.Domain;
using Bob.Windows.Storage;
using Bob.Windows.Persistence;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Bob.Windows.Transport;

public sealed class TransferCoordinator
{
    public static readonly TimeSpan DigestRetentionPeriod = TimeSpan.FromHours(24);
    public static readonly TimeSpan OfferResponseTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CancellationConfirmationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AcceptedCrossChannelTimeout = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TransferEntry> _entries = new();
    private readonly Queue<Guid> _outgoingQueue = new();
    private readonly ReceiveDirectorySettings? _receiveDirectorySettings;
    private readonly string? _fixedReceiveDirectory;
    private readonly TransferHistoryStore? _history;

    public TransferCoordinator()
        : this(ReceiveDirectorySettings.GetDefaultReceiveDirectory())
    {
    }

    public TransferCoordinator(ReceiveDirectorySettings receiveDirectorySettings)
    {
        ArgumentNullException.ThrowIfNull(receiveDirectorySettings);
        _receiveDirectorySettings = receiveDirectorySettings;
    }

    public TransferCoordinator(ReceiveDirectorySettings receiveDirectorySettings, AppPaths paths)
        : this(receiveDirectorySettings)
    {
        _history = new TransferHistoryStore(paths);
        RestoreHistory();
    }

    public TransferCoordinator(string receiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveDirectory);
        _fixedReceiveDirectory = Path.GetFullPath(receiveDirectory);
    }

    public TransferCoordinator(string receiveDirectory, AppPaths paths)
        : this(receiveDirectory)
    {
        _history = new TransferHistoryStore(paths);
        RestoreHistory();
    }

    public event EventHandler<TransferSnapshot>? TransferChanged;

    internal event EventHandler? CancellationConfirmationExpired;

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

                decision = existing.CancelPending
                    ? new IncomingOfferDecision(IncomingOfferReply.Canceled, Canceled: existing.Canceled)
                    : existing.State switch
                {
                    TransferState.Completed => new IncomingOfferDecision(
                        IncomingOfferReply.Completed,
                        Completed: existing.Completed),
                    TransferState.Failed => new IncomingOfferDecision(
                        IncomingOfferReply.Failed,
                        Failure: existing.Failure),
                    TransferState.Canceled => new IncomingOfferDecision(
                        IncomingOfferReply.Canceled,
                        Canceled: existing.Canceled),
                    _ => new IncomingOfferDecision(
                        IncomingOfferReply.Accepted,
                        existing.PlannedName)
                };
                PersistLocked();
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
                    entry.StateSince = DateTimeOffset.UtcNow;
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

        try { Publish(snapshot); }
        catch (TransferHistoryStoreException)
        {
            lock (_sync)
            {
                if (_entries.Remove(offer.TransferId, out var entry)) { TryDelete(entry.PartPath); }
            }
            throw;
        }
        return decision;
    }

    public TransferOffer EnqueueOutgoing(string sourcePath, TransferKind kind, Guid? retryOf = null)
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
            retryOf,
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

        try { Publish(snapshot); }
        catch (TransferHistoryStoreException)
        {
            lock (_sync)
            {
                _entries.Remove(offer.TransferId);
                RebuildOutgoingQueueLocked();
            }
            throw;
        }
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
                entry.StateSince = DateTimeOffset.UtcNow;
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
            entry.StateSince = DateTimeOffset.UtcNow;
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
            EnsureContentCanStartLocked(entry);
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
                entry.ActiveStream = new TransferContentStream(stream);
                entry.State = TransferState.Transferring;
                entry.BytesTransferred = 0;
                snapshot = CreateSnapshot(entry);
                content = new IncomingContent(entry.ActiveStream, entry.Offer.Size, entry.BodyCancellation.Token);
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

    public IncomingDigestDecision? CompleteIncomingContent(Guid transferId, long bytes, string sha256)
    {
        TransferSnapshot snapshot;
        TransferDigest? pendingDigest = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.AndroidToWindows);
            EnsureState(entry, TransferState.Transferring);
            entry.ActiveStream = null;
            if (entry.CancelPending && entry.Offer.Size is not null && entry.Offer.Size != bytes)
            {
                throw ProtocolError("invalid_state", "The cancellation is awaiting the peer's stored result.",
                    false, transferId);
            }
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
                entry.VerifyingSince = DateTimeOffset.UtcNow;
                pendingDigest = entry.RemoteDigest;
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

        return pendingDigest is null ? null : ApplyIncomingDigest(pendingDigest);
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
            EnsureContentCanStartLocked(entry);

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
                    if (stream.Length != entry.Offer.Size)
                    {
                        stream.Dispose();
                        SetFailureLocked(entry, "size_mismatch", "The source file changed before opening.",
                            true, localTerminal: true);
                        snapshot = CreateSnapshot(entry);
                        error = ProtocolError("size_mismatch", entry.Failure!.Message, true, transferId);
                    }
                    else
                    {
                    entry.State = TransferState.Transferring;
                    entry.ActiveStream = new TransferContentStream(stream);
                    entry.BytesTransferred = 0;
                    snapshot = CreateSnapshot(entry);
                    content = new OutgoingContent(
                        entry.ActiveStream,
                        entry.Offer.Name,
                        entry.Offer.MediaType,
                        stream.Length,
                        entry.BodyCancellation.Token);
                    }
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
            entry.ActiveStream = null;
            if (entry.CancelPending && entry.Offer.Size != bytes)
            {
                throw ProtocolError("invalid_state", "The cancellation is awaiting the peer's stored result.",
                    false, transferId);
            }
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
                entry.VerifyingSince = DateTimeOffset.UtcNow;
                entry.OutgoingBodyFinished.TrySetResult();
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

    internal void RecordOutgoingContentProof(Guid transferId, long bytes, string sha256)
    {
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (entry.State != TransferState.Transferring || bytes != entry.Offer.Size) { return; }
            // Save the complete source hash before the last bytes can become visible
            // to the receiver. A published completion can then win a concurrent cancel.
            entry.LocalSha256 = sha256;
            entry.OutgoingBodyFinished.TrySetResult();
            PersistLocked();
        }
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
                PersistLocked();
                return new IncomingDigestDecision(Completed: entry.Completed);
            }

            if (entry.State == TransferState.Failed)
            {
                PersistLocked();
                return new IncomingDigestDecision(Failure: entry.Failure);
            }

            if (entry.State == TransferState.Canceled)
            {
                PersistLocked();
                return new IncomingDigestDecision(Canceled: entry.Canceled);
            }

            if (entry.CancelPending)
            {
                // Keep the proof when HTTP/WSS completion crosses a local cancel,
                // but leave publication blocked until the peer's canonical result.
                entry.RemoteDigest = digest;
                PersistLocked();
                return new IncomingDigestDecision();
            }

            if (entry.State is TransferState.Accepted or TransferState.Transferring)
            {
                // HTTPS completion and WSS delivery are independent. Save the digest
                // now, and reconcile it only after the body is closed and flushed.
                entry.RemoteDigest = digest;
                PersistLocked();
                return new IncomingDigestDecision();
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
                    entry.TerminalAt = completed.CompletedAt;
                    entry.PublishPending = false;
                    entry.TerminalAckPending = true;
                    snapshot = CreateSnapshot(entry);
                    decision = new IncomingDigestDecision(Completed: completed);
                }
                catch (Exception exception) when (exception is not TransferHistoryStoreException
                    && exception is (IOException or UnauthorizedAccessException))
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

            PersistLocked();
        }

        Publish(snapshot, persist: false);
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

                PersistLocked();
                return;
            }

            if (entry.State == TransferState.Failed)
            {
                throw ProtocolError("invalid_state", "The transfer already has a failed terminal result.",
                    false, completed.TransferId);
            }
            var pendingBodyCompletion = entry.Direction == TransferDirection.WindowsToAndroid
                && entry.State == TransferState.Transferring && entry.LocalSha256 is not null;
            if (!pendingBodyCompletion && entry.State is not (TransferState.Verifying or TransferState.Canceled))
            {
                EnsureState(entry, TransferState.Verifying);
            }
            if ((!pendingBodyCompletion && completed.Bytes != entry.BytesTransferred)
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
            entry.BytesTransferred = completed.Bytes;
            entry.StoredName = completed.StoredName;
            entry.State = TransferState.Completed;
            entry.TerminalAt = completed.CompletedAt;
            entry.Failure = null;
            entry.Canceled = null;
            entry.CancelPending = false;
            entry.TerminalAckPending = false;
            StopContentLocked(entry);
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    internal async Task ApplyRemoteCompletedAsync(TransferCompleted completed, CancellationToken cancellationToken)
    {
        Task? proofReady = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(completed.TransferId);
            if (entry.CancelPending && entry.Direction == TransferDirection.WindowsToAndroid
                && entry.State == TransferState.Transferring && entry.LocalSha256 is null)
            {
                proofReady = entry.OutgoingBodyFinished.Task;
            }
        }
        if (proofReady is not null)
        {
            await proofReady.WaitAsync(CancellationConfirmationTimeout, cancellationToken).ConfigureAwait(false);
        }
        ApplyRemoteCompleted(completed);
    }

    public void ApplyRemoteFailure(TransferFailure failure)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(failure.TransferId);
            if (entry.State == TransferState.Failed)
            {
                // Both ends can independently fail the same interrupted HTTP body.
                // Keep the local terminal result immutable and remember the peer's
                // first result separately so later conflicting replays still fail.
                var previousRemoteFailure = entry.RemoteFailure
                    ?? (entry.FailureIsLocal ? null : entry.Failure);
                if (previousRemoteFailure is not null && previousRemoteFailure != failure)
                {
                    throw ProtocolError(
                        "invalid_metadata",
                        "A duplicate failed message changed immutable fields.",
                        retryable: false,
                        failure.TransferId);
                }

                entry.RemoteFailure ??= failure;
                PersistLocked();
                return;
            }

            if (IsTerminal(entry.State) && !(entry.State == TransferState.Canceled && entry.CancelPending))
            {
                throw ProtocolError(
                    "invalid_state",
                    "The transfer is already terminal.",
                    retryable: false,
                    failure.TransferId);
            }

            entry.State = TransferState.Failed;
            entry.Failure = failure;
            entry.FailureIsLocal = false;
            entry.RemoteFailure = failure;
            entry.Canceled = null;
            entry.CancelPending = false;
            entry.TerminalAt = DateTimeOffset.UtcNow;
            entry.TerminalAckPending = false;
            StopContentLocked(entry);
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
            if (entry.CancelPending)
            {
                throw ProtocolError("invalid_state", "The cancellation is awaiting the peer's stored result.",
                    false, transferId);
            }
            if (entry.State is TransferState.Completed or TransferState.Canceled)
            {
                throw ProtocolError(
                    "invalid_state",
                    "A completed or canceled transfer cannot fail.",
                    retryable: false,
                    transferId);
            }

            if (entry.State == TransferState.Failed)
            {
                TryDelete(entry.PartPath);
                PersistLocked();
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
            PersistLocked();
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

        Publish(snapshot, persist: false);
    }

    public void ApplyRemoteProgress(Guid transferId, long bytes, long? total)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (IsTerminal(entry.State))
            {
                // A late progress frame must not undo a terminal result.
                return;
            }

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

        Publish(snapshot, persist: false);
    }

    public IReadOnlyList<TransferSnapshot> ReadAllSnapshots()
    {
        lock (_sync)
        {
            return _entries.Values.OrderBy(entry => entry.RecordedAt)
                .Select(CreateSnapshot).ToArray();
        }
    }

    internal IReadOnlyList<object> ReadSessionSnapshotItems()
    {
        lock (_sync)
        {
            PersistLocked();
            return _entries.Values
                .Where(entry => !IsTerminal(entry.State) || entry.TerminalAckPending)
                .OrderBy(entry => entry.RecordedAt)
                .Select(entry => (object)new
                {
                    transferId = entry.Offer.TransferId,
                    direction = entry.Direction == TransferDirection.WindowsToAndroid
                        ? "server_to_client" : "client_to_server",
                    kind = TransferProtocolMessages.Kind(entry.Offer.Kind),
                    name = entry.Offer.Name,
                    size = entry.Offer.Size,
                    mediaType = entry.Offer.MediaType,
                    state = entry.State.ToString().ToLowerInvariant(),
                    bytesTransferred = entry.BytesTransferred,
                    createdAt = entry.Offer.CreatedAt,
                    retryOf = entry.Offer.RetryOf,
                    terminalAt = entry.TerminalAt,
                    terminalAckPending = entry.TerminalAckPending,
                    sha256 = entry.State == TransferState.Completed ? entry.LocalSha256 : null,
                    storedName = entry.State == TransferState.Completed ? entry.StoredName : null,
                    error = entry.Failure is null ? null : new
                    {
                        code = entry.Failure.Code,
                        message = entry.Failure.Message,
                        retryable = entry.Failure.Retryable
                    }
                }).ToArray();
        }
    }

    internal IReadOnlyList<ProtocolEnvelope> ReadReplayEnvelopes(bool includeDigests = true)
    {
        lock (_sync)
        {
            PersistLocked();
            var messages = new List<ProtocolEnvelope>();
            foreach (var entry in _entries.Values.OrderBy(entry => entry.RecordedAt))
            {
                if (entry.CancelPending)
                {
                    StartCancellationWaitLocked(entry);
                    messages.Add(TransferProtocolMessages.Cancel(entry.Offer.TransferId));
                }
                else if (entry.TerminalAckPending)
                {
                    messages.Add(TerminalEnvelopeLocked(entry));
                }
                else if (includeDigests && entry.Direction == TransferDirection.WindowsToAndroid
                    && entry.State == TransferState.Verifying)
                {
                    messages.Add(TransferProtocolMessages.Digest(new TransferDigest(
                        entry.Offer.TransferId, "sha-256", entry.LocalSha256!, entry.BytesTransferred)));
                }
            }

            return messages;
        }
    }

    internal ProtocolEnvelope? ReadTerminalOrCancelEnvelope(Guid transferId)
    {
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (!entry.CancelPending && !IsTerminal(entry.State)) { return null; }
            PersistLocked();
            return entry.CancelPending
                ? TransferProtocolMessages.Cancel(transferId)
                : TerminalEnvelopeLocked(entry);
        }
    }

    public TransferState Cancel(Guid transferId, bool requestedByPeer = false)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (entry.CancelPending && !IsTerminal(entry.State)
                && !(requestedByPeer && entry.Direction == TransferDirection.AndroidToWindows))
            {
                PersistLocked();
                return TransferState.Canceled;
            }
            if (IsTerminal(entry.State))
            {
                if (requestedByPeer && entry.State == TransferState.Canceled && entry.CancelPending
                    && entry.Direction == TransferDirection.AndroidToWindows)
                {
                    // If both ends canceled, the body receiver fixes the timestamp.
                    // The body sender keeps its pending request until this result arrives.
                    entry.CancelPending = false;
                    entry.TerminalAckPending = true;
                    StopContentLocked(entry);
                    TryDelete(entry.PartPath);
                    snapshot = CreateSnapshot(entry);
                }
                else
                {
                    PersistLocked();
                    return entry.State;
                }
            }
            else
            {
                entry.CancelPending = !requestedByPeer && entry.State != TransferState.Queued;
                if (entry.CancelPending)
                {
                    // Send cancel over WSS before either endpoint closes the HTTP body.
                    // An early close can make the peer persist Failed before seeing cancel.
                    entry.Canceled ??= new TransferCanceled(transferId, DateTimeOffset.UtcNow);
                    StartCancellationWaitLocked(entry);
                    snapshot = CreateSnapshot(entry);
                }
                else
                {
                    // During simultaneous cancellation, the receiver keeps the time of
                    // its first local request. It becomes terminal only at this point.
                    entry.Canceled ??= new TransferCanceled(transferId, DateTimeOffset.UtcNow);
                    entry.TerminalAt = entry.Canceled.CanceledAt;
                    entry.TerminalAckPending = requestedByPeer;
                    entry.State = TransferState.Canceled;
                    StopContentLocked(entry);
                    entry.AcceptedSignal.TrySetResult();
                    TryDelete(entry.PartPath);
                    snapshot = CreateSnapshot(entry);
                }
            }
        }

        Publish(snapshot);
        return TransferState.Canceled;
    }

    public void ApplyRemoteCanceled(TransferCanceled canceled)
    {
        TransferSnapshot snapshot;
        lock (_sync)
        {
            var entry = GetEntryLocked(canceled.TransferId);
            if (entry.State == TransferState.Canceled && !entry.CancelPending)
            {
                if (entry.Canceled != canceled)
                {
                    throw ProtocolError("invalid_metadata", "A duplicate canceled result changed fields.",
                        false, canceled.TransferId);
                }
                PersistLocked();
                return;
            }

            if (IsTerminal(entry.State) && entry.State != TransferState.Canceled)
            {
                throw ProtocolError("invalid_state", "The transfer already has a different terminal result.",
                    false, canceled.TransferId);
            }

            entry.State = TransferState.Canceled;
            entry.Canceled = canceled;
            entry.TerminalAt = canceled.CanceledAt;
            entry.CancelPending = false;
            entry.TerminalAckPending = false;
            StopContentLocked(entry);
            entry.AcceptedSignal.TrySetResult();
            TryDelete(entry.PartPath);
            snapshot = CreateSnapshot(entry);
        }

        Publish(snapshot);
    }

    public TransferOffer Retry(Guid transferId)
    {
        string sourcePath;
        TransferKind kind;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            EnsureDirection(entry, TransferDirection.WindowsToAndroid);
            if (entry.CancelPending)
            {
                throw ProtocolError("invalid_state", "Wait for cancellation confirmation before retrying.",
                    false, transferId);
            }
            if (entry.State != TransferState.Canceled
                && (entry.State != TransferState.Failed || entry.Failure?.Retryable != true))
            {
                throw ProtocolError("invalid_state", "Only retryable failed or canceled transfers can be retried.",
                    false, transferId);
            }
            sourcePath = entry.SourcePath!;
            kind = entry.Offer.Kind;
        }

        return EnqueueOutgoing(sourcePath, kind, retryOf: transferId);
    }

    internal void HandleSessionDisconnected()
    {
        var changed = new List<TransferSnapshot>();
        lock (_sync)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.CancelPending)
                {
                    StopContentLocked(entry);
                    entry.CancelWaitExpired = false;
                }
                else if (entry.State == TransferState.Offered)
                {
                    entry.State = TransferState.Queued;
                    changed.Add(CreateSnapshot(entry));
                }
                else if (entry.State is TransferState.Accepted or TransferState.Transferring)
                {
                    SetFailureLocked(entry, "network_interrupted", "The phone connection was interrupted.",
                        true, localTerminal: true);
                    TryDelete(entry.PartPath);
                    changed.Add(CreateSnapshot(entry));
                }
            }
            RebuildOutgoingQueueLocked();
            PersistLocked();
        }

        foreach (var snapshot in changed) { Publish(snapshot, persist: false); }
    }

    internal void ReturnOutgoingOfferToQueue(Guid transferId)
    {
        TransferSnapshot? snapshot = null;
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (entry.State == TransferState.Offered && !entry.CancelPending)
            {
                entry.State = TransferState.Queued;
                RebuildOutgoingQueueLocked();
                snapshot = CreateSnapshot(entry);
            }
        }
        Publish(snapshot);
    }

    public bool ExpireWaitingTransfers(DateTimeOffset now)
    {
        var changed = new List<TransferSnapshot>();
        var cancellationExpired = false;
        lock (_sync)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.CancelPending)
                {
                    if (entry.CancelWaitExpired || entry.CancelWaitStartedAt is DateTimeOffset since
                        && now - since >= CancellationConfirmationTimeout)
                    {
                        StopContentLocked(entry);
                        entry.CancelWaitExpired = true;
                        cancellationExpired = true;
                    }
                    continue;
                }
                var digestExpired = entry.State == TransferState.Verifying
                    && now - entry.VerifyingSince >= DigestRetentionPeriod;
                var offerExpired = entry.State is TransferState.Offered or TransferState.Accepted
                    && now - entry.StateSince >= OfferResponseTimeout;
                if (!digestExpired && !offerExpired) { continue; }
                SetFailureLocked(entry, digestExpired ? "digest_timeout" : "network_interrupted",
                    digestExpired ? "Digest reconciliation expired after 24 hours."
                        : "The peer did not start the accepted transfer in time.", true, localTerminal: true);
                TryDelete(entry.PartPath);
                changed.Add(CreateSnapshot(entry));
            }
            if (changed.Count > 0) { PersistLocked(); }
        }
        foreach (var snapshot in changed) { Publish(snapshot, persist: false); }
        return cancellationExpired;
    }

    internal void ReleaseContent(Guid transferId)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(transferId, out var entry)) { return; }
            entry.ActiveStream = null;
            if (IsTerminal(entry.State)) { TryDelete(entry.PartPath); }
        }
    }

    private static ProtocolEnvelope TerminalEnvelopeLocked(TransferEntry entry) => entry.State switch
    {
        TransferState.Completed => TransferProtocolMessages.Completed(entry.Completed!),
        TransferState.Failed => TransferProtocolMessages.Failed(entry.Failure!),
        TransferState.Canceled => TransferProtocolMessages.Canceled(entry.Canceled!),
        _ => throw new InvalidOperationException("Transfer is not terminal.")
    };

    private static void StopContentLocked(TransferEntry entry)
    {
        entry.CancelWaitCancellation?.Dispose();
        entry.CancelWaitCancellation = null;
        entry.CancelWaitStartedAt = null;
        var activeStream = entry.ActiveStream;
        entry.ActiveStream = null;
        entry.BodyCancellation.Cancel();
        try { activeStream?.Dispose(); }
        // The HTTP worker may finish disposing the same canceled file stream first.
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        entry.OutgoingBodyFinished.TrySetResult();
        if (entry.CancelPending) { TryDelete(entry.PartPath); }
    }

    internal void SuspendPendingCancellation(Guid transferId)
    {
        lock (_sync)
        {
            var entry = GetEntryLocked(transferId);
            if (entry.CancelPending) { StopContentLocked(entry); entry.CancelWaitExpired = false; }
        }
    }

    private void StartCancellationWaitLocked(TransferEntry entry)
    {
        if (entry.CancelWaitCancellation is not null || entry.CancelWaitExpired) { return; }
        var wait = new CancellationTokenSource();
        entry.CancelWaitCancellation = wait;
        entry.CancelWaitStartedAt = DateTimeOffset.UtcNow;
        wait.Token.Register(() =>
        {
            lock (_sync)
            {
                if (!entry.CancelPending || !ReferenceEquals(entry.CancelWaitCancellation, wait)) { return; }
                StopContentLocked(entry);
                entry.CancelWaitExpired = true;
            }
            CancellationConfirmationExpired?.Invoke(this, EventArgs.Empty);
        });
        wait.CancelAfter(CancellationConfirmationTimeout);
    }

    private void RebuildOutgoingQueueLocked()
    {
        _outgoingQueue.Clear();
        foreach (var entry in _entries.Values.Where(entry =>
            entry.Direction == TransferDirection.WindowsToAndroid && entry.State == TransferState.Queued)
            .OrderBy(entry => entry.RecordedAt))
        {
            _outgoingQueue.Enqueue(entry.Offer.TransferId);
        }
    }

    private void PersistLocked() => _history?.Save(_entries.Values.Select(entry => new TransferHistoryRecord(
        entry.Offer, entry.Direction, entry.State, entry.RecordedAt, entry.SourcePath, entry.ReceiveDirectory,
        entry.PlannedName, entry.StoredName, entry.BytesTransferred, entry.LocalSha256,
        entry.RemoteDigest, entry.Completed, entry.Failure, entry.Canceled, entry.TerminalAt,
        entry.VerifyingSince, entry.TerminalAckPending, entry.CancelPending, entry.PublishPending,
        entry.FailureIsLocal, entry.RemoteFailure)).ToArray());

    private void RestoreHistory()
    {
        try
        {
            // Validate the entire snapshot before touching any file. A later damaged
            // record must not make an earlier pending object disappear on startup.
            foreach (var record in _history!.ReadAll())
            {
                var entry = RestoreEntry(record);
                if (!_entries.TryAdd(entry.Offer.TransferId, entry))
                {
                    throw new TransferHistoryStoreException("Transfer history contains duplicate IDs.");
                }
            }

            foreach (var entry in _entries.Values)
            {
                if (entry.CancelPending) { StopContentLocked(entry); }
                else if (entry.State == TransferState.Offered) { entry.State = TransferState.Queued; }
                else if (entry.State is TransferState.Accepted or TransferState.Transferring)
                {
                    SetFailureLocked(entry, "network_interrupted", "The content stream was interrupted by restart.",
                        true, localTerminal: true);
                }
                else if (entry.PublishPending && entry.State == TransferState.Verifying)
                {
                    RecoverPublicationLocked(entry);
                }
                else if (entry.Direction == TransferDirection.AndroidToWindows
                    && entry.State == TransferState.Verifying && !File.Exists(entry.PartPath))
                {
                    SetFailureLocked(entry, "io_error", "The unverified receive file is no longer available.",
                        true, localTerminal: true);
                }
                if (IsTerminal(entry.State)) { TryDelete(entry.PartPath); }
            }
        }
        catch (Exception exception) when (exception is TransferProtocolException or NullReferenceException
            or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Text.Json.JsonException)
        {
            if (exception is TransferHistoryStoreException) { throw; }
            throw new TransferHistoryStoreException("Unable to read transfer history. The original data was kept.",
                exception);
        }
        RebuildOutgoingQueueLocked();
        ExpireWaitingTransfers(DateTimeOffset.UtcNow);
        PersistLocked();
    }

    private static TransferEntry RestoreEntry(TransferHistoryRecord record)
    {
        ValidateOffer(record.Offer);
        if (!Enum.IsDefined(record.Direction) || !Enum.IsDefined(record.State)
            || record.BytesTransferred < 0 || !IsUtcInstant(record.RecordedAt)
            || IsTerminal(record.State) != (record.TerminalAt is not null)
            || record.TerminalAt is not null && !IsUtcInstant(record.TerminalAt.Value)
            || record.VerifyingSince is not null && !IsUtcInstant(record.VerifyingSince.Value)
            || (record.Completed is not null) != (record.State == TransferState.Completed)
            || (record.Failure is not null) != (record.State == TransferState.Failed)
            || record.State == TransferState.Canceled && record.Canceled is null
            || record.Canceled is not null && record.State != TransferState.Canceled && !record.CancelPending
            || record.TerminalAckPending && !IsTerminal(record.State)
            || record.CancelPending && record.State != TransferState.Canceled
                && (IsTerminal(record.State) || record.State == TransferState.Queued)
            || record.RemoteFailure is not null && record.State != TransferState.Failed
            || record.State == TransferState.Verifying
                && (record.LocalSha256 is null || record.VerifyingSince is null)
            || record.PublishPending && (record.State != TransferState.Verifying
                || record.Direction != TransferDirection.AndroidToWindows
                || record.StoredName is null || record.RemoteDigest is null))
        {
            throw new TransferHistoryStoreException("Transfer history contains invalid state.");
        }

        if (record.LocalSha256 is not null)
        {
            _ = TransferProtocolParser.ParseDigest(TransferProtocolMessages.Digest(new TransferDigest(
                record.Offer.TransferId, "sha-256", record.LocalSha256, record.BytesTransferred)).Payload);
        }
        if (record.RemoteDigest is not null)
        {
            var digest = TransferProtocolParser.ParseDigest(
                TransferProtocolMessages.Digest(record.RemoteDigest).Payload);
            if (digest.TransferId != record.Offer.TransferId)
            {
                throw new TransferHistoryStoreException("Transfer history contains a mismatched digest ID.");
            }
        }
        if (record.Completed is not null)
        {
            var completed = TransferProtocolParser.ParseCompleted(
                TransferProtocolMessages.Completed(record.Completed).Payload);
            if (completed.TransferId != record.Offer.TransferId || completed.Bytes != record.BytesTransferred
                || completed.Sha256 != record.LocalSha256 || completed.StoredName != record.StoredName
                || completed.CompletedAt != record.TerminalAt
                || record.Offer.Size is not null && completed.Bytes != record.Offer.Size)
            {
                throw new TransferHistoryStoreException("Transfer history contains inconsistent completion.");
            }
        }
        foreach (var failure in new[] { record.Failure, record.RemoteFailure })
        {
            if (failure is null) { continue; }
            var parsed = TransferProtocolParser.ParseFailure(TransferProtocolMessages.Failed(failure).Payload);
            if (parsed.TransferId != record.Offer.TransferId)
            {
                throw new TransferHistoryStoreException("Transfer history contains a mismatched failure ID.");
            }
        }
        if (record.Canceled is not null)
        {
            var canceled = TransferProtocolParser.ParseCanceled(
                TransferProtocolMessages.Canceled(record.Canceled).Payload);
            if (canceled.TransferId != record.Offer.TransferId
                || record.State == TransferState.Canceled && canceled.CanceledAt != record.TerminalAt)
            {
                throw new TransferHistoryStoreException("Transfer history contains inconsistent cancellation.");
            }
        }
        var failureIsLocal = record.FailureIsLocal ?? (record.State == TransferState.Failed
            && record.TerminalAckPending);
        if (!failureIsLocal && record.RemoteFailure is not null && record.RemoteFailure != record.Failure
            || record.PublishPending && (record.RemoteDigest!.Algorithm != "sha-256"
                || record.RemoteDigest.Bytes != record.BytesTransferred
                || record.RemoteDigest.Value != record.LocalSha256
                || record.Offer.Size is not null && record.BytesTransferred != record.Offer.Size))
        {
            throw new TransferHistoryStoreException("Transfer history contains inconsistent reconciliation.");
        }

        var entry = new TransferEntry(record.Offer, record.Direction)
        {
            State = record.State,
            RecordedAt = record.RecordedAt,
            SourcePath = record.SourcePath,
            ReceiveDirectory = record.ReceiveDirectory,
            PlannedName = record.PlannedName,
            StoredName = record.StoredName,
            BytesTransferred = record.BytesTransferred,
            LocalSha256 = record.LocalSha256,
            RemoteDigest = record.RemoteDigest,
            Completed = record.Completed,
            Failure = record.Failure,
            FailureIsLocal = failureIsLocal,
            RemoteFailure = record.RemoteFailure,
            Canceled = record.Canceled,
            TerminalAt = record.TerminalAt,
            VerifyingSince = record.VerifyingSince,
            TerminalAckPending = record.TerminalAckPending,
            CancelPending = record.CancelPending,
            PublishPending = record.PublishPending
        };
        if (entry.Direction == TransferDirection.WindowsToAndroid)
        {
            if (string.IsNullOrWhiteSpace(entry.SourcePath) || !Path.IsPathFullyQualified(entry.SourcePath))
            {
                throw new TransferHistoryStoreException("The source path is missing from transfer history.");
            }
            entry.SourcePath = Path.GetFullPath(entry.SourcePath);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(entry.ReceiveDirectory)
                && entry.State is not (TransferState.Failed or TransferState.Canceled))
            {
                throw new TransferHistoryStoreException("The receive directory is missing from transfer history.");
            }
            if (!string.IsNullOrWhiteSpace(entry.ReceiveDirectory))
            {
                if (!Path.IsPathFullyQualified(entry.ReceiveDirectory))
                {
                    throw new TransferHistoryStoreException("Transfer history contains a relative receive directory.");
                }
                entry.ReceiveDirectory = Path.GetFullPath(entry.ReceiveDirectory);
                entry.PartPath = TransferFileNames.GetPathInsideRoot(entry.ReceiveDirectory,
                    $".bob-{entry.Offer.TransferId:D}.part");
                if (entry.PlannedName is not null)
                {
                    EnsureSavedBasename(entry.ReceiveDirectory, entry.PlannedName);
                }
                if (entry.StoredName is not null)
                {
                    EnsureSavedBasename(entry.ReceiveDirectory, entry.StoredName);
                }
            }
        }
        return entry;
    }

    private static bool IsUtcInstant(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private static void EnsureSavedBasename(string receiveDirectory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
        {
            throw new TransferHistoryStoreException("Transfer history contains an invalid receive filename.");
        }
        _ = TransferFileNames.GetPathInsideRoot(receiveDirectory, name);
    }

    private static void RecoverPublicationLocked(TransferEntry entry)
    {
        if (File.Exists(entry.PartPath))
        {
            entry.PublishPending = false;
            entry.StoredName = null;
            return;
        }
        if (!string.IsNullOrWhiteSpace(entry.StoredName))
        {
            var destination = TransferFileNames.GetPathInsideRoot(entry.ReceiveDirectory!, entry.StoredName);
            if (File.Exists(destination))
            {
                using var stream = File.OpenRead(destination);
                var sha256 = Convert.ToBase64String(SHA256.HashData(stream)).TrimEnd('=')
                    .Replace('+', '-').Replace('/', '_');
                if (stream.Length == entry.BytesTransferred && sha256 == entry.LocalSha256)
                {
                    entry.Completed = new TransferCompleted(entry.Offer.TransferId,
                        entry.BytesTransferred, sha256, entry.StoredName, DateTimeOffset.UtcNow);
                    entry.State = TransferState.Completed;
                    entry.TerminalAt = entry.Completed.CompletedAt;
                    entry.TerminalAckPending = true;
                    entry.PublishPending = false;
                    return;
                }
            }
        }
        SetFailureLocked(entry, "io_error", "The verified publication could not be recovered.",
            true, localTerminal: true);
    }

    private static void ValidateOffer(TransferOffer offer)
    {
        if (offer is null || !Enum.IsDefined(offer.Kind) || offer.TransferId == Guid.Empty
            || offer.RetryOf == Guid.Empty
            || string.IsNullOrWhiteSpace(offer.Name)
            || offer.Size < 0
            || string.IsNullOrWhiteSpace(offer.MediaType)
            || !MediaTypeHeaderValue.TryParse(offer.MediaType, out _)
            || offer.CreatedAt == default
            || offer.CreatedAt.Offset != TimeSpan.Zero
            || System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(offer, ProtocolJson.Options).Length
                > BobProtocol.MaxSnapshotEnvelopeBytes - 2048)
        {
            throw ProtocolError(
                "invalid_metadata",
                "transfer.offer contains invalid metadata.",
                retryable: false,
                offer is null || offer.TransferId == Guid.Empty ? null : offer.TransferId);
        }
    }

    private string PublishPartLocked(TransferEntry entry)
    {
        if (entry.CancelPending)
        {
            throw ProtocolError("invalid_state", "The cancellation is awaiting the peer's stored result.",
                false, entry.Offer.TransferId);
        }
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
            entry.StoredName = storedName;
            entry.PublishPending = true;
            PersistLocked();
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
            && (entry.CancelPending || entry.ActiveStream is not null || entry.State is TransferState.Offered
                or TransferState.Accepted
                or TransferState.Transferring
                or TransferState.Verifying));

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

    private static void EnsureContentCanStartLocked(TransferEntry entry)
    {
        if (entry.CancelPending && entry.BodyCancellation.IsCancellationRequested)
        {
            throw ProtocolError("invalid_state", "The cancellation is awaiting the peer's stored result.",
                false, entry.Offer.TransferId);
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
        entry.FailureIsLocal = localTerminal;
        entry.RemoteFailure = localTerminal ? null : failure;
        entry.PublishPending = false;
        entry.TerminalAt = DateTimeOffset.UtcNow;
        entry.TerminalAckPending = localTerminal;
        StopContentLocked(entry);
        entry.CancelPending = false;
        entry.Canceled = null;
        entry.AcceptedSignal.TrySetResult();
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
            GetLocalPath(entry),
            !entry.CancelPending && (entry.Failure?.Retryable ?? entry.State == TransferState.Canceled),
            entry.Offer.RetryOf,
            entry.TerminalAt,
            entry.TerminalAckPending,
            entry.State == TransferState.Completed ? entry.LocalSha256 : null,
            entry.RecordedAt,
            entry.CancelPending);

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

    private void Publish(TransferSnapshot? snapshot, bool persist = true)
    {
        if (snapshot is not null)
        {
            if (persist)
            {
                lock (_sync)
                {
                    try { PersistLocked(); }
                    catch (TransferHistoryStoreException)
                    {
                        if (_entries.TryGetValue(snapshot.TransferId, out var entry))
                        {
                            StopContentLocked(entry);
                        }
                        throw;
                    }
                }
            }
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

        public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

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

        public bool FailureIsLocal { get; set; }

        public TransferFailure? RemoteFailure { get; set; }

        public bool TerminalAckPending { get; set; }

        public TransferCanceled? Canceled { get; set; }

        public bool CancelPending { get; set; }

        public CancellationTokenSource? CancelWaitCancellation { get; set; }

        public DateTimeOffset? CancelWaitStartedAt { get; set; }

        public bool CancelWaitExpired { get; set; }

        public TaskCompletionSource OutgoingBodyFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PublishPending { get; set; }

        public DateTimeOffset? TerminalAt { get; set; }

        public DateTimeOffset? VerifyingSince { get; set; }

        public DateTimeOffset StateSince { get; set; } = DateTimeOffset.UtcNow;

        public CancellationTokenSource BodyCancellation { get; } = new();

        public Stream? ActiveStream { get; set; }

        public TaskCompletionSource AcceptedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
