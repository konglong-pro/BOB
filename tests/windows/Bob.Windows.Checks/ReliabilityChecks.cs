using System.Security.Cryptography;
using Bob.Windows;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Transport;
using Bob.Windows.UI;

internal static class ReliabilityChecks
{
    public static void CheckEarlyDigest()
    {
        WithRoot(root =>
        {
            var coordinator = new TransferCoordinator(Path.Combine(root, "received"));
            var bytes = "cross-channel body"u8.ToArray();
            var offer = Offer("early.bin", bytes.LongLength);
            coordinator.RegisterIncomingOffer(offer);
            var digest = new TransferDigest(offer.TransferId, "sha-256", Sha(bytes), bytes.LongLength);
            var early = coordinator.ApplyIncomingDigest(digest);
            Ensure(early.Completed is null && early.Failure is null,
                "An early digest must be saved without prematurely publishing or failing.");
            var body = coordinator.BeginIncomingContent(offer.TransferId, bytes.LongLength);
            body.Stream.Write(bytes);
            Ensure(coordinator.ApplyIncomingDigest(digest).Completed is null,
                "A repeated digest must wait while the receive handle is still open.");
            Ensure(!File.Exists(Path.Combine(coordinator.ReceiveDirectory, "early.bin")),
                "Unflushed content must stay unpublished.");
            body.Stream.Dispose();
            var completed = coordinator.CompleteIncomingContent(offer.TransferId, bytes.LongLength,
                Sha(bytes))?.Completed;
            Ensure(completed is not null, "The retained digest must reconcile after the body closes.");
            Ensure(File.ReadAllBytes(Path.Combine(coordinator.ReceiveDirectory, "early.bin"))
                .SequenceEqual(bytes), "Published bytes changed.");
            var conflicting = false;
            try { coordinator.ApplyIncomingDigest(digest with { Value = Sha("different"u8.ToArray()) }); }
            catch (TransferProtocolException exception) { conflicting = exception.Code == "invalid_metadata"; }
            Ensure(conflicting, "Conflicting repeated digests must be rejected.");
        });
    }

