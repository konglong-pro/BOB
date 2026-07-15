using Bob.Windows.Domain;

namespace Bob.Windows.Transport;

internal static class TransferProtocolMessages
{
    public static ProtocolEnvelope Offer(TransferOffer offer) =>
        ProtocolJson.Create(
            "transfer.offer",
            new
            {
                transferId = offer.TransferId,
                retryOf = offer.RetryOf,
                kind = Kind(offer.Kind),
                name = offer.Name,
                size = offer.Size,
                mediaType = offer.MediaType,
                createdAt = offer.CreatedAt
            });

    public static ProtocolEnvelope Accepted(Guid transferId, string plannedName) =>
        ProtocolJson.Create(
            "transfer.accepted",
            new
            {
                transferId,
                plannedName
            });

    public static ProtocolEnvelope Digest(TransferDigest digest) =>
        ProtocolJson.Create(
            "transfer.digest",
            new
            {
                transferId = digest.TransferId,
                algorithm = digest.Algorithm,
                value = digest.Value,
                bytes = digest.Bytes
            });

    public static ProtocolEnvelope Completed(TransferCompleted completed) =>
        ProtocolJson.Create(
            "transfer.completed",
            new
            {
                transferId = completed.TransferId,
                bytes = completed.Bytes,
                sha256 = completed.Sha256,
                storedName = completed.StoredName,
                completedAt = completed.CompletedAt
            });

    public static ProtocolEnvelope Failed(TransferFailure failure) =>
        ProtocolJson.Create(
            "transfer.failed",
            new
            {
                transferId = failure.TransferId,
                code = failure.Code,
                message = failure.Message,
                retryable = failure.Retryable
            });

    public static ProtocolEnvelope TerminalAck(Guid transferId, TransferState state) =>
        ProtocolJson.Create(
            "transfer.terminalAck",
            new
            {
                transferId,
                state = State(state)
            });

    public static ProtocolEnvelope Progress(Guid transferId, long bytes, long? total) =>
        ProtocolJson.Create(
            "transfer.progress",
            new
            {
                transferId,
                bytes,
                total
            });

    public static string Kind(TransferKind kind) => kind switch
    {
        TransferKind.File => "file",
        TransferKind.Image => "image",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string State(TransferState state) => state switch
    {
        TransferState.Completed => "completed",
        TransferState.Failed => "failed",
        TransferState.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
