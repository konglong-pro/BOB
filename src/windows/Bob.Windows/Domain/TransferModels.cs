namespace Bob.Windows.Domain;

public enum TransferDirection
{
    AndroidToWindows,
    WindowsToAndroid
}

public enum TransferKind
{
    File,
    Image
}

public enum TransferState
{
    Queued,
    Offered,
    Accepted,
    Transferring,
    Verifying,
    Completed,
    Failed,
    Canceled
}

public sealed record TransferOffer(
    Guid TransferId,
    Guid? RetryOf,
    TransferKind Kind,
    string Name,
    long? Size,
    string MediaType,
    DateTimeOffset CreatedAt);

public sealed record TransferDigest(
    Guid TransferId,
    string Algorithm,
    string Value,
    long Bytes);

public sealed record TransferCompleted(
    Guid TransferId,
    long Bytes,
    string Sha256,
    string StoredName,
    DateTimeOffset CompletedAt);

public sealed record TransferFailure(
    Guid TransferId,
    string Code,
    string Message,
    bool Retryable);

public sealed record TransferSnapshot(
    Guid TransferId,
    long Revision,
    TransferDirection Direction,
    TransferKind Kind,
    string Name,
    long? Size,
    string MediaType,
    DateTimeOffset CreatedAt,
    TransferState State,
    long BytesTransferred,
    string? PlannedName,
    string? StoredName,
    string? ErrorCode,
    string? ErrorMessage,
    string? LocalPath);

public enum IncomingOfferReply
{
    Accepted,
    Completed,
    Failed
}

public sealed record IncomingOfferDecision(
    IncomingOfferReply Reply,
    string? PlannedName = null,
    TransferCompleted? Completed = null,
    TransferFailure? Failure = null);

public sealed record IncomingDigestDecision(
    TransferCompleted? Completed = null,
    TransferFailure? Failure = null);

public sealed record IncomingContent(
    Stream Stream,
    long? ExpectedSize);

public sealed record OutgoingContent(
    Stream Stream,
    string Name,
    string MediaType,
    long Size);

public sealed class TransferProtocolException : Exception
{
    public TransferProtocolException(
        string code,
        string message,
        bool retryable,
        Guid? transferId = null)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
        TransferId = transferId;
    }

    public string Code { get; }

    public bool Retryable { get; }

    public Guid? TransferId { get; }
}
