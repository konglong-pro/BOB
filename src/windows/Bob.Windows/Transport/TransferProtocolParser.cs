using System.Globalization;
using System.Text;
using System.Text.Json;
using Bob.Windows.Domain;

namespace Bob.Windows.Transport;

internal static class TransferProtocolParser
{
    public static TransferOffer ParseOffer(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        var retryOf = OptionalGuid(payload, "retryOf");
        var kindText = RequiredString(payload, "kind");
        var kind = kindText switch
        {
            "file" => TransferKind.File,
            "image" => TransferKind.Image,
            _ => throw Error(
                "unsupported_kind",
                "transfer.offer kind must be file or image.",
                retryable: false,
                transferId)
        };
        var name = RequiredString(payload, "name");
        var size = OptionalNonNegativeInt64(payload, "size");
        var mediaType = RequiredString(payload, "mediaType");
        var createdAt = RequiredUtcInstant(payload, "createdAt");
        return new TransferOffer(
            transferId,
            retryOf,
            kind,
            name,
            size,
            mediaType,
            createdAt);
    }

    public static (Guid TransferId, string PlannedName) ParseAccepted(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        return (transferId, RequiredBasename(payload, "plannedName", transferId));
    }

    public static TransferDigest ParseDigest(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        var algorithm = RequiredString(payload, "algorithm");
        var value = RequiredString(payload, "value");
        if (!IsSha256Base64Url(value))
        {
            throw Error(
                "invalid_metadata",
                "transfer.digest value must be an unpadded SHA-256 base64url value.",
                retryable: false,
                transferId);
        }

        return new TransferDigest(
            transferId,
            algorithm,
            value,
            RequiredNonNegativeInt64(payload, "bytes", transferId));
    }

    public static TransferCompleted ParseCompleted(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        var sha256 = RequiredString(payload, "sha256");
        if (!IsSha256Base64Url(sha256))
        {
            throw Error(
                "invalid_metadata",
                "transfer.completed sha256 must be an unpadded SHA-256 base64url value.",
                retryable: false,
                transferId);
        }

        return new TransferCompleted(
            transferId,
            RequiredNonNegativeInt64(payload, "bytes", transferId),
            sha256,
            RequiredBasename(payload, "storedName", transferId),
            RequiredUtcInstant(payload, "completedAt"));
    }

    public static (Guid TransferId, TransferState State) ParseTerminalAck(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        var state = RequiredString(payload, "state") switch
        {
            "completed" => TransferState.Completed,
            "failed" => TransferState.Failed,
            "canceled" => TransferState.Canceled,
            _ => throw Error(
                "invalid_metadata",
                "terminalAck state is not terminal.",
                retryable: false,
                transferId)
        };
        return (transferId, state);
    }

    public static TransferFailure ParseFailure(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        var code = RequiredString(payload, "code");
        var message = RequiredString(payload, "message");
        if (Encoding.UTF8.GetByteCount(message) > 4 * 1024)
        {
            throw Error(
                "invalid_metadata",
                "transfer.failed message exceeds 4 KiB.",
                retryable: false,
                transferId);
        }
        if (!payload.TryGetProperty("retryable", out var retryableValue)
            || retryableValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Error(
                "invalid_message",
                "transfer.failed retryable must be a boolean.",
                retryable: false,
                transferId);
        }

        return new TransferFailure(
            transferId,
            code,
            message,
            retryableValue.GetBoolean());
    }

    public static (Guid TransferId, long Bytes, long? Total) ParseProgress(JsonElement payload)
    {
        var transferId = RequiredGuid(payload, "transferId");
        var bytes = RequiredNonNegativeInt64(payload, "bytes", transferId);
        var total = OptionalNonNegativeInt64(payload, "total");
        if (total is not null && bytes > total)
        {
            throw Error(
                "invalid_metadata",
                "transfer.progress bytes exceeds total.",
                retryable: false,
                transferId);
        }

        return (transferId, bytes, total);
    }

    private static Guid RequiredGuid(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetGuid(out var result)
            || result == Guid.Empty)
        {
            throw Error(
                "invalid_message",
                $"{propertyName} must be a non-empty UUID.",
                retryable: false,
                transferId: null);
        }

        return result;
    }

    private static Guid? OptionalGuid(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            throw Error(
                "invalid_message",
                $"{propertyName} is required.",
                retryable: false,
                transferId: null);
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && value.TryGetGuid(out var result)
            && result != Guid.Empty)
        {
            return result;
        }

        throw Error(
            "invalid_message",
            $"{propertyName} must be null or a non-empty UUID.",
            retryable: false,
            transferId: null);
    }

    private static string RequiredString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error(
                "invalid_message",
                $"{propertyName} must be a non-empty string.",
                retryable: false,
                transferId: null);
        }

        return value.GetString()!;
    }

    private static string RequiredBasename(
        JsonElement payload,
        string propertyName,
        Guid transferId)
    {
        var value = RequiredString(payload, propertyName);
        if (value.Contains('/') || value.Contains('\\') || value.Contains('\0'))
        {
            throw Error(
                "invalid_metadata",
                $"{propertyName} must be a basename.",
                retryable: false,
                transferId);
        }

        return value;
    }

    private static long RequiredNonNegativeInt64(
        JsonElement payload,
        string propertyName,
        Guid transferId)
    {
        if (!payload.TryGetProperty(propertyName, out var value)
            || !value.TryGetInt64(out var result)
            || result < 0)
        {
            throw Error(
                "invalid_message",
                $"{propertyName} must be a non-negative int64.",
                retryable: false,
                transferId);
        }

        return result;
    }

    private static long? OptionalNonNegativeInt64(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value))
        {
            throw Error(
                "invalid_message",
                $"{propertyName} is required.",
                retryable: false,
                transferId: null);
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.TryGetInt64(out var result) && result >= 0)
        {
            return result;
        }

        throw Error(
            "invalid_message",
            $"{propertyName} must be null or a non-negative int64.",
            retryable: false,
            transferId: null);
    }

    private static DateTimeOffset RequiredUtcInstant(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result)
            || result.Offset != TimeSpan.Zero)
        {
            throw Error(
                "invalid_message",
                $"{propertyName} must be a UTC timestamp.",
                retryable: false,
                transferId: null);
        }

        return result;
    }

    private static bool IsSha256Base64Url(string value) =>
        value.Length == 43
        && value.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_');

    private static TransferProtocolException Error(
        string code,
        string message,
        bool retryable,
        Guid? transferId) =>
        new(code, message, retryable, transferId);
}
