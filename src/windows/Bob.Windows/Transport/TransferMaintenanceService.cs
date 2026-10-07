using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;

namespace Bob.Windows.Transport;

public sealed class TransferMaintenanceService(
    TransferCoordinator transfers,
    SessionCoordinator sessions,
    ILogger<TransferMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EventHandler expired = (_, _) => sessions.AbortActiveConnection();
        transfers.CancellationConfirmationExpired += expired;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (IOException)
                {
                    logger.LogWarning("Transfer state could not be persisted; reconciliation will be retried.");
                }
                catch (Exception exception) when (exception is WebSocketException or InvalidOperationException
                    or OperationCanceledException)
                {
                    // The next active session replays the durable queue and terminal states.
                }
            }
        }
        finally { transfers.CancellationConfirmationExpired -= expired; }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (transfers.ExpireWaitingTransfers(DateTimeOffset.UtcNow))
        {
            sessions.AbortActiveConnection();
            return;
        }
        if (!sessions.Snapshot.IsConnected) { return; }
        // A bounded control send can fail while the WSS session remains healthy.
        // Replaying the durable digest also recovers that case without reconnecting.
        foreach (var message in transfers.ReadReplayEnvelopes())
        {
            await sessions.SendEnvelopeAsync(message, cancellationToken);
        }
        await sessions.SendNextTransferOfferAsync(transfers, cancellationToken);
    }
}
