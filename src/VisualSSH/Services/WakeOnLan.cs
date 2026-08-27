using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VisualSSH.Models;

namespace VisualSSH.Services;

public static class WakeOnLan
{
    private static readonly Regex MacRx = new(
        @"^[0-9A-Fa-f]{2}([:\-]?[0-9A-Fa-f]{2}){5}$",
        RegexOptions.Compiled);

    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = [];
        var raw = (text ?? "").Trim();
        if (raw.Length == 0 || !MacRx.IsMatch(raw)) return false;
        var hex = raw.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (hex.Length != 12) return false;
        mac = Convert.FromHexString(hex);
        return mac.Length == 6 && mac.Any(b => b != 0);
    }

    public static string FormatMac(byte[] mac) =>
        string.Join(":", mac.Select(b => b.ToString("x2")));

    public static string? ResolveMac(ConnectionProfile profile)
    {
        if (TryParseMac(profile.MacAddress, out var stored))
            return FormatMac(stored);
        return LookupArp(profile.Host);
    }

    public static bool HostLooksAsleep(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
            if (addresses.Count == 0) return true;
            foreach (var ip in addresses)
            {
                using var tcp = new TcpClient();
                var task = tcp.ConnectAsync(ip, port);
                if (task.Wait(timeoutMs) && tcp.Connected) return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    public static void Send(string macText, string? host)
    {
        if (!TryParseMac(macText, out var mac))
            throw new InvalidOperationException("Ongeldig MAC-adres voor Wake-on-LAN: " + macText);
        Send(mac, host);
    }

    public static void Send(byte[] mac, string? host)
    {
        var packet = BuildPacket(mac);
        var targets = Targets(host);
        foreach (var ep in targets)
        {
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                udp.Send(packet, packet.Length, ep);
            }
            catch
            {
                /* andere broadcastpoort mag falen */
            }
        }
    }

    public static string LearnMacScript() =>
        "python3 -c " + DeckClient.ShQuote(
            "import os,glob\n" +
            "rows=[]\n" +
            "for p in glob.glob('/sys/class/net/*/address'):\n" +
            "    n=os.path.basename(os.path.dirname(p))\n" +
            "    if n=='lo' or n.startswith(('veth','docker','br-','virbr','tailscale')): continue\n" +
            "    addr=open(p).read().strip()\n" +
            "    try: st=open(os.path.join(os.path.dirname(p),'operstate')).read().strip()\n" +
            "    except: st='?'\n" +
            "    print(n+'\\t'+st+'\\t'+addr)\n");

    public static string EnableWowlanScript() =>
        "IF=$(ls /sys/class/net 2>/dev/null | grep -E '^(wl|mlan)' | head -n1); " +
        "ETH=$(ls /sys/class/net 2>/dev/null | grep -E '^(en|eth)' | head -n1); " +
        "[ -n \"$IF\" ] && (iw dev \"$IF\" wowlan enable magic-packet 2>/dev/null || sudo -n iw dev \"$IF\" wowlan enable magic-packet 2>/dev/null); " +
        "[ -n \"$ETH\" ] && (ethtool -s \"$ETH\" wol g 2>/dev/null || sudo -n ethtool -s \"$ETH\" wol g 2>/dev/null); " +
        "true";

    public static string? PickMac(string listing)
    {
        string? fallback = null;
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var name = parts[0].Trim();
            var state = parts[1].Trim();
            var mac = parts[2].Trim();
            if (!TryParseMac(mac, out _)) continue;
            var wifi = name.StartsWith("wl", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("mlan", StringComparison.OrdinalIgnoreCase);
            var wired = name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("eth", StringComparison.OrdinalIgnoreCase);
            if (state is "up" or "unknown" && (wifi || wired))
                return mac;
            fallback ??= mac;
        }
        return fallback;
    }

    private static byte[] BuildPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
            Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    private static List<IPEndPoint> Targets(string? host)
    {
        var list = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, 9),
            new(IPAddress.Broadcast, 7),
            new(IPAddress.Broadcast, 40000)
        };
        if (string.IsNullOrWhiteSpace(host)) return list;
        try
        {
            var addresses = Dns.GetHostAddresses(host)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
            foreach (var ip in addresses)
            {
                list.Add(new IPEndPoint(ip, 9));
                list.Add(new IPEndPoint(ip, 7));
                var bytes = ip.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    bytes[3] = 255;
                    list.Add(new IPEndPoint(new IPAddress(bytes), 9));
                }
            }
        }
        catch
        {
            /* hostnaam onbekend: alleen broadcast */
        }
        return list;
    }

    private static string? LookupArp(string host)
    {
        try
        {
            var ip = Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) return null;
            var psi = new System.Diagnostics.ProcessStartInfo("arp", "-a " + ip)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            var match = Regex.Match(output,
                Regex.Escape(ip.ToString()) + @"\s+([0-9a-fA-F\-:]{17})");
            if (match.Success && TryParseMac(match.Groups[1].Value, out var mac))
                return FormatMac(mac);
        }
        catch
        {
            /* geen ARP-cache */
        }
        return null;
    }
}
