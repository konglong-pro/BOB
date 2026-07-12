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
    }
}

internal static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
