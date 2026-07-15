using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Bob.Windows.Domain;
using Bob.Windows.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Bob.Windows.Transport;

public static class BobEndpoints
{
    public static void Map(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(BobProtocol.HeartbeatSeconds),
            KeepAliveTimeout = TimeSpan.FromSeconds(
                BobProtocol.PeerTimeoutSeconds - BobProtocol.HeartbeatSeconds)
        });

        app.MapGet("/bob/v1/info", (HttpContext context, ServerIdentity identity) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                serverId = identity.ServerId,
                name = Environment.MachineName,
                appVersion = AppVersion.Current,
                protocol = new
                {
                    min = BobProtocol.Version,
                    max = BobProtocol.Version
                }
            });
        });

        app.MapGet("/bob/v1/ws", async (
            HttpContext context,
            WebSocketSessionHandler handler) =>
        {
            await handler.HandleAsync(context);
        });

        app.MapPut(
            "/bob/v1/transfers/{transferId:guid}/content",
            HandleIncomingContentAsync);
        app.MapGet(
            "/bob/v1/transfers/{transferId:guid}/content",
            HandleOutgoingContentAsync);
    }

    private static async Task HandleIncomingContentAsync(
        HttpContext context,
        Guid transferId,
        TransferCoordinator transfers,
        SessionCoordinator sessions)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!TryValidateTransferHeader(context, transferId, out var headerError))
        {
            await WriteErrorAsync(context, headerError!);
            return;
        }

        var contentStoredForVerification = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = 0L;
            var lastProgress = Stopwatch.GetTimestamp();
            await using (var incoming = transfers.BeginIncomingContent(
                transferId,
                context.Request.ContentLength).Stream)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        var count = await context.Request.Body.ReadAsync(
                            buffer.AsMemory(),
                            context.RequestAborted);
                        if (count == 0)
                        {
                            break;
                        }

                        await incoming.WriteAsync(
                            buffer.AsMemory(0, count),
                            context.RequestAborted);
                        hash.AppendData(buffer, 0, count);
                        bytes = checked(bytes + count);

                        if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(250))
                        {
                            lastProgress = Stopwatch.GetTimestamp();
                            transfers.ReportProgress(transferId, bytes);
                            await TrySendControlAsync(
                                sessions,
                                TransferProtocolMessages.Progress(
                                    transferId,
                                    bytes,
                                    context.Request.ContentLength),
                                context.RequestAborted);
                        }
                    }

                    await incoming.FlushAsync(context.RequestAborted);
                    transfers.ReportProgress(transferId, bytes);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var sha256 = Base64Url(hash.GetHashAndReset());
            transfers.CompleteIncomingContent(transferId, bytes, sha256);
            contentStoredForVerification = true;
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    transferId,
                    bytesReceived = bytes,
                    state = "verifying"
                },
                cancellationToken: context.RequestAborted);
        }
        catch (TransferProtocolException exception)
        {
            await TrySendFailureForTerminalErrorAsync(
                sessions,
                transfers,
                exception,
                context.RequestAborted);
            await WriteErrorAsync(context, exception);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!contentStoredForVerification)
            {
                await FailInterruptedAsync(sessions, transfers, transferId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (contentStoredForVerification)
            {
                context.Abort();
                return;
            }

            var failure = transfers.FailLocal(
                transferId,
                exception is UnauthorizedAccessException ? "permission_denied" : "io_error",
                "Windows could not receive the content stream.",
                retryable: true);
            await TrySendControlAsync(
                sessions,
                TransferProtocolMessages.Failed(failure),
                CancellationToken.None);
            await WriteErrorAsync(
                context,
                new TransferProtocolException(
                    failure.Code,
                    failure.Message,
                    failure.Retryable,
                    transferId));
        }
    }

    private static async Task HandleOutgoingContentAsync(
        HttpContext context,
        Guid transferId,
        TransferCoordinator transfers,
        SessionCoordinator sessions)
    {
        context.Response.Headers.CacheControl = "no-store";
        var contentStarted = false;
        try
        {
            var content = await transfers.BeginOutgoingContentAsync(
                transferId,
                context.RequestAborted);
            contentStarted = true;
            await using var outgoing = content.Stream;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = content.Size;
            context.Response.ContentType = content.MediaType;
            context.Response.Headers["Content-Disposition"] =
                $"attachment; filename*=UTF-8''{Uri.EscapeDataString(content.Name)}";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Bob-Transfer-Id"] = transferId.ToString("D");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            var bytes = 0L;
            var lastProgress = Stopwatch.GetTimestamp();
            try
            {
                while (true)
                {
                    var count = await outgoing.ReadAsync(
                        buffer.AsMemory(),
                        context.RequestAborted);
                    if (count == 0)
                    {
                        break;
                    }

                    await context.Response.Body.WriteAsync(
                        buffer.AsMemory(0, count),
                        context.RequestAborted);
                    hash.AppendData(buffer, 0, count);
                    bytes = checked(bytes + count);
                    if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(250))
                    {
                        lastProgress = Stopwatch.GetTimestamp();
                        transfers.ReportProgress(transferId, bytes);
                    }
                }

                await context.Response.Body.FlushAsync(context.RequestAborted);
                transfers.ReportProgress(transferId, bytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var digest = transfers.CompleteOutgoingContent(
                transferId,
                bytes,
                Base64Url(hash.GetHashAndReset()));
            await TrySendControlAsync(
                sessions,
                TransferProtocolMessages.Digest(digest),
                CancellationToken.None);
        }
        catch (TransferProtocolException exception)
        {
            await TrySendFailureForTerminalErrorAsync(
                sessions,
                transfers,
                exception,
                context.RequestAborted);
            await WriteErrorAsync(context, exception);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (contentStarted)
            {
                await FailInterruptedAsync(sessions, transfers, transferId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var failure = transfers.FailLocal(
                transferId,
                exception is UnauthorizedAccessException ? "permission_denied" : "io_error",
                "Windows could not send the content stream.",
                retryable: true);
            await TrySendControlAsync(
                sessions,
                TransferProtocolMessages.Failed(failure),
                CancellationToken.None);
            await WriteErrorAsync(
                context,
                new TransferProtocolException(
                    failure.Code,
                    failure.Message,
                    failure.Retryable,
                    transferId));
        }
    }

    private static bool TryValidateTransferHeader(
        HttpContext context,
        Guid transferId,
        out TransferProtocolException? error)
    {
        error = null;
        if (!context.Request.Headers.TryGetValue("X-Bob-Transfer-Id", out var values)
            || values.Count != 1
            || !Guid.TryParse(values[0], out var headerTransferId)
            || headerTransferId != transferId)
        {
            error = new TransferProtocolException(
                "invalid_metadata",
                "X-Bob-Transfer-Id must match the route transferId.",
                retryable: false,
                transferId);
            return false;
        }

        return true;
    }

    private static async Task TrySendFailureForTerminalErrorAsync(
        SessionCoordinator sessions,
        TransferCoordinator transfers,
        TransferProtocolException exception,
        CancellationToken cancellationToken)
    {
        if (exception.TransferId is not Guid transferId
            || exception.Code is not (
                "size_mismatch"
                or "source_missing"
                or "permission_denied"
                or "io_error"))
        {
            return;
        }

        try
        {
            var failure = transfers.FailLocal(
                transferId,
                exception.Code,
                exception.Message,
                exception.Retryable);
            await TrySendControlAsync(
                sessions,
                TransferProtocolMessages.Failed(failure),
                cancellationToken);
        }
        catch (TransferProtocolException)
        {
        }
    }

    private static async Task FailInterruptedAsync(
        SessionCoordinator sessions,
        TransferCoordinator transfers,
        Guid transferId)
    {
        try
        {
            var failure = transfers.FailLocal(
                transferId,
                "network_interrupted",
                "The content stream was interrupted.",
                retryable: true);
            await TrySendControlAsync(
                sessions,
                TransferProtocolMessages.Failed(failure),
                CancellationToken.None);
        }
        catch (TransferProtocolException)
        {
        }
    }

    private static async Task TrySendControlAsync(
        SessionCoordinator sessions,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await sessions.SendEnvelopeAsync(envelope, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or WebSocketException
                or OperationCanceledException)
        {
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        TransferProtocolException exception)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return;
        }

        context.Response.StatusCode = exception.Code switch
        {
            "invalid_metadata" => StatusCodes.Status400BadRequest,
            "transfer_not_found" => StatusCodes.Status404NotFound,
            "invalid_state" or "wrong_direction" or "direction_busy" =>
                StatusCodes.Status409Conflict,
            "size_mismatch" => StatusCodes.Status422UnprocessableEntity,
            "insufficient_storage" => StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status500InternalServerError
        };
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = new
                {
                    code = exception.Code,
                    message = exception.Message,
                    retryable = exception.Retryable,
                    transferId = exception.TransferId
                }
            },
            cancellationToken: context.RequestAborted);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
