using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// UDP LAN discovery responder so paired phones automatically find this PC again when its DHCP IP changes.
/// Replies contain only public discovery metadata (PC name, pinned certificate fingerprint, port, LAN IPs) —
/// never any auth token. The phone still pins the TLS certificate and authenticates over HTTPS.
/// </summary>
public sealed class LanDiscovery(ConfigStore configStore, string? certFingerprint, int lanPort, ILogger<LanDiscovery> log) : BackgroundService
{
    public const int DiscoveryPort = 8767;
    public const string ProbeMessage = "PHONEBOX_DISCOVER_V1";

    internal static byte[] BuildReply(string pcName, string fingerprint, int port, IReadOnlyList<string> hosts) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            pc = pcName,
            fp = fingerprint,
            port,
            hosts,
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(certFingerprint)) return;

        using var udp = new UdpClient();
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            udp.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            log.LogWarning("LAN discovery could not listen on UDP {Port}: {Message}", DiscoveryPort, ex.Message);
            return;
        }

        log.LogInformation("LAN discovery listening on UDP {Port}", DiscoveryPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug("LAN discovery receive error: {Message}", ex.Message);
                await Task.Delay(500, stoppingToken);
                continue;
            }

            if (packet.Buffer.Length > 128) continue;
            var text = Encoding.UTF8.GetString(packet.Buffer).Trim();
            if (!string.Equals(text, ProbeMessage, StringComparison.Ordinal)) continue;
            if (!configStore.Get().Lan.Enabled) continue;

            try
            {
                var reply = BuildReply(Environment.MachineName, certFingerprint, lanPort, Lan.Addresses());
                await udp.SendAsync(reply, packet.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug("LAN discovery reply failed to {Endpoint}: {Message}", packet.RemoteEndPoint, ex.Message);
            }
        }
    }
}
