using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Bob.Windows.Transport;

public static class BobEndpoints
{
    public static readonly TimeSpan ContentIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ControlSendTimeout = TimeSpan.FromSeconds(2);

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
                protocol = new { min = BobProtocol.Version, max = BobProtocol.Version }
            });
        });
        app.MapGet("/bob/v1/ws", async (HttpContext context, WebSocketSessionHandler handler) =>
            await handler.HandleAsync(context));
        app.MapPut("/bob/v1/transfers/{transferId:guid}/content", HandleIncomingContentAsync);
        app.MapGet("/bob/v1/transfers/{transferId:guid}/content", HandleOutgoingContentAsync);
    }

    private static async Task HandleIncomingContentAsync(HttpContext context, Guid transferId,
        TransferCoordinator transfers, SessionCoordinator sessions)
    {
        context.Response.Headers.CacheControl = "no-store";
        DisableDefaultBodyLimits(context);
        if (!TryValidateTransferHeader(context, transferId, out var headerError))
        {
            await WriteErrorAsync(context, headerError!);
            return;
        }
        var contentStarted = false;
        var contentStored = false;
        var readingNetwork = true;
        CancellationToken transferCancellation = default;
        try
        {
            var content = transfers.BeginIncomingContent(transferId, context.Request.ContentLength);
            contentStarted = true;
            transferCancellation = content.CancellationToken;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, transferCancellation);
            idle.CancelAfter(ContentIdleTimeout);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = 0L;
            var lastProgress = Stopwatch.GetTimestamp();
            await using (var incoming = content.Stream)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        readingNetwork = true;
                        var count = await context.Request.Body.ReadAsync(buffer.AsMemory(), idle.Token);
                        if (count == 0) { break; }
                        idle.CancelAfter(ContentIdleTimeout);
                        readingNetwork = false;
                        await incoming.WriteAsync(buffer.AsMemory(0, count), idle.Token);
                        hash.AppendData(buffer, 0, count);
                        bytes = checked(bytes + count);
                        idle.CancelAfter(ContentIdleTimeout);
                        if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(250))
                        {
                            lastProgress = Stopwatch.GetTimestamp();
                            transfers.ReportProgress(transferId, bytes);
                            await TrySendControlAsync(sessions,
                                TransferProtocolMessages.Progress(transferId, bytes, content.ExpectedSize),
                                idle.Token);
                        }
                    }
                    readingNetwork = false;
                    await incoming.FlushAsync(idle.Token);
                    if (incoming is TransferContentStream file) { file.FlushToDisk(); }
                    transfers.ReportProgress(transferId, bytes);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            var decision = transfers.CompleteIncomingContent(transferId, bytes,
                Base64Url(hash.GetHashAndReset()));
            contentStored = true;
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(new
            {
                transferId,
                bytesReceived = bytes,
                state = "verifying"
            }, cancellationToken: context.RequestAborted);
            if (decision is not null)
            {
                await SendStoredTerminalAndAdvanceAsync(sessions, transfers, transferId);
            }
        }
        catch (TransferHistoryStoreException)
        {
            sessions.AbortActiveConnection();
            context.Abort();
        }
        catch (TransferProtocolException exception)
        {
            await HandleProtocolErrorAsync(context, sessions, transfers, exception);
        }
        catch (OperationCanceledException) when (contentStarted)
        {
            if (!contentStored)
            {
                await FailStreamAsync(sessions, transfers, transferId, "network_interrupted",
                    "The content stream was interrupted or stopped making progress.");
            }
            context.Abort();
        }
        catch (ObjectDisposedException) when (transferCancellation.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!contentStored && contentStarted)
            {
                var code = IoErrorCode(exception, networkOperation: readingNetwork);
                await FailStreamAsync(sessions, transfers, transferId, code,
                    "Windows could not receive the content stream.");
                await WriteErrorAsync(context, new TransferProtocolException(code,
                    "Windows could not receive the content stream.", true, transferId));
            }
            else { context.Abort(); }
        }
        finally
        {
            if (contentStarted) { transfers.ReleaseContent(transferId); }
        }
    }

    private static async Task HandleOutgoingContentAsync(HttpContext context, Guid transferId,
        TransferCoordinator transfers, SessionCoordinator sessions)
    {
        context.Response.Headers.CacheControl = "no-store";
        DisableDefaultBodyLimits(context);
        var contentStarted = false;
        var contentStored = false;
        var writingNetwork = false;
        CancellationToken transferCancellation = default;
        try
        {
            var content = await transfers.BeginOutgoingContentAsync(transferId, context.RequestAborted);
            contentStarted = true;
            transferCancellation = content.CancellationToken;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, transferCancellation);
            idle.CancelAfter(ContentIdleTimeout);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = content.Size;
            context.Response.ContentType = content.MediaType;
            context.Response.Headers["Content-Disposition"] =
                $"attachment; filename*=UTF-8''{Uri.EscapeDataString(content.Name)}";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Bob-Transfer-Id"] = transferId.ToString("D");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = 0L;
            var lastProgress = Stopwatch.GetTimestamp();
            await using (var outgoing = content.Stream)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        writingNetwork = false;
                        var count = await outgoing.ReadAsync(buffer.AsMemory(), idle.Token);
                        if (count == 0)
                        {
                            if (content.Size == 0)
                            {
                                transfers.RecordOutgoingContentProof(transferId, 0, Base64Url(hash.GetCurrentHash()));
                            }
                            break;
                        }
                        idle.CancelAfter(ContentIdleTimeout);
                        writingNetwork = true;
                        hash.AppendData(buffer, 0, count);
                        bytes = checked(bytes + count);
                        if (bytes == content.Size)
                        {
                            transfers.RecordOutgoingContentProof(transferId, bytes, Base64Url(hash.GetCurrentHash()));
                        }
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), idle.Token);
                        idle.CancelAfter(ContentIdleTimeout);
                        if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(250))
                        {
                            lastProgress = Stopwatch.GetTimestamp();
                            transfers.ReportProgress(transferId, bytes);
                        }
                    }
                    writingNetwork = true;
                    await context.Response.Body.FlushAsync(idle.Token);
                    transfers.ReportProgress(transferId, bytes);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            var digest = transfers.CompleteOutgoingContent(transferId, bytes,
                Base64Url(hash.GetHashAndReset()));
            contentStored = true;
            var pendingCancel = transfers.ReadTerminalOrCancelEnvelope(transferId);
            await TrySendControlAsync(sessions, pendingCancel?.Type == "transfer.cancel"
                    ? pendingCancel : TransferProtocolMessages.Digest(digest),
                CancellationToken.None);
        }
        catch (TransferHistoryStoreException)
        {
            sessions.AbortActiveConnection();
            context.Abort();
        }
        catch (TransferProtocolException exception)
        {
            await HandleProtocolErrorAsync(context, sessions, transfers, exception);
        }
        catch (OperationCanceledException)
        {
            if (contentStarted && !contentStored)
            {
                await FailStreamAsync(sessions, transfers, transferId, "network_interrupted",
                    "The content stream was interrupted or stopped making progress.");
            }
            context.Abort();
        }
        catch (ObjectDisposedException) when (transferCancellation.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (contentStarted && !contentStored)
            {
                var code = IoErrorCode(exception, networkOperation: writingNetwork);
                await FailStreamAsync(sessions, transfers, transferId, code,
                    "Windows could not send the content stream.");
                await WriteErrorAsync(context, new TransferProtocolException(code,
                    "Windows could not send the content stream.", true, transferId));
            }
            else { context.Abort(); }
        }
        finally
        {
            if (contentStarted) { transfers.ReleaseContent(transferId); }
        }
    }

    private static void DisableDefaultBodyLimits(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = null;
        }
        if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } requestRate)
        {
            requestRate.MinDataRate = null;
        }
        if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } responseRate)
        {
            responseRate.MinDataRate = null;
        }
    }

    private static bool TryValidateTransferHeader(HttpContext context, Guid transferId,
        out TransferProtocolException? error)
    {
        error = null;
        if (!context.Request.Headers.TryGetValue("X-Bob-Transfer-Id", out var values)
            || values.Count != 1 || !Guid.TryParse(values[0], out var headerTransferId)
            || headerTransferId != transferId)
        {
            error = new TransferProtocolException("invalid_metadata",
                "X-Bob-Transfer-Id must match the route transferId.", false, transferId);
            return false;
        }
        return true;
    }

    private static async Task HandleProtocolErrorAsync(HttpContext context, SessionCoordinator sessions,
        TransferCoordinator transfers, TransferProtocolException exception)
    {
        if (exception.TransferId is Guid transferId && exception.Code is
            "size_mismatch" or "source_missing" or "permission_denied" or "io_error"
                or "insufficient_storage")
        {
            await FailStreamAsync(sessions, transfers, transferId, exception.Code, exception.Message);
        }
        await WriteErrorAsync(context, exception);
    }

    private static async Task FailStreamAsync(SessionCoordinator sessions, TransferCoordinator transfers,
        Guid transferId, string code, string message)
    {
        try
        {
            if (transfers.ReadTerminalOrCancelEnvelope(transferId) is null)
            {
                transfers.FailLocal(transferId, code, message, retryable: true);
            }
            await SendStoredTerminalAndAdvanceAsync(sessions, transfers, transferId);
        }
        catch (TransferProtocolException) { }
        catch (TransferHistoryStoreException) { sessions.AbortActiveConnection(); }
    }

    private static async Task SendStoredTerminalAndAdvanceAsync(SessionCoordinator sessions,
        TransferCoordinator transfers, Guid transferId)
    {
        var terminal = transfers.ReadTerminalOrCancelEnvelope(transferId);
        if (terminal is not null)
        {
            await TrySendControlAsync(sessions, terminal, CancellationToken.None);
            try { await sessions.SendNextTransferOfferAsync(transfers); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                or WebSocketException or OperationCanceledException) { }
        }
    }

    private static async Task TrySendControlAsync(SessionCoordinator sessions, ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ControlSendTimeout);
        try { await sessions.SendEnvelopeAsync(envelope, timeout.Token); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or WebSocketException or OperationCanceledException) { }
    }

    private static string IoErrorCode(Exception exception, bool networkOperation) => exception switch
    {
        UnauthorizedAccessException => "permission_denied",
        IOException io when (io.HResult & 0xFFFF) is 39 or 112 => "insufficient_storage",
        _ when networkOperation => "network_interrupted",
        _ => "io_error"
    };

    private static async Task WriteErrorAsync(HttpContext context, TransferProtocolException exception)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return;
        }
        context.Response.ContentLength = null;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.Remove("Content-Disposition");
        context.Response.StatusCode = exception.Code switch
        {
            "invalid_metadata" => StatusCodes.Status400BadRequest,
            "transfer_not_found" => StatusCodes.Status404NotFound,
            "invalid_state" or "wrong_direction" or "direction_busy" => StatusCodes.Status409Conflict,
            "size_mismatch" => StatusCodes.Status422UnprocessableEntity,
            "insufficient_storage" => StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status500InternalServerError
        };
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = exception.Code,
                message = exception.Message,
                retryable = exception.Retryable,
                transferId = exception.TransferId
            }
        }, cancellationToken: context.RequestAborted);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