    public static void CheckCancelAndRetry()
    {
        WithRoot(root =>
        {
            var coordinator = new TransferCoordinator(Path.Combine(root, "received"));
            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [1, 2, 3]);
            var first = coordinator.EnqueueOutgoing(source, TransferKind.File);
            var second = coordinator.EnqueueOutgoing(source, TransferKind.File);
            Ensure(coordinator.TryOfferNextOutgoing()?.TransferId == first.TransferId, "FIFO changed.");
            coordinator.AcceptOutgoing(first.TransferId, "copy.bin");
            var outgoing = coordinator.BeginOutgoingContent(first.TransferId);
            var incomingOffer = Offer("incoming.bin", null);
            coordinator.RegisterIncomingOffer(incomingOffer);
            var incoming = coordinator.BeginIncomingContent(incomingOffer.TransferId, null);
            incoming.Stream.Write([5, 6]);
            Ensure(coordinator.Cancel(incomingOffer.TransferId, requestedByPeer: true) == TransferState.Canceled,
                "An active incoming transfer must cancel.");
            Ensure(incoming.CancellationToken.IsCancellationRequested,
                "Cancel must stop the active HTTP body.");
            Ensure(!File.Exists(Path.Combine(coordinator.ReceiveDirectory,
                $".bob-{incomingOffer.TransferId:D}.part")), "An open .part handle was not closed and removed.");
            Ensure(coordinator.RegisterIncomingOffer(incomingOffer).Reply == IncomingOfferReply.Canceled,
                "A repeated canceled offer must replay its terminal result.");
            Ensure(coordinator.RegisterIncomingOffer(Offer("next.bin", 0)).Reply == IncomingOfferReply.Accepted,
                "Cancel must free the incoming direction.");
            coordinator.Cancel(first.TransferId);
            Ensure(!outgoing.CancellationToken.IsCancellationRequested,
                "A sender cancel must reach the receiver before aborting its HTTP source.");
            coordinator.ApplyRemoteCanceled(new TransferCanceled(first.TransferId, DateTimeOffset.UtcNow));
            Ensure(outgoing.CancellationToken.IsCancellationRequested,
                "The source read must close after the receiver returns its canonical cancellation.");
            File.WriteAllBytes(source, [4, 5, 6, 7]);
            var retry = coordinator.Retry(first.TransferId);
            Ensure(retry.TransferId != first.TransferId && retry.RetryOf == first.TransferId && retry.Size == 4,
                "Retry must make a new ID and reread current source metadata.");
            Ensure(coordinator.TryOfferNextOutgoing()?.TransferId == second.TransferId,
                "Retry must remain behind older queued files.");
            coordinator.Cancel(second.TransferId);
            coordinator.ApplyRemoteCanceled(new TransferCanceled(second.TransferId, DateTimeOffset.UtcNow));
            Ensure(coordinator.TryOfferNextOutgoing()?.TransferId == retry.TransferId, "Retry queue was blocked.");
            incoming.Stream.Dispose();
            outgoing.Stream.Dispose();
        });
    }

    public static void CheckDurableRecovery()
    {
        WithRoot(root =>
        {
            var paths = Paths(root);
            var receiveA = Path.Combine(root, "received-a");
            var receiveB = Path.Combine(root, "received-b");
            var coordinator = new TransferCoordinator(receiveA, paths);
            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [1, 3, 5]);
            var first = coordinator.EnqueueOutgoing(source, TransferKind.File);
            var second = coordinator.EnqueueOutgoing(source, TransferKind.File);
            coordinator.TryOfferNextOutgoing();
            var incoming = Offer("verified.bin", 3);
            coordinator.RegisterIncomingOffer(incoming);
            var body = coordinator.BeginIncomingContent(incoming.TransferId, 3);
            body.Stream.Write([2, 4, 6]);
            body.Stream.Dispose();
            coordinator.CompleteIncomingContent(incoming.TransferId, 3, Sha([2, 4, 6]));
            var recovered = new TransferCoordinator(receiveB, paths);
            Ensure(recovered.TryOfferNextOutgoing()?.TransferId == first.TransferId,
                "An unaccepted offer must retain its ID and FIFO across restart.");
            var completed = recovered.ApplyIncomingDigest(new TransferDigest(incoming.TransferId,
                "sha-256", Sha([2, 4, 6]), 3)).Completed!;
            Ensure(File.Exists(Path.Combine(receiveA, completed.StoredName))
                && !File.Exists(Path.Combine(receiveB, completed.StoredName)),
                "Restart must preserve the frozen receive directory.");
            var terminalRestored = new TransferCoordinator(receiveB, paths);
            var saved = terminalRestored.RegisterIncomingOffer(incoming).Completed;
            Ensure(saved == completed, "A terminal replay changed its immutable completion payload.");
            Ensure(terminalRestored.ReadAllSnapshots().Single(item => item.TransferId == incoming.TransferId)
                .TerminalAckPending, "Lost terminalAck was not preserved across restart.");
            terminalRestored.AcknowledgeTerminal(incoming.TransferId, TransferState.Completed);
            var acked = new TransferCoordinator(receiveB, paths);
            Ensure(!acked.ReadAllSnapshots().Single(item => item.TransferId == incoming.TransferId)
                .TerminalAckPending, "Terminal confirmation was not durable.");
            acked.TryOfferNextOutgoing();
            acked.AcceptOutgoing(first.TransferId, "source.bin");
            var read = acked.BeginOutgoingContent(first.TransferId);
            read.Stream.Dispose();
            var interrupted = new TransferCoordinator(receiveB, paths);
            Ensure(interrupted.ReadAllSnapshots().Single(item => item.TransferId == first.TransferId)
                .ErrorCode == "network_interrupted", "A restarted body must fail from byte zero.");
            Ensure(interrupted.TryOfferNextOutgoing()?.TransferId == second.TransferId,
                "An interrupted body blocked the durable FIFO.");
            var historyPath = Path.Combine(paths.RootDirectory, "file-transfers-v1.json");
            File.WriteAllText(historyPath, "{broken}");
            var rejected = false;
            try { _ = new TransferCoordinator(receiveB, paths); }
            catch (TransferHistoryStoreException) { rejected = true; }
            Ensure(rejected && File.ReadAllText(historyPath) == "{broken}",
                "Damaged transfer history must be preserved instead of erasing its queue.");
        });
    }

    public static void CheckDigestRetention()
    {
        WithRoot(root =>
        {
            Ensure(TransferCoordinator.DigestRetentionPeriod == TimeSpan.FromHours(24),
                "The digest retention window must match the 24-hour contract.");
            var paths = Paths(root);
            var coordinator = new TransferCoordinator(Path.Combine(root, "received"), paths);
            var offer = Offer("waiting.bin", 0);
            coordinator.RegisterIncomingOffer(offer);
            coordinator.BeginIncomingContent(offer.TransferId, 0).Stream.Dispose();
            coordinator.CompleteIncomingContent(offer.TransferId, 0, Sha([]));
            var since = new TransferHistoryStore(paths).ReadAll().Single().VerifyingSince!.Value;
            coordinator.ExpireWaitingTransfers(since.AddHours(24).AddTicks(-1));
            Ensure(coordinator.ReadAllSnapshots().Single().State == TransferState.Verifying,
                "Digest coordination expired before 24 hours.");
            coordinator.ExpireWaitingTransfers(since.AddHours(24));
            Ensure(coordinator.ReadAllSnapshots().Single().ErrorCode == "digest_timeout",
                "The 24-hour boundary must produce digest_timeout.");
            Ensure(!File.Exists(Path.Combine(coordinator.ReceiveDirectory, $".bob-{offer.TransferId:D}.part")),
                "Digest timeout must remove unverified content.");
        });
    }

    public static void CheckTerminalConflicts()
    {
        WithRoot(root =>
        {
            var coordinator = new TransferCoordinator(Path.Combine(root, "received"));
            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [1, 2, 3]);
            for (var index = 0; index < 3; index++)
            {
                var offer = coordinator.EnqueueOutgoing(source, TransferKind.File);
                coordinator.TryOfferNextOutgoing();
                coordinator.AcceptOutgoing(offer.TransferId, "copy.bin");
                var body = coordinator.BeginOutgoingContent(offer.TransferId);
                var sha256 = Convert.ToBase64String(SHA256.HashData(body.Stream))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                body.Stream.Dispose();
                coordinator.CompleteOutgoingContent(offer.TransferId, 3, sha256);
                if (index == 0)
                {
                    coordinator.FailLocal(offer.TransferId, "digest_timeout", "Digest expired.", true);
                }
                else if (index == 1)
                {
                    coordinator.ApplyRemoteFailure(new TransferFailure(offer.TransferId,
                        "permission_denied", "Peer storage failed.", true));
                }
                else { coordinator.Cancel(offer.TransferId); }
                var rejected = false;
                try
                {
                    coordinator.ApplyRemoteCompleted(new TransferCompleted(offer.TransferId, 3,
                        sha256, "copy.bin", DateTimeOffset.UtcNow));
                }
                catch (TransferProtocolException exception) { rejected = exception.Code == "invalid_state"; }
                Ensure(index == 2 ? !rejected : rejected,
                    "Completed must reject failed state and retain its priority only against cancel.");
            }
        });
    }

    public static void CheckHistoryFailures()
    {
        WithRoot(root =>
        {
            var blocker = Path.Combine(root, "blocked-directory");
            File.WriteAllText(blocker, "file");
            var blockedPaths = new AppPaths(blocker, Path.Combine(blocker, "identity"));
            var textHistory = new TextHistoryStore(blockedPaths);
            var textRejected = false;
            try
            {
                textHistory.Save(new TextHistoryRecord(Guid.NewGuid(), "persist first", DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, true, "Queued", "phone"));
            }
            catch (TextHistoryStoreException) { textRejected = true; }
            Ensure(textRejected && File.ReadAllText(blocker) == "file",
                "Directory I/O errors must use the documented text-store exception.");
            var transferRejected = false;
            try { _ = new TransferCoordinator(Path.Combine(root, "received"), blockedPaths); }
            catch (TransferHistoryStoreException) { transferRejected = true; }
            Ensure(transferRejected, "Directory I/O errors must use the documented transfer-store exception.");

            var paths = Paths(root);
            Directory.CreateDirectory(paths.RootDirectory);
            var path = Path.Combine(paths.RootDirectory, "file-transfers-v1.json");
            const string brokenTyped = "{\"schemaVersion\":1,\"transfers\":[{\"offer\":null}]}";
            File.WriteAllText(path, brokenTyped);
            transferRejected = false;
            try { _ = new TransferCoordinator(Path.Combine(root, "received"), paths); }
            catch (TransferHistoryStoreException) { transferRejected = true; }
            Ensure(transferRejected && File.ReadAllText(path) == brokenTyped,
                "Valid JSON with invalid typed records must be rejected and kept unchanged.");
            var textPath = Path.Combine(paths.RootDirectory, "text-messages-v1.json");
            File.WriteAllText(textPath, "{\"schemaVersion\":1,\"messages\":null}");
            textRejected = false;
            try { _ = new TextHistoryStore(paths); }
            catch (TextHistoryStoreException) { textRejected = true; }
            Ensure(textRejected, "A null messages array must not be treated as an empty durable outbox.");

            var directoryPaths = Paths(Path.Combine(root, "directory-collision"));
            var textDirectory = Path.Combine(directoryPaths.RootDirectory, "text-messages-v1.json");
            var transferDirectory = Path.Combine(directoryPaths.RootDirectory, "file-transfers-v1.json");
            Directory.CreateDirectory(textDirectory);
            Directory.CreateDirectory(transferDirectory);
            File.WriteAllText(Path.Combine(textDirectory, "preserved.txt"), "existing text object");
            File.WriteAllText(Path.Combine(transferDirectory, "preserved.txt"), "existing transfer object");
            textRejected = false;
            try { _ = new TextHistoryStore(directoryPaths); }
            catch (TextHistoryStoreException) { textRejected = true; }
            transferRejected = false;
            try { _ = new TransferCoordinator(Path.Combine(root, "other-received"), directoryPaths); }
            catch (TransferHistoryStoreException) { transferRejected = true; }
            Ensure(textRejected && transferRejected
                && File.ReadAllText(Path.Combine(textDirectory, "preserved.txt")) == "existing text object"
                && File.ReadAllText(Path.Combine(transferDirectory, "preserved.txt")) == "existing transfer object",
                "A directory at either history filename must be rejected and preserved, never treated as an empty outbox.");
        });
    }

    public static void CheckConcurrentFailures()
    {
        WithRoot(root =>
        {
            var paths = Paths(root);
            var receive = Path.Combine(root, "received");
            var coordinator = new TransferCoordinator(receive, paths);
            var offer = Offer("interrupted.bin", null);
            coordinator.RegisterIncomingOffer(offer);
            var local = coordinator.FailLocal(offer.TransferId, "network_interrupted",
                "Windows saw the upload disconnect.", true);
            var original = coordinator.ReadAllSnapshots().Single();
            // A late peer result can arrive even after our own result was acknowledged.
            coordinator.AcknowledgeTerminal(offer.TransferId, TransferState.Failed);
            var restored = new TransferCoordinator(receive, paths);
            var remote = new TransferFailure(offer.TransferId, "io_error",
                "Android saw the source stream close.", true);
            restored.ApplyRemoteFailure(remote);
            restored.ApplyRemoteFailure(remote);
            var snapshot = restored.ReadAllSnapshots().Single();
            Ensure(snapshot.ErrorCode == local.Code && snapshot.ErrorMessage == local.Message
                && snapshot.TerminalAt == original.TerminalAt && !snapshot.TerminalAckPending,
                "Simultaneous failure must keep the original local terminal and its acknowledgement.");
            var replay = restored.RegisterIncomingOffer(offer).Failure;
            Ensure(replay == local, "A repeated offer changed the original local failure payload.");
            var afterRestart = new TransferCoordinator(receive, paths);
            afterRestart.ApplyRemoteFailure(remote);
            var conflicting = false;
            try { afterRestart.ApplyRemoteFailure(remote with { Message = "Changed peer result." }); }
            catch (TransferProtocolException exception) { conflicting = exception.Code == "invalid_metadata"; }
            Ensure(conflicting, "A peer failure replay must remain immutable across restart.");

            var other = Offer("remote-first.bin", 0);
            afterRestart.RegisterIncomingOffer(other);
            var remoteFirst = new TransferFailure(other.TransferId, "permission_denied", "No access.", true);
            afterRestart.ApplyRemoteFailure(remoteFirst);
            afterRestart.ApplyRemoteFailure(remoteFirst);
            conflicting = false;
            try { afterRestart.ApplyRemoteFailure(remoteFirst with { Code = "io_error" }); }
            catch (TransferProtocolException exception) { conflicting = exception.Code == "invalid_metadata"; }
            Ensure(conflicting, "A remote-origin failure must reject a changed first terminal payload.");
        });
    }

    public static void CheckHistoryValidationBeforeCleanup()
    {
        WithRoot(root =>
        {
            var paths = Paths(root);
            var receive = Path.Combine(root, "received");
            var coordinator = new TransferCoordinator(receive, paths);
            var terminal = Offer("terminal.bin", 0);
            coordinator.RegisterIncomingOffer(terminal);
            coordinator.FailLocal(terminal.TransferId, "network_interrupted", "Interrupted.", true);
            var waiting = Offer("waiting.bin", 0);
            coordinator.RegisterIncomingOffer(waiting);
            var records = new TransferHistoryStore(paths).ReadAll();
            var terminalPart = Path.Combine(receive, $".bob-{terminal.TransferId:D}.part");
            File.WriteAllText(terminalPart, "pending terminal cleanup");
            var waitingPart = Path.Combine(receive, $".bob-{waiting.TransferId:D}.part");
            var originalWaiting = File.ReadAllBytes(waitingPart);
            var history = new TransferHistoryStore(paths);
            history.Save([records[0], records[1] with { BytesTransferred = -1 }]);
            var path = Path.Combine(paths.RootDirectory, "file-transfers-v1.json");
            var saved = File.ReadAllText(path);
            var rejected = false;
            try { _ = new TransferCoordinator(receive, paths); }
            catch (TransferHistoryStoreException) { rejected = true; }
            Ensure(rejected && File.ReadAllText(path) == saved
                && File.ReadAllText(terminalPart) == "pending terminal cleanup"
                && File.ReadAllBytes(waitingPart).SequenceEqual(originalWaiting),
                "A later invalid record must not trigger earlier pending-file cleanup or overwrite history.");
            history.Save([records[0], records[0]]);
            rejected = false;
            try { _ = new TransferCoordinator(receive, paths); }
            catch (TransferHistoryStoreException) { rejected = true; }
            Ensure(rejected && File.Exists(terminalPart),
                "Duplicate journal IDs must be rejected before any pending-file cleanup.");
        });
    }

    public static void CheckCancelRequestLosesToExistingFailure()
    {
        WithRoot(root =>
        {
            var paths = Paths(root);
            var receive = Path.Combine(root, "received");
            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [1, 2]);
            var coordinator = new TransferCoordinator(receive, paths);
            var offer = coordinator.EnqueueOutgoing(source, TransferKind.File);
            coordinator.TryOfferNextOutgoing();
            coordinator.Cancel(offer.TransferId);
            Ensure(new TransferHistoryStore(paths).ReadAll().Single().CancelPending,
                "An online cancel must remain a request until the peer returns its stored terminal.");
            var failure = new TransferFailure(offer.TransferId, "permission_denied",
                "The peer had already failed before receiving cancel.", true);
            coordinator.ApplyRemoteFailure(failure);
            var saved = new TransferHistoryStore(paths).ReadAll().Single();
            Ensure(saved.State == TransferState.Failed && saved.Failure == failure
                && saved.Canceled is null && !saved.CancelPending && !saved.TerminalAckPending,
                "The peer's existing failure must settle an unconfirmed cancellation request.");
            var restored = new TransferCoordinator(receive, paths);
            restored.ApplyRemoteFailure(failure);
            var other = restored.EnqueueOutgoing(source, TransferKind.File);
            restored.TryOfferNextOutgoing();
            restored.Cancel(other.TransferId, requestedByPeer: true);
            var conflicting = false;
            try { restored.ApplyRemoteFailure(failure with { TransferId = other.TransferId }); }
            catch (TransferProtocolException exception) { conflicting = exception.Code == "invalid_state"; }
            Ensure(conflicting, "A confirmed canceled result must stay immutable.");
        });
    }

    public static void CheckConcurrentCancellation()
    {
        WithRoot(root =>
        {
            var paths = Paths(root);
            var receive = Path.Combine(root, "received");
            var coordinator = new TransferCoordinator(receive, paths);
            var incoming = Offer("canceled-upload.bin", null);
            coordinator.RegisterIncomingOffer(incoming);
            coordinator.Cancel(incoming.TransferId);
            Ensure(coordinator.ReadTerminalOrCancelEnvelope(incoming.TransferId)?.Type == "transfer.cancel",
                "An unconfirmed local cancellation must only replay cancel, including HTTP error paths.");
            Ensure(coordinator.ReadAllSnapshots().Single().TerminalAt is null,
                "The receiver's local request must not invent a terminal before the peer responds.");
            var originalTime = new TransferHistoryStore(paths).ReadAll().Single().Canceled!.CanceledAt;
            coordinator.Cancel(incoming.TransferId, requestedByPeer: true);
            var reply = coordinator.ReadTerminalOrCancelEnvelope(incoming.TransferId)!;
            Ensure(reply.Type == "transfer.canceled"
                && TransferProtocolParser.ParseCanceled(reply.Payload).CanceledAt == originalTime,
                "The body receiver must settle concurrent cancellation with its original timestamp.");
            var receiverRestarted = new TransferCoordinator(receive, paths);
            receiverRestarted.Cancel(incoming.TransferId, requestedByPeer: true);
            Ensure(receiverRestarted.ReadTerminalOrCancelEnvelope(incoming.TransferId)!.Payload.GetRawText()
                == reply.Payload.GetRawText(), "The receiver's canceled result must survive restart unchanged.");
            receiverRestarted.AcknowledgeTerminal(incoming.TransferId, TransferState.Canceled);

            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [1]);
            var outgoing = receiverRestarted.EnqueueOutgoing(source, TransferKind.File);
            receiverRestarted.TryOfferNextOutgoing();
            receiverRestarted.Cancel(outgoing.TransferId);
            receiverRestarted.Cancel(outgoing.TransferId, requestedByPeer: true);
            Ensure(receiverRestarted.ReadTerminalOrCancelEnvelope(outgoing.TransferId)?.Type == "transfer.cancel",
                "The body sender must wait for the receiver's simultaneous-cancel result.");
            var peerCanceled = new TransferCanceled(outgoing.TransferId, DateTimeOffset.UtcNow.AddSeconds(1));
            receiverRestarted.ApplyRemoteCanceled(peerCanceled);
            Ensure(TransferProtocolParser.ParseCanceled(
                receiverRestarted.ReadTerminalOrCancelEnvelope(outgoing.TransferId)!.Payload) == peerCanceled,
                "The sender must retain the receiver's fixed cancellation timestamp.");
        });
    }

    public static void CheckCancellationOrdering()
    {
        WithRoot(root =>
        {
            var paths = Paths(root);
            var receive = Path.Combine(root, "received");
            var source = Path.Combine(root, "source.bin");
            byte[] bytes = [1, 3, 5, 7];
            File.WriteAllBytes(source, bytes);
            var coordinator = new TransferCoordinator(receive, paths);
            var first = coordinator.EnqueueOutgoing(source, TransferKind.File);
            var second = coordinator.EnqueueOutgoing(source, TransferKind.File);
            coordinator.TryOfferNextOutgoing();
            coordinator.Cancel(first.TransferId);
            // Late accepted/GET still belong to the original offered transfer.
            coordinator.AcceptOutgoing(first.TransferId, "source.bin");
            var body = coordinator.BeginOutgoingContent(first.TransferId);
            Ensure(!body.CancellationToken.IsCancellationRequested && body.Stream.ReadByte() == 1,
                "Sender cancellation must keep the body readable while WSS cancellation reaches the receiver.");
            var snapshot = coordinator.ReadAllSnapshots().Single(item => item.TransferId == first.TransferId);
            Ensure(snapshot.CancelPending && snapshot.State == TransferState.Transferring
                && snapshot.TerminalAt is null && !snapshot.Retryable,
                "The cancellation request must not invent a terminal or release its direction.");
            Ensure(coordinator.TryOfferNextOutgoing() is null, "A pending cancellation released FIFO early.");
            var rejected = false;
            try { coordinator.Retry(first.TransferId); }
            catch (TransferProtocolException exception) { rejected = exception.Code == "invalid_state"; }
            Ensure(rejected, "Retry must wait for the peer's canonical terminal.");
            var item = new TimelineItemViewModel(first.TransferId, first.Name, first.CreatedAt, true,
                "Transferring", "phone", kind: TransferKind.File,
                cancel: _ => Task.CompletedTask, retry: _ => Task.CompletedTask);
            item.ApplyTransferProgress(snapshot);
            Ensure(!item.CancelCommand.CanExecute(null) && !item.RetryCommand.CanExecute(null)
                && item.Status == "Canceling...", "Pending cancellation UI must disable cancel and retry.");
            body.Stream.Dispose();
            coordinator.CompleteOutgoingContent(first.TransferId, bytes.LongLength, Sha(bytes));
            Ensure(coordinator.TryOfferNextOutgoing() is null
                && coordinator.ReadReplayEnvelopes().Single().Type == "transfer.cancel",
                "A body finishing during cancel must retain its hash and replay only the cancellation request.");
            coordinator.ApplyRemoteCanceled(new TransferCanceled(first.TransferId, DateTimeOffset.UtcNow));
            Ensure(body.CancellationToken.IsCancellationRequested
                && coordinator.TryOfferNextOutgoing()?.TransferId == second.TransferId,
                "The peer's canonical cancellation must close the source and release FIFO.");
            coordinator.Cancel(second.TransferId, requestedByPeer: true);

            var completeWins = coordinator.EnqueueOutgoing(source, TransferKind.File);
            coordinator.TryOfferNextOutgoing();
            coordinator.AcceptOutgoing(completeWins.TransferId, "source.bin");
            var completeBody = coordinator.BeginOutgoingContent(completeWins.TransferId);
            coordinator.Cancel(completeWins.TransferId);
            // The peer's published result can beat the sender's EOF continuation.
            var completed = new TransferCompleted(completeWins.TransferId, bytes.LongLength,
                Sha(bytes), "published.bin", DateTimeOffset.UtcNow);
            var waiting = coordinator.ApplyRemoteCompletedAsync(completed, CancellationToken.None);
            Ensure(!waiting.IsCompleted, "Published completion must wait for the source proof, not invent a checksum.");
            coordinator.RecordOutgoingContentProof(completeWins.TransferId, bytes.LongLength, Sha(bytes));
            waiting.GetAwaiter().GetResult();
            Ensure(completeBody.CancellationToken.IsCancellationRequested
                && coordinator.ReadAllSnapshots().Single(row => row.TransferId == completeWins.TransferId)
                    .State == TransferState.Completed,
                "Verified published completion must settle cancel before the sender's EOF callback.");
            completeBody.Stream.Dispose();

            var failedFirst = coordinator.EnqueueOutgoing(source, TransferKind.File);
            coordinator.TryOfferNextOutgoing();
            coordinator.AcceptOutgoing(failedFirst.TransferId, "source.bin");
            var failedBody = coordinator.BeginOutgoingContent(failedFirst.TransferId);
            coordinator.Cancel(failedFirst.TransferId);
            coordinator.ApplyRemoteFailure(new TransferFailure(failedFirst.TransferId,
                "io_error", "Receiver failed before cancel.", true));
            Ensure(failedBody.CancellationToken.IsCancellationRequested
                && coordinator.ReadAllSnapshots().Single(row => row.TransferId == failedFirst.TransferId)
                    .ErrorCode == "io_error", "A real peer failure must close the waiting sender and keep its original result.");
            failedBody.Stream.Dispose();

            var incoming = Offer("cancel-receive.bin", bytes.LongLength);
            coordinator.RegisterIncomingOffer(incoming);
            var incomingBody = coordinator.BeginIncomingContent(incoming.TransferId, bytes.LongLength);
            incomingBody.Stream.Write(bytes.AsSpan(0, 1));
            coordinator.Cancel(incoming.TransferId);
            Ensure(!incomingBody.CancellationToken.IsCancellationRequested,
                "Receiver cancellation must send its WSS request before interrupting the sender's HTTP body.");
            incomingBody.Stream.Write(bytes.AsSpan(1));
            var incomingDigest = new TransferDigest(incoming.TransferId, "sha-256", Sha(bytes), bytes.LongLength);
            var earlyDigest = coordinator.ApplyIncomingDigest(incomingDigest);
            Ensure(earlyDigest.Completed is null && earlyDigest.Failure is null,
                "A digest crossing the receiver's local cancel must remain saved without publishing.");
            incomingBody.Stream.Dispose();
            var decision = coordinator.CompleteIncomingContent(incoming.TransferId, bytes.LongLength, Sha(bytes));
            var incomingPending = coordinator.ReadAllSnapshots().Single(row => row.TransferId == incoming.TransferId);
            var part = Path.Combine(receive, $".bob-{incoming.TransferId:D}.part");
            Ensure(incomingPending.CancelPending && incomingPending.State == TransferState.Verifying
                && incomingPending.TerminalAt is null && !incomingPending.Retryable
                && decision?.Completed is null && decision?.Failure is null && File.Exists(part)
                && !File.Exists(Path.Combine(receive, incoming.Name)),
                "A pending receiver must retain complete proof but leave the file unpublished.");
            Ensure(coordinator.RegisterIncomingOffer(incoming).Reply == IncomingOfferReply.Canceled,
                "A repeated offer must replay the pending cancel instead of accepting another HTTP body.");
            Ensure(coordinator.RegisterIncomingOffer(Offer("blocked-receive.bin", 0)).Failure?.Code == "direction_busy",
                "The receiver's pending cancellation must hold the incoming direction through EOF.");
            var incomingItem = new TimelineItemViewModel(incoming.TransferId, incoming.Name, incoming.CreatedAt, false,
                "Verifying", "phone", kind: TransferKind.File,
                cancel: _ => Task.CompletedTask, retry: _ => Task.CompletedTask);
            incomingItem.ApplyTransferProgress(incomingPending);
            Ensure(!incomingItem.CancelCommand.CanExecute(null) && !incomingItem.RetryCommand.CanExecute(null)
                && incomingItem.Status == "Canceling...", "Receiver pending UI must also disable cancel and retry.");
            coordinator.ApplyRemoteCanceled(new TransferCanceled(incoming.TransferId, DateTimeOffset.UtcNow));
            Ensure(incomingBody.CancellationToken.IsCancellationRequested && !File.Exists(part)
                && coordinator.RegisterIncomingOffer(Offer("next-receive.bin", 0)).Reply == IncomingOfferReply.Accepted,
                "The canonical result must close and remove the pending receive file and release its direction.");

            var receiveFailed = Offer("receive-failed.bin", null);
            var separate = new TransferCoordinator(Path.Combine(root, "failed-receive"));
            separate.RegisterIncomingOffer(receiveFailed);
            var receiveFailedBody = separate.BeginIncomingContent(receiveFailed.TransferId, null);
            receiveFailedBody.Stream.Write([9]);
            separate.Cancel(receiveFailed.TransferId);
            separate.ApplyRemoteFailure(new TransferFailure(receiveFailed.TransferId,
                "source_missing", "Sender failed before receiving cancel.", true));
            Ensure(receiveFailedBody.CancellationToken.IsCancellationRequested
                && separate.ReadAllSnapshots().Single().ErrorCode == "source_missing"
                && !File.Exists(Path.Combine(separate.ReceiveDirectory, $".bob-{receiveFailed.TransferId:D}.part")),
                "A real sender failure must settle receiver cancellation and discard the unverified part.");
            receiveFailedBody.Stream.Dispose();
        });
    }

    public static void CheckCancellationTimeoutRecovery()
    {
        WithRoot(root =>
        {
            Ensure(TransferCoordinator.CancellationConfirmationTimeout == TimeSpan.FromSeconds(60),
                "Both platforms must use the same bounded cancellation confirmation wait.");
            var paths = Paths(root);
            var receive = Path.Combine(root, "received");
            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [2, 4, 6]);
            var coordinator = new TransferCoordinator(receive, paths);
            var first = coordinator.EnqueueOutgoing(source, TransferKind.File);
            var second = coordinator.EnqueueOutgoing(source, TransferKind.File);
            coordinator.TryOfferNextOutgoing();
            coordinator.AcceptOutgoing(first.TransferId, "source.bin");
            var body = coordinator.BeginOutgoingContent(first.TransferId);
            coordinator.Cancel(first.TransferId);
            Ensure(coordinator.ExpireWaitingTransfers(DateTimeOffset.UtcNow.AddMinutes(1)),
                "An expired sender cancel must tell maintenance to close the control connection, including no-body waits.");
            var pending = coordinator.ReadAllSnapshots().Single(row => row.TransferId == first.TransferId);
            Ensure(body.CancellationToken.IsCancellationRequested && pending.CancelPending
                && pending.State == TransferState.Transferring && pending.ErrorCode is null
                && coordinator.TryOfferNextOutgoing() is null,
                "Cancel timeout must release the source but keep its request and FIFO without inventing Failed.");
            File.WriteAllBytes(source, [1, 3, 5]);
            coordinator.HandleSessionDisconnected();
            var restarted = new TransferCoordinator(receive, paths);
            var replay = restarted.ReadReplayEnvelopes().Single();
            Ensure(replay.Type == "transfer.cancel"
                && replay.Payload.GetProperty("transferId").GetGuid() == first.TransferId
                && restarted.TryOfferNextOutgoing() is null,
                "Restart must replay the same cancellation ID before releasing the next file.");
            restarted.ApplyRemoteCanceled(new TransferCanceled(first.TransferId, DateTimeOffset.UtcNow));
            Ensure(restarted.TryOfferNextOutgoing()?.TransferId == second.TransferId,
                "The replayed canonical cancellation must release the persisted FIFO.");
            restarted.Cancel(second.TransferId, requestedByPeer: true);
            restarted.AcknowledgeTerminal(second.TransferId, TransferState.Canceled);
            body.Stream.Dispose();

            // Older journals used provisional Canceled + CancelPending. Preserve
            // compatibility without treating that provisional timestamp as final.
            var history = new TransferHistoryStore(paths);
            var records = history.ReadAll();
            var legacy = records[0] with { State = TransferState.Canceled, CancelPending = true,
                TerminalAt = DateTimeOffset.UtcNow, Canceled = new TransferCanceled(first.TransferId, DateTimeOffset.UtcNow),
                TerminalAckPending = false };
            legacy = legacy with { TerminalAt = legacy.Canceled!.CanceledAt };
            history.Save([legacy, records[1]]);
            var oldJournal = new TransferCoordinator(receive, paths);
            Ensure(oldJournal.TryOfferNextOutgoing() is null
                && oldJournal.ReadReplayEnvelopes().Single().Type == "transfer.cancel",
                "Legacy canceled-pending history must remain replayable and hold its direction.");
            oldJournal.ApplyRemoteFailure(new TransferFailure(first.TransferId, "network_interrupted",
                "Stored peer failure.", true));
        });

        foreach (var stopReason in new[] { "timeout", "disconnect", "control_failure", "restart" })
        {
            WithRoot(root =>
            {
                var paths = Paths(root);
                var receive = Path.Combine(root, "received");
                var coordinator = new TransferCoordinator(receive, paths);
                var incoming = Offer("pending-receive.bin", 2);
                coordinator.RegisterIncomingOffer(incoming);
                var body = coordinator.BeginIncomingContent(incoming.TransferId, 2);
                body.Stream.Write([8]);
                coordinator.Cancel(incoming.TransferId);
                if (stopReason == "timeout")
                {
                    Ensure(coordinator.ExpireWaitingTransfers(DateTimeOffset.UtcNow.AddMinutes(1)),
                        "Receiver cancel timeout must request control disconnection without waiting for body EOF.");
                }
                else if (stopReason == "disconnect") { coordinator.HandleSessionDisconnected(); }
                else if (stopReason == "control_failure") { coordinator.SuspendPendingCancellation(incoming.TransferId); }
                else { body.Stream.Dispose(); }
                var restored = new TransferCoordinator(receive, paths);
                var pending = restored.ReadAllSnapshots().Single();
                var part = Path.Combine(receive, $".bob-{incoming.TransferId:D}.part");
                Ensure(pending.CancelPending && pending.State == TransferState.Transferring
                    && pending.TerminalAt is null && pending.ErrorCode is null && !File.Exists(part)
                    && (stopReason == "restart" || body.CancellationToken.IsCancellationRequested),
                    $"Receiver {stopReason} must release the part and preserve the nonterminal cancel request.");
                var replay = restored.ReadReplayEnvelopes().Single();
                Ensure(replay.Type == "transfer.cancel"
                    && replay.Payload.GetProperty("transferId").GetGuid() == incoming.TransferId,
                    "A receiver restart must replay the original cancellation ID, without inventing Failed.");
                var failureRejected = false;
                try { restored.FailLocal(incoming.TransferId, "network_interrupted", "Late EOF.", true); }
                catch (TransferProtocolException exception) { failureRejected = exception.Code == "invalid_state"; }
                Ensure(failureRejected, "Cancellation cleanup must not become a local network failure.");
                Ensure(restored.RegisterIncomingOffer(Offer("waiting-receive.bin", 0)).Failure?.Code == "direction_busy",
                    "Receiver cancel replay must continue to hold its direction after restart.");
                restored.ApplyRemoteCanceled(new TransferCanceled(incoming.TransferId, DateTimeOffset.UtcNow));
                Ensure(restored.RegisterIncomingOffer(Offer("next-receive.bin", 0)).Reply == IncomingOfferReply.Accepted,
                    "The peer's canonical cancellation must release the restored incoming direction.");
                body.Stream.Dispose();
                coordinator.SuspendPendingCancellation(incoming.TransferId);
            });
        }
    }

    private static TransferOffer Offer(string name, long? size) => new(Guid.NewGuid(), null,
        TransferKind.File, name, size, "application/octet-stream", DateTimeOffset.UtcNow);
    private static string Sha(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static AppPaths Paths(string root) => new(Path.Combine(root, "data"), Path.Combine(root, "identity"));
    private static void Ensure(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"bob-windows-reliability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
