using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Cheap "is there a network path to Backloggd" probe, used to tell a connection failure apart
    /// from any other error. It says nothing about the site itself: a captive portal or an anti-bot
    /// screen also answers, which is why going back online always ends with a real page load.
    /// </summary>
    public static class BackloggdConnectivity
    {
        private const string Host = "backloggd.com";
        private const int TimeoutMs = 3000;

        public static async Task<bool> IsReachableAsync()
        {
            // Some networks drop ICMP, so a TCP handshake on 443 also counts as an answer.
            return await PingAsync() || await ConnectAsync();
        }

        private static async Task<bool> PingAsync()
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(Host, TimeoutMs);
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> ConnectAsync()
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(Host, 443);
                return await Task.WhenAny(connect, Task.Delay(TimeoutMs)) == connect && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
