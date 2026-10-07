using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bob.Windows;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Security;
using Bob.Windows.Transport;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class RealTransportChecks
{
    public static async Task CheckPinnedTlsAsync()
    {
        await using var host = await TestBobHost.StartAsync(tls: true);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && ServerIdentity.CalculatePin(certificate) == host.CertificatePin
        };
        using var client = new HttpClient(handler) { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        try { response = await client.GetAsync("/bob/v1/info", timeout.Token); }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"TLS handshake failed: {exception}", exception);
        }
        using var ownedResponse = response;
        Ensure(response.StatusCode == HttpStatusCode.OK, "The temporary test identity must negotiate HTTPS.");
        using var socket = host.CreateWebSocketClient();
        await OpenSessionAsync(host, socket, timeout.Token);
        await SendSentinelAsync(socket, timeout.Token);
        Ensure((await ReceiveAsync(socket, timeout.Token)).Type == "text.ack", "Pinned WSS must exchange text.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "tls_complete", timeout.Token);
        using var wrongPinHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && ServerIdentity.CalculatePin(certificate) == new string('A', 43)
        };
        using var wrongPinClient = new HttpClient(wrongPinHandler) { BaseAddress = host.BaseUri };
        var rejected = false;
        try { using var rejectedResponse = await wrongPinClient.GetAsync("/bob/v1/info", timeout.Token); }
        catch (HttpRequestException) { rejected = true; }
        Ensure(rejected, "Wrong certificate pin must fail the real TLS handshake.");
    }

    public static async Task CheckLargeStreamingRoundTripAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await OpenSessionAsync(host, socket, timeout.Token);
        const long size = 64L * 1024 * 1024;
        var (offer, sha256, storedPath) = await UploadSyntheticAsync(host, client, size,
            knownSize: true, timeout.Token);
        Ensure(new FileInfo(storedPath).Length == size, "The large upload was truncated.");
        var outgoing = host.Transfers.EnqueueOutgoing(storedPath, TransferKind.File);
        await host.Sessions.SendNextTransferOfferAsync(host.Transfers, timeout.Token);
        var offered = await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
        Ensure(offered.Payload.GetProperty("transferId").GetGuid() == outgoing.TransferId,
            "Large GET must refer to the queued large file.");
        await SendAcceptedAsync(socket, outgoing, timeout.Token);
        using var response = await client.GetAsync($"/bob/v1/transfers/{outgoing.TransferId:D}/content",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Ensure(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == size,
            "Large GET must preserve its full Content-Length.");
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var bytes = 0L;
        while (true)
        {
            var count = await body.ReadAsync(buffer, timeout.Token);
            if (count == 0) { break; }
            hash.AppendData(buffer, 0, count);
            bytes += count;
        }
        Ensure(bytes == size && Base64Url(hash.GetHashAndReset()) == sha256,
            "The complete large GET must have the same bytes and SHA-256 as the streamed PUT.");
        var digest = await ReceiveTypeAsync(socket, "transfer.digest", timeout.Token);
        Ensure(digest.Payload.GetProperty("bytes").GetInt64() == size
            && digest.Payload.GetProperty("value").GetString() == sha256,
            "The large GET control digest changed the byte count or hash.");
        await SendCompletedAsync(socket, outgoing.TransferId, size, sha256, timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.terminalAck", timeout.Token);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "large_complete", timeout.Token);
        Console.WriteLine($"  streamed {size} bytes each direction with a 128 KiB receive buffer");
    }

    public static async Task CheckGigabyteChunkedUploadAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        const long size = 1024L * 1024 * 1024;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseline = process.PeakWorkingSet64;
        var (_, _, storedPath) = await UploadSyntheticAsync(host, client, size,
            knownSize: false, timeout.Token);
        process.Refresh();
        var increase = Math.Max(0, process.PeakWorkingSet64 - baseline);
        Ensure(increase < 192L * 1024 * 1024,
            $"A 1 GiB body increased peak memory by {increase} bytes; the body must stay streamed.");
        Ensure(new FileInfo(storedPath).Length == size, "Chunked upload was truncated.");
        using var sample = File.OpenRead(storedPath);
        sample.Position = size - 1;
        Ensure(sample.ReadByte() == 0xA5, "Chunked publication lost its final byte.");
        Console.WriteLine($"  streamed {size} chunked bytes; peak working-set increase {increase} bytes");
    }

    public static async Task CheckInt64DownloadAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = Path.Combine(host.TemporaryRoot, "over-int32.bin");
        const long size = (long)int.MaxValue + 1024 * 1024;
        using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Ensure(DeviceIoControl(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0,
                IntPtr.Zero, 0, out _, IntPtr.Zero), "The int64 fixture must use a real sparse file.");
            file.SetLength(size);
            file.Position = size - 1;
            file.WriteByte(0x7F);
        }
        var offer = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        Ensure(offer.Size == size && new FileInfo(source).Length == size,
            "The actual file length must exceed int32 without truncation.");
        host.Transfers.TryOfferNextOutgoing();
        host.Transfers.AcceptOutgoing(offer.TransferId, offer.Name);
        using var response = await client.GetAsync($"/bob/v1/transfers/{offer.TransferId:D}/content",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Ensure(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == size,
            "The real GET header must contain the untruncated int64 length.");
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        var sample = new byte[4096];
        Ensure(await body.ReadAsync(sample, timeout.Token) > 0, "The sparse source did not stream.");
        host.Transfers.Cancel(offer.TransferId, requestedByPeer: true);
        using var reopened = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Ensure(reopened.Length == size, "Cancel must close the source handle without changing its length.");
    }

    public static async Task CheckTextOutboxRestartAsync()
    {
        var root = NewReliabilityRoot();
        var textId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        const string text = "offline durable outbox";
        try
        {
            await using (var offline = await TestBobHost.StartAsync(root, durable: true, preserveRoot: true))
            {
                await offline.Sessions.SendTextAsync(textId, text, createdAt);
                Ensure(new TextHistoryStore(DataPaths(root)).ReadAll().Single().Status == "Queued",
                    "Offline text must be saved before SendTextAsync returns.");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (var online = await TestBobHost.StartAsync(root, durable: true, preserveRoot: true))
            {
                using var socket = online.CreateWebSocketClient();
                await OpenSessionAsync(online, socket, timeout.Token);
                var replay = await ReceiveTypeAsync(socket, "text.send", timeout.Token);
                Ensure(replay.Payload.GetProperty("textId").GetGuid() == textId
                    && replay.Payload.GetProperty("text").GetString() == text
                    && replay.Payload.GetProperty("createdAt").GetDateTimeOffset() == createdAt,
                    "Restart must replay the same text ID and immutable body.");
                var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                online.Sessions.TextAcknowledged += (_, id) =>
                {
                    if (id != textId) { return; }
                    Ensure(new TextHistoryStore(DataPaths(root)).ReadAll().Single(record => record.TextId == id)
                        .Status == "Delivered", "Delivered notification occurred before persistence.");
                    acknowledged.TrySetResult();
                };
                await SendAsync(socket, ProtocolJson.Create("text.ack", new
                {
                    textId,
                    storedAt = DateTimeOffset.UtcNow
                }), timeout.Token);
                await acknowledged.Task.WaitAsync(timeout.Token);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ack_saved", timeout.Token);
            }
            await using var last = await TestBobHost.StartAsync(root, durable: true, preserveRoot: true);
            using var finalSocket = last.CreateWebSocketClient();
            await OpenSessionAsync(last, finalSocket, timeout.Token);
            await SendSentinelAsync(finalSocket, timeout.Token);
            Ensure((await ReceiveAsync(finalSocket, timeout.Token)).Type == "text.ack",
                "Delivered text must not be replayed again after restart.");
            await finalSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
        }
        finally { DeleteReliabilityRoot(root); }
    }

    public static async Task CheckTerminalReplayAfterRestartAsync()
    {
        var root = NewReliabilityRoot();
        var transferId = Guid.NewGuid();
        ProtocolEnvelope original;
        var bytes = "pending terminal"u8.ToArray();
        var sha256 = Base64Url(SHA256.HashData(bytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await using (var first = await TestBobHost.StartAsync(root, durable: true, preserveRoot: true))
            {
                using var socket = first.CreateWebSocketClient();
                using var client = new HttpClient { BaseAddress = first.BaseUri };
                await OpenSessionAsync(first, socket, timeout.Token);
                await SendAsync(socket, ProtocolJson.Create("transfer.offer", new
                {
                    transferId,
                    retryOf = (Guid?)null,
                    kind = "file",
                    name = "terminal.bin",
                    size = bytes.LongLength,
                    mediaType = "application/octet-stream",
                    createdAt = DateTimeOffset.UtcNow
                }), timeout.Token);
                await ReceiveTypeAsync(socket, "transfer.accepted", timeout.Token);
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"/bob/v1/transfers/{transferId:D}/content") { Content = new ByteArrayContent(bytes) };
                request.Headers.Add("X-Bob-Transfer-Id", transferId.ToString("D"));
                using var response = await client.SendAsync(request, timeout.Token);
                Ensure(response.StatusCode == HttpStatusCode.Accepted, "The fixture upload failed.");
                await SendAsync(socket, ProtocolJson.Create("transfer.digest", new
                {
                    transferId,
                    algorithm = "sha-256",
                    value = sha256,
                    bytes = bytes.LongLength
                }), timeout.Token);
                original = await ReceiveTypeAsync(socket, "transfer.completed", timeout.Token);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "lose_ack", timeout.Token);
            }
            await using var second = await TestBobHost.StartAsync(root, durable: true, preserveRoot: true);
            using var replaySocket = second.CreateWebSocketClient();
            var rows = await OpenAndCaptureSnapshotsAsync(second, replaySocket, timeout.Token);
            var row = rows.Single(item => item.GetProperty("transferId").GetGuid() == transferId);
            Ensure(row.GetProperty("terminalAckPending").GetBoolean()
                && row.GetProperty("state").GetString() == "completed",
                "The restart snapshot must expose the unconfirmed durable terminal.");
            var replay = await ReceiveTypeAsync(replaySocket, "transfer.completed", timeout.Token);
            Ensure(replay.Payload.GetRawText() == original.Payload.GetRawText(),
                "Terminal replay must preserve the original payload including completedAt.");
            await SendAsync(replaySocket, ProtocolJson.Create("transfer.terminalAck", new
            {
                transferId,
                state = "completed"
            }), timeout.Token);
            await SendSentinelAsync(replaySocket, timeout.Token);
            await ReceiveTypeAsync(replaySocket, "text.ack", timeout.Token);
            Ensure(!new TransferHistoryStore(DataPaths(root)).ReadAll().Single().TerminalAckPending,
                "TerminalAck did not durably clear the delivery marker.");
            await replaySocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
        }
        finally { DeleteReliabilityRoot(root); }
    }

    public static async Task CheckSourceFailureAdvancesQueueAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = Path.Combine(host.TemporaryRoot, "missing.bin");
        var nextPath = Path.Combine(host.TemporaryRoot, "next.bin");
        File.WriteAllBytes(source, [1]);
        File.WriteAllBytes(nextPath, [2]);
        var first = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        var next = host.Transfers.EnqueueOutgoing(nextPath, TransferKind.File);
        await OpenSessionAsync(host, socket, timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
        await SendAcceptedAsync(socket, first, timeout.Token);
        File.Delete(source);
        using var response = await client.GetAsync($"/bob/v1/transfers/{first.TransferId:D}/content", timeout.Token);
        Ensure(response.StatusCode == HttpStatusCode.InternalServerError, "Missing source must fail GET.");
        var failure = await ReceiveTypeAsync(socket, "transfer.failed", timeout.Token);
        Ensure(failure.Payload.GetProperty("code").GetString() == "source_missing",
            "Missing source must map to source_missing.");
        var offered = await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
        Ensure(offered.Payload.GetProperty("transferId").GetGuid() == next.TransferId,
            "A failed item must release FIFO immediately without waiting for terminalAck.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
    }

    public static async Task CheckRepeatedReconnectQueueAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var source = Path.Combine(host.TemporaryRoot, "waiting.bin");
        File.WriteAllBytes(source, [1, 2]);
        var first = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        var second = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        for (var index = 0; index < 4; index++)
        {
            using var socket = host.CreateWebSocketClient();
            await OpenSessionAsync(host, socket, timeout.Token);
            var offer = await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
            Ensure(offer.Payload.GetProperty("transferId").GetGuid() == first.TransferId,
                "Repeated reconnects must preserve the head ID and avoid losing the queue.");
            await CloseAndWaitOfflineAsync(host, socket, timeout.Token);
        }
        var rows = host.Transfers.ReadAllSnapshots();
        Ensure(rows.Single(item => item.TransferId == first.TransferId).State == TransferState.Queued
            && rows.Single(item => item.TransferId == second.TransferId).State == TransferState.Queued,
            "Unaccepted files must remain queued after reconnect attempts.");
    }

    public static async Task CheckSnapshotPaginationAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var name = new string('n', 2048) + ".bin";
        for (var index = 0; index < 400; index++)
        {
            host.Transfers.RegisterIncomingOffer(new TransferOffer(Guid.NewGuid(), null,
                TransferKind.File, name, 0, "application/octet-stream", DateTimeOffset.UtcNow));
        }
        var rows = await OpenAndCaptureSnapshotsAsync(host, socket, timeout.Token);
        Ensure(rows.Count == 400, "Snapshot pagination lost or duplicated transfer rows.");
        socket.Abort();
    }

    public static async Task CheckLocalCancellationRoundTripAsync()
    {
        await using var host = await TestBobHost.StartAsync(durable: true);
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var source = Path.Combine(host.TemporaryRoot, "cancel.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var offer = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        await OpenSessionAsync(host, socket, timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
        await host.Sessions.CancelTransferAsync(host.Transfers, offer.TransferId, timeout.Token);
        var cancel = await ReceiveTypeAsync(socket, "transfer.cancel", timeout.Token);
        Ensure(cancel.Payload.GetProperty("transferId").GetGuid() == offer.TransferId,
            "Local cancellation must send transfer.cancel.");
        await SendAsync(socket, TransferProtocolMessages.Cancel(offer.TransferId), timeout.Token);
        var concurrentCancel = await ReceiveTypeAsync(socket, "transfer.cancel", timeout.Token);
        Ensure(concurrentCancel.Payload.GetProperty("transferId").GetGuid() == offer.TransferId,
            "On simultaneous cancellation, the body sender must await the receiver's fixed terminal.");
        var peerTime = DateTimeOffset.UtcNow.AddSeconds(1);
        var peerCanceled = ProtocolJson.Create("transfer.canceled", new
        {
            transferId = offer.TransferId,
            canceledAt = peerTime
        });
        await SendAsync(socket, peerCanceled, timeout.Token);
        var ack = await ReceiveTypeAsync(socket, "transfer.terminalAck", timeout.Token);
        Ensure(ack.Payload.GetProperty("state").GetString() == "canceled",
            "The peer canceled timestamp must be accepted and acknowledged.");
        await SendAsync(socket, peerCanceled, timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.terminalAck", timeout.Token);
        var saved = new TransferHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll().Single();
        Ensure(saved.Canceled?.CanceledAt == peerTime && !saved.CancelPending,
            "The received immutable canceled payload must replace the local provisional time.");
        await CloseAndWaitOfflineAsync(host, socket, timeout.Token);
        using var again = host.CreateWebSocketClient();
        await OpenSessionAsync(host, again, timeout.Token);
        await SendSentinelAsync(again, timeout.Token);
        Ensure((await ReceiveAsync(again, timeout.Token)).Type == "text.ack",
            "A confirmed cancellation must not replay a new canceled timestamp or another cancel.");
        await CheckActiveUploadCancellationAsync(host, again, timeout.Token);
        await again.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
    }

    private static async Task CheckActiveUploadCancellationAsync(TestBobHost host, ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = host.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        var connectionRevision = host.Sessions.Snapshot.Revision;
        var canceledResults = new Dictionary<Guid, TransferCanceled>();
        async Task<ProtocolEnvelope> ReceiveNextAsync(string type, Guid? transferId = null)
        {
            while (true)
            {
                var envelope = await ReceiveAsync(socket, cancellationToken);
                if (envelope.Type == type && (transferId is null
                    || envelope.Payload.GetProperty("transferId").GetGuid() == transferId))
                {
                    return envelope;
                }
                if (envelope.Type == "transfer.progress") { continue; }
                if (envelope.Type == "transfer.canceled")
                {
                    var duplicate = TransferProtocolParser.ParseCanceled(envelope.Payload);
                    Ensure(canceledResults.TryGetValue(duplicate.TransferId, out var original)
                        && original == duplicate, "An HTTP cancel replay must preserve its canonical canceled payload.");
                    continue;
                }
                throw new InvalidOperationException($"Expected {type} on the original WSS, received {envelope.Type}.");
            }
        }

        for (var index = 0; index < 8; index++)
        {
            const long size = 64L * 1024 * 1024;
            var offer = new TransferOffer(Guid.NewGuid(), null, TransferKind.File,
                $"cancel-active-upload-{index}.bin", size, "application/octet-stream", DateTimeOffset.UtcNow);
            await SendAsync(socket, TransferProtocolMessages.Offer(offer), cancellationToken);
            await ReceiveNextAsync("transfer.accepted", offer.TransferId);
            using var producer = new SyntheticBody(size, paced: true);
            using var content = new StreamContent(producer, 128 * 1024);
            content.Headers.ContentLength = size;
            using var request = new HttpRequestMessage(HttpMethod.Put,
                $"/bob/v1/transfers/{offer.TransferId:D}/content") { Content = content };
            request.Headers.Add("X-Bob-Transfer-Id", offer.TransferId.ToString("D"));
            using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var uploading = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, uploadCancellation.Token);
            try
            {
                var progress = await ReceiveNextAsync("transfer.progress", offer.TransferId);
                var bytes = progress.Payload.GetProperty("bytes").GetInt64();
                Ensure(bytes > 0 && bytes < size && !uploading.IsCompleted
                    && host.Transfers.ReadAllSnapshots().Single(row => row.TransferId == offer.TransferId)
                        .State == TransferState.Transferring,
                    "The cancellation fixture must have a real HTTP upload still writing its partial body.");
                await SendAsync(socket, TransferProtocolMessages.Cancel(offer.TransferId), cancellationToken);
                var canonical = TransferProtocolParser.ParseCanceled(
                    (await ReceiveNextAsync("transfer.canceled", offer.TransferId)).Payload);
                canceledResults.Add(offer.TransferId, canonical);
                // Match the real sender: keep uploading until the peer fixes canceled,
                // then release the HTTP body concurrently with the receiver's cleanup.
                uploadCancellation.Cancel();
                await SendAsync(socket, TransferProtocolMessages.TerminalAck(offer.TransferId,
                    TransferState.Canceled), cancellationToken);
            }
            finally
            {
                uploadCancellation.Cancel();
                try
                {
                    using var response = await uploading;
                    Ensure(response.StatusCode != HttpStatusCode.Accepted,
                        "An interrupted partial upload must not return a completed-body 202 response.");
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
            }
            await SendSentinelAsync(socket, cancellationToken);
            await ReceiveNextAsync("text.ack");
            var part = Path.Combine(host.ReceiveDirectory, $".bob-{offer.TransferId:D}.part");
            var cleanupStarted = Stopwatch.GetTimestamp();
            while (File.Exists(part) && Stopwatch.GetElapsedTime(cleanupStarted) < TimeSpan.FromSeconds(2))
            {
                // The HTTP endpoint's asynchronous Dispose/finally may still own
                // the last file handle after the canonical WSS reply is sent.
                await Task.Delay(10, cancellationToken);
            }
            var saved = new TransferHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll()
                .Single(row => row.Offer.TransferId == offer.TransferId);
            Ensure(saved.State == TransferState.Canceled && saved.Canceled == canceledResults[offer.TransferId]
                && saved.Failure is null && !saved.CancelPending && !saved.TerminalAckPending
                && !File.Exists(part)
                && !File.Exists(Path.Combine(host.ReceiveDirectory, offer.Name)),
                "Active-upload cancellation must persist and acknowledge canceled and remove only the unverified part.");
            Ensure(socket.State == WebSocketState.Open && host.Sessions.Snapshot.IsConnected
                && host.Sessions.Snapshot.Revision == connectionRevision,
                "Repeated HTTP cleanup must leave the same WSS connection alive without reconnecting.");
        }

        byte[] finalBytes = [2, 3, 5, 7, 11];
        var next = new TransferOffer(Guid.NewGuid(), null, TransferKind.File, "after-active-cancel.bin",
            finalBytes.LongLength, "application/octet-stream", DateTimeOffset.UtcNow);
        await SendAsync(socket, TransferProtocolMessages.Offer(next), cancellationToken);
        await ReceiveNextAsync("transfer.accepted", next.TransferId);
        using (var request = new HttpRequestMessage(HttpMethod.Put,
            $"/bob/v1/transfers/{next.TransferId:D}/content") { Content = new ByteArrayContent(finalBytes) })
        {
            request.Headers.Add("X-Bob-Transfer-Id", next.TransferId.ToString("D"));
            using var response = await client.SendAsync(request, cancellationToken);
            Ensure(response.StatusCode == HttpStatusCode.Accepted,
                "The next real upload must start and finish on the original WSS after repeated cancellations.");
        }
        await SendAsync(socket, TransferProtocolMessages.Digest(new TransferDigest(next.TransferId,
            "sha-256", Base64Url(SHA256.HashData(finalBytes)), finalBytes.LongLength)), cancellationToken);
        var completed = TransferProtocolParser.ParseCompleted(
            (await ReceiveNextAsync("transfer.completed", next.TransferId)).Payload);
        Ensure(File.ReadAllBytes(Path.Combine(host.ReceiveDirectory, completed.StoredName)).SequenceEqual(finalBytes),
            "The subsequent upload must publish the verified original bytes.");
        await SendAsync(socket, TransferProtocolMessages.TerminalAck(next.TransferId,
            TransferState.Completed), cancellationToken);
        await SendSentinelAsync(socket, cancellationToken);
        await ReceiveNextAsync("text.ack");
        Ensure(host.Sessions.Snapshot.IsConnected && host.Sessions.Snapshot.Revision == connectionRevision,
            "Text and another completed transfer must reuse the original WSS after active-upload cancellation.");
    }

    public static async Task CheckOnlineDigestReplayAsync()
    {
        await using var host = await TestBobHost.StartAsync(durable: true);
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = Path.Combine(host.TemporaryRoot, "lost-digest.bin");
        File.WriteAllBytes(source, [1, 4, 9, 16]);
        var offer = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        await OpenSessionAsync(host, socket, timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
        await SendAcceptedAsync(socket, offer, timeout.Token);
        await SendSentinelAsync(socket, timeout.Token);
        await ReceiveTypeAsync(socket, "text.ack", timeout.Token);
        var body = host.Transfers.BeginOutgoingContent(offer.TransferId);
        var sha256 = Base64Url(SHA256.HashData(body.Stream));
        body.Stream.Dispose();
        // Complete the body, but deliberately lose the first digest send.
        var original = host.Transfers.CompleteOutgoingContent(offer.TransferId, 4, sha256);
        var maintenance = new TransferMaintenanceService(host.Transfers, host.Sessions,
            NullLogger<TransferMaintenanceService>.Instance);
        await maintenance.ReconcileAsync(timeout.Token);
        var replay = await ReceiveTypeAsync(socket, "transfer.digest", timeout.Token);
        Ensure(TransferProtocolParser.ParseDigest(replay.Payload) == original
            && host.Sessions.Snapshot.IsConnected,
            "Maintenance must replay the original digest without requiring a reconnect.");
        await maintenance.ReconcileAsync(timeout.Token);
        var duplicate = await ReceiveTypeAsync(socket, "transfer.digest", timeout.Token);
        Ensure(duplicate.Payload.GetRawText() == replay.Payload.GetRawText(),
            "Repeated maintenance must preserve the digest payload.");
        await SendAsync(socket, TransferProtocolMessages.Completed(new TransferCompleted(
            offer.TransferId, 4, sha256, "lost-digest.bin", DateTimeOffset.UtcNow)), timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.terminalAck", timeout.Token);
        Ensure(host.Transfers.ReadAllSnapshots().Single().State == TransferState.Completed,
            "The replayed digest must allow completion on the original online session.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
    }

    public static async Task CheckTextStorageFailureReconnectAsync()
    {
        await using var host = await TestBobHost.StartAsync(durable: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var socket = host.CreateWebSocketClient();
        await OpenSessionAsync(host, socket, timeout.Token);
        var textId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        const string text = "replay after a blocked acknowledgement save";
        await host.Sessions.SendTextAsync(textId, text, createdAt, timeout.Token);
        await ReceiveTypeAsync(socket, "text.send", timeout.Token);
        var historyPath = Path.Combine(DataPaths(host.TemporaryRoot).RootDirectory, "text-messages-v1.json");
        var acknowledgement = ProtocolJson.Create("text.ack", new { textId, storedAt = DateTimeOffset.UtcNow });
        using (var blocked = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await SendAsync(socket, acknowledgement, timeout.Token);
            await ReceiveStorageFailureCloseAsync(host, socket, timeout.Token);
        }
        Ensure(new TextHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll().Single().Status != "Delivered",
            "A failed acknowledgement write must not report durable delivery.");
        using var second = host.CreateWebSocketClient();
        await OpenSessionAsync(host, second, timeout.Token);
        var replay = await ReceiveTypeAsync(second, "text.send", timeout.Token);
        Ensure(replay.Payload.GetProperty("textId").GetGuid() == textId
            && replay.Payload.GetProperty("text").GetString() == text
            && replay.Payload.GetProperty("createdAt").GetDateTimeOffset() == createdAt,
            "Storage recovery must replay the exact original outgoing text.");
        await SendAsync(second, acknowledgement, timeout.Token);
        await SendSentinelAsync(second, timeout.Token);
        await ReceiveTypeAsync(second, "text.ack", timeout.Token);
        Ensure(new TextHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll()
            .Single(record => record.TextId == textId).Status == "Delivered",
            "Delivery must complete after the store becomes writable again.");

        var incomingId = Guid.NewGuid();
        var incoming = ProtocolJson.Create("text.send", new
        {
            textId = incomingId,
            text = "incoming store failure",
            createdAt = DateTimeOffset.UtcNow
        });
        using (var blocked = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await SendAsync(second, incoming, timeout.Token);
            await ReceiveStorageFailureCloseAsync(host, second, timeout.Token);
        }
        Ensure(!new TextHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll()
            .Any(record => record.TextId == incomingId),
            "An incoming text must not be acknowledged before its store write succeeds.");
        using var third = host.CreateWebSocketClient();
        await OpenSessionAsync(host, third, timeout.Token);
        await SendAsync(third, incoming, timeout.Token);
        var ack = await ReceiveTypeAsync(third, "text.ack", timeout.Token);
        Ensure(ack.Payload.GetProperty("textId").GetGuid() == incomingId,
            "The incoming original ID must be accepted after storage recovers.");
        await SendAsync(third, incoming, timeout.Token);
        await ReceiveTypeAsync(third, "text.ack", timeout.Token);
        Ensure(new TextHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll()
            .Count(record => record.TextId == incomingId) == 1,
            "Storage recovery must not duplicate incoming history.");
        await third.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
    }

    private static async Task ReceiveStorageFailureCloseAsync(TestBobHost host, ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConnectionSnapshot> changed = (_, snapshot) =>
        {
            if (snapshot.Phase == SessionPhase.Offline) { offline.TrySetResult(); }
        };
        host.Sessions.ConnectionChanged += changed;
        try
        {
            var response = await socket.ReceiveAsync(new byte[1024].AsMemory(), cancellationToken);
            Ensure(response.MessageType == WebSocketMessageType.Close && (int?)socket.CloseStatus == 4500
                && socket.CloseStatusDescription == "storage_failure",
                "An unsaved text state must close WSS with the storage_failure diagnostic.");
            if (host.Sessions.Snapshot.Phase != SessionPhase.Offline)
            {
                await offline.Task.WaitAsync(cancellationToken);
            }
        }
        finally { host.Sessions.ConnectionChanged -= changed; }
    }

    public static async Task CheckQueuedCancellationAndPeerErrorAsync()
    {
        await using var host = await TestBobHost.StartAsync(durable: true);
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = Path.Combine(host.TemporaryRoot, "queued.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var first = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        var unseen = host.Transfers.EnqueueOutgoing(source, TransferKind.File);
        await OpenSessionAsync(host, socket, timeout.Token);
        await ReceiveTypeAsync(socket, "transfer.offer", timeout.Token);
        await host.Sessions.CancelTransferAsync(host.Transfers, unseen.TransferId, timeout.Token);
        await SendSentinelAsync(socket, timeout.Token);
        Ensure((await ReceiveAsync(socket, timeout.Token)).Type == "text.ack",
            "Canceling an unoffered file must not send an unknown transfer ID to the peer.");
        var canceled = new TransferHistoryStore(DataPaths(host.TemporaryRoot)).ReadAll()
            .Single(record => record.Offer.TransferId == unseen.TransferId);
        Ensure(canceled.State == TransferState.Canceled && !canceled.CancelPending,
            "An unoffered cancellation must complete locally without waiting for a peer response.");
        foreach (var code in new[] { "invalid_state", "untrusted/path-like-code" })
        {
            await SendAsync(socket, ProtocolJson.Create("error", new
            {
                code,
                message = "peer diagnostic is not a transfer terminal",
                retryable = false,
                transferId = first.TransferId
            }), timeout.Token);
            await SendSentinelAsync(socket, timeout.Token);
            Ensure((await ReceiveAsync(socket, timeout.Token)).Type == "text.ack"
                && host.Sessions.Snapshot.IsConnected,
                "A peer generic error must not be echoed or close a healthy session.");
            Ensure(host.Transfers.ReadAllSnapshots().Single(record => record.TransferId == first.TransferId)
                .State == TransferState.Offered, "A generic error must not mutate the transfer state.");
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token);
    }

    private static async Task<(TransferOffer Offer, string Sha256, string Path)> UploadSyntheticAsync(
        TestBobHost host, HttpClient client, long size, bool knownSize, CancellationToken cancellationToken)
    {
        var offer = new TransferOffer(Guid.NewGuid(), null, TransferKind.File,
            knownSize ? "known-large.bin" : "chunked-large.bin", knownSize ? size : null,
            "application/octet-stream", DateTimeOffset.UtcNow);
        Ensure(host.Transfers.RegisterIncomingOffer(offer).Reply == IncomingOfferReply.Accepted,
            "The synthetic streaming offer must be accepted.");
        using var producer = new SyntheticBody(size);
        using var content = new StreamContent(producer, 128 * 1024);
        if (knownSize) { content.Headers.ContentLength = size; }
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/bob/v1/transfers/{offer.TransferId:D}/content") { Content = content };
        request.Headers.Add("X-Bob-Transfer-Id", offer.TransferId.ToString("D"));
        using var response = await client.SendAsync(request, cancellationToken);
        Ensure(response.StatusCode == HttpStatusCode.Accepted,
            $"A {size}-byte {(knownSize ? "known-size" : "chunked")} PUT returned {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Ensure(document.RootElement.GetProperty("bytesReceived").GetInt64() == size,
            "The HTTP result truncated the streamed byte count.");
        var sha256 = producer.FinishDigest();
        var completed = host.Transfers.ApplyIncomingDigest(new TransferDigest(offer.TransferId,
            "sha-256", sha256, size)).Completed;
        Ensure(completed is not null && completed.Bytes == size && completed.Sha256 == sha256,
            "Incremental SHA-256 verification failed for the large streamed body.");
        host.Transfers.AcknowledgeTerminal(offer.TransferId, TransferState.Completed);
        return (offer, sha256, Path.Combine(host.ReceiveDirectory, completed!.StoredName));
    }

    private static async Task<List<JsonElement>> OpenAndCaptureSnapshotsAsync(TestBobHost host,
        ClientWebSocket socket, CancellationToken cancellationToken)
    {
        await socket.ConnectAsync(host.WebSocketUri, cancellationToken);
        await SendAsync(socket, ProtocolJson.Create("session.hello", new
        {
            protocolMin = 1,
            protocolMax = 1,
            client = new { name = "reliability probe" }
        }), cancellationToken);
        Ensure((await ReceiveAsync(socket, cancellationToken)).Type == "session.welcome",
            "Expected welcome before a replay snapshot.");
        var rows = new List<JsonElement>();
        Guid? snapshotId = null;
        var page = 0;
        while (true)
        {
            var envelope = await ReceiveAsync(socket, cancellationToken);
            Ensure(envelope.Type == "session.snapshot"
                && ProtocolJson.Serialize(envelope).Length <= BobProtocol.MaxSnapshotEnvelopeBytes,
                "Each coherent snapshot page must be at most 768 KiB.");
            var payload = envelope.Payload;
            var id = payload.GetProperty("snapshotId").GetGuid();
            snapshotId ??= id;
            Ensure(id == snapshotId && payload.GetProperty("page").GetInt32() == page++,
                "Snapshot IDs and page numbering must remain contiguous.");
            rows.AddRange(payload.GetProperty("transfers").EnumerateArray().Select(item => item.Clone()));
            if (payload.GetProperty("isLast").GetBoolean()) { break; }
        }
        if (rows.Count == 400) { Ensure(page > 1, "The fixture must produce multiple snapshot pages."); }
        return rows;
    }

    private static async Task<ProtocolEnvelope> ReceiveTypeAsync(ClientWebSocket socket, string type,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var envelope = await ReceiveAsync(socket, cancellationToken);
            if (envelope.Type == type) { return envelope; }
            Ensure(envelope.Type == "transfer.progress", $"Expected {type}, received {envelope.Type}.");
        }
    }
    private static Task SendAcceptedAsync(ClientWebSocket socket, TransferOffer offer,
        CancellationToken cancellationToken) => SendAsync(socket, ProtocolJson.Create("transfer.accepted",
            new { transferId = offer.TransferId, plannedName = offer.Name }), cancellationToken);
    private static Task SendCompletedAsync(ClientWebSocket socket, Guid transferId, long bytes, string sha256,
        CancellationToken cancellationToken) => SendAsync(socket, ProtocolJson.Create("transfer.completed", new
        {
            transferId,
            bytes,
            sha256,
            storedName = "phone-copy.bin",
            completedAt = DateTimeOffset.UtcNow
        }), cancellationToken);
    private static Task SendSentinelAsync(ClientWebSocket socket, CancellationToken cancellationToken) =>
        SendAsync(socket, ProtocolJson.Create("text.send", new
        {
            textId = Guid.NewGuid(),
            text = "sentinel",
            createdAt = DateTimeOffset.UtcNow
        }), cancellationToken);
    private static async Task CloseAndWaitOfflineAsync(TestBobHost host, ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConnectionSnapshot> changed = (_, snapshot) =>
        {
            if (snapshot.Phase == SessionPhase.Offline) { offline.TrySetResult(); }
        };
        host.Sessions.ConnectionChanged += changed;
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", cancellationToken);
            if (host.Sessions.Snapshot.Phase != SessionPhase.Offline)
            {
                await offline.Task.WaitAsync(cancellationToken);
            }
        }
        finally { host.Sessions.ConnectionChanged -= changed; }
    }
    private static AppPaths DataPaths(string root) => new(Path.Combine(root, "data"),
        Path.Combine(root, "data", "identity"));
    private static string NewReliabilityRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bob-windows-reliability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
    private static void DeleteReliabilityRoot(string root)
    {
        var absolute = Path.GetFullPath(root);
        Ensure(string.Equals(Path.GetDirectoryName(absolute), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("bob-windows-reliability-"),
            "Test cleanup must remain inside its dedicated temporary directory.");
        Directory.Delete(absolute, recursive: true);
    }

    private sealed class SyntheticBody(long size, bool paced = false) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;
        private string? _digest;
        private bool _disposed;
        public string FinishDigest()
        {
            Ensure(_position == size, "The producer body was not completely consumed.");
            return _digest ??= Base64Url(_hash.GetHashAndReset());
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(paced ? Math.Min(buffer.Length, 4093) : buffer.Length, size - _position);
            buffer[..count].Fill(0xA5);
            if (count > 0) { _hash.AppendData(buffer[..count]); }
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return paced ? new ValueTask<int>(ReadPacedAsync(buffer, cancellationToken))
                : ValueTask.FromResult(Read(buffer.Span));
        }
        private async Task<int> ReadPacedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            // Uneven chunks keep the receiver's buffered FileStream active when
            // WSS cancel and the HTTP await-using cleanup race to close it.
            await Task.Delay(1, cancellationToken);
            return Read(buffer.Span);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                if (_position == size && _digest is null) { _digest = Base64Url(_hash.GetHashAndReset()); }
                _hash.Dispose();
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode,
        IntPtr input, uint inputBytes, IntPtr output, uint outputBytes, out uint returnedBytes, IntPtr overlapped);
}
