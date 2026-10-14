using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Voca.Services;

/// <summary>
/// "Học trên điện thoại": while turned on in Cài đặt, serves the phone page on the local network (same Wi-Fi,
/// or Tailscale from anywhere). Follows the settings each time the data is saved; the requests run on the
/// UI thread through <c>onUiThread</c>, like every other change to the store.
/// </summary>
public sealed class PhoneAccess : IDisposable
{
    private readonly Store _store;
    private readonly Func<Func<PhoneResponse>, Task<PhoneResponse>> _onUiThread;
    private readonly PhoneApi _api;
    private PhoneServer? _server;

    /// <summary>Why the page is not being served although it is turned on (e.g. the port is taken); null otherwise.</summary>
    public string? Error { get; private set; }
    public bool Running => _server is not null;
    public int Port => _server?.Port ?? _store.Data.Settings.PhonePort;
    public event Action? StatusChanged;

    public PhoneAccess(Store store, Func<Func<PhoneResponse>, Task<PhoneResponse>> onUiThread)
    {
        _store = store;
        _onUiThread = onUiThread;
        _api = new PhoneApi(store);
        _store.Changed += Apply;
        Apply();
    }

    /// <summary>Starts, stops or moves the server to match the settings.</summary>
    public void Apply()
    {
        var settings = _store.Data.Settings;
        var wanted = settings.PhoneAccess && settings.PhoneKey.Length > 0;
        if (wanted && _server?.Port == settings.PhonePort) return;
        if (!wanted && !Running && Error is null) return;
        _server?.Dispose();
        _server = null;
        Error = null;
        if (wanted)
        {
            try
            {
                _server = new PhoneServer(IPAddress.Any, settings.PhonePort, request => _onUiThread(() => _api.Handle(request)));
            }
            catch (SocketException ex)
            {
                Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Cổng {settings.PhonePort} đang bị chương trình khác dùng. Đổi sang cổng khác rồi bật lại."
                    : $"Không mở được cổng {settings.PhonePort}: {ex.Message}";
            }
        }
        StatusChanged?.Invoke();
    }

    /// <summary>A new secret for the phone link: about 120 bits, letters and digits only so it reads well in a URL.</summary>
    public static string NewKey()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        return string.Concat(Enumerable.Range(0, 24).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }

    public string Link(string address) => $"http://{address}:{Port}/?k={_store.Data.Settings.PhoneKey}";

    /// <summary>
    /// This computer's IPv4 addresses a phone can reach, best first: Wi-Fi/Ethernet on a home network (with a
    /// gateway), then Tailscale (100.64.0.0/10), then the rest. Virtual adapters (Hyper-V, WSL, VPN taps) last.
    /// </summary>
    public static List<(string Address, string Adapter)> Addresses()
    {
        var found = new List<(string Address, string Adapter, int Rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var name = $"{nic.Name} {nic.Description}";
            var isVirtual = name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("WSL", StringComparison.OrdinalIgnoreCase) || name.Contains("VMware", StringComparison.OrdinalIgnoreCase);
            foreach (var unicast in props.UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // no DHCP answer: unreachable
                var tailscale = bytes[0] == 100 && bytes[1] is >= 64 and < 128;
                var rank = tailscale ? 1 : isVirtual ? 3 : hasGateway ? 0 : 2;
                found.Add((ip.ToString(), tailscale ? "Tailscale (dùng được cả khi ra ngoài)" : nic.Name, rank));
            }
        }
        return found.OrderBy(a => a.Rank).Select(a => (a.Address, a.Adapter)).Distinct().ToList();
    }

    /// <summary>PNG of a QR code for <paramref name="text"/>.</summary>
    public static byte[] QrPng(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.PngByteQRCode(data).GetGraphic(8);
    }

    public void Dispose()
    {
        _store.Changed -= Apply;
        _server?.Dispose();
        _server = null;
    }
}
