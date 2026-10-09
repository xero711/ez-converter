using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.NetworkInformation;

namespace EZConverter.Sharing;

public sealed class PeerDiscovery : IAsyncDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");
    private static readonly IPAddress GroupV6 = IPAddress.Parse("ff12::fd3a:e420");
    private const int DefaultHttpPort = 53317;
    private readonly LocalSendDeviceInfo _device;
    private readonly int _discoveryPort;
    private readonly bool _enableMulticast;
    private readonly bool _enableBroadcast;
    private readonly X509Certificate2? _clientCertificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, PeerDevice> _peers = new(StringComparer.Ordinal);
    private UdpClient? _udp;
    private readonly List<(UdpClient Socket, int InterfaceIndex)> _udpV6 = [];
    private Task? _receiveTask;
    private Task[] _receiveV6Tasks = [];
    private Task? _announceTask;
    private DateTimeOffset _nextLegacyProbe;
    private int _legacyProbeRunning;
    public event Action<IReadOnlyList<PeerDevice>>? Changed;
    public event Action<string>? Warning;
    internal int Ipv6MulticastSocketCount => _udpV6.Count;

    private sealed record Announcement(
        [property: JsonPropertyName("alias")] string Alias,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("deviceModel")] string? DeviceModel,
        [property: JsonPropertyName("deviceType")] string? DeviceType,
        [property: JsonPropertyName("fingerprint")] string? Fingerprint,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("download")] bool? Download,
        [property: JsonPropertyName("announce")] bool? Announce);

    public PeerDiscovery(DeviceInfo device, int discoveryPort = 53317)
        : this(new LocalSendDeviceInfo(device.Name, Fingerprint: device.Fingerprint, Port: device.Port, Protocol: "https", Download: false), discoveryPort) { }

    public PeerDiscovery(LocalSendDeviceInfo device, int discoveryPort = 53317, X509Certificate2? clientCertificate = null)
        : this(device, discoveryPort, enableMulticast: true, enableBroadcast: true, clientCertificate: clientCertificate) { }

    internal PeerDiscovery(LocalSendDeviceInfo device, int discoveryPort, bool enableMulticast, bool enableBroadcast, X509Certificate2? clientCertificate = null)
    {
        if (discoveryPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(discoveryPort));
        _device = device;
        _discoveryPort = discoveryPort;
        _enableMulticast = enableMulticast;
        _enableBroadcast = enableBroadcast;
        _clientCertificate = clientCertificate;
    }

    public void Start()
    {
        if (_udp is not null || _udpV6.Count > 0) return;
        SocketException? ipv4Error = null;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = _enableBroadcast;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            if (_enableMulticast)
            {
                var joined = false;
                foreach (var address in TransferServer.LocalIpv4Addresses())
                {
                    try { udp.JoinMulticastGroup(Group, IPAddress.Parse(address)); joined = true; }
                    catch (SocketException) { }
                }
                if (!joined) udp.JoinMulticastGroup(Group);
                udp.MulticastLoopback = true;
                udp.Ttl = 1;
            }
            _udp = udp;
        }
        catch (SocketException error)
        {
            _udp?.Dispose();
            _udp = null;
            ipv4Error = error;
        }

        if (_enableMulticast) _udpV6.AddRange(CreateIpv6MulticastSockets());
        if (_udp is null && _udpV6.Count == 0)
        {
            var detail = ipv4Error is null ? string.Empty : $"{ipv4Error.SocketErrorCode}";
            Warning?.Invoke($"LocalSend互換の端末検出を開始できません。ファイアウォールとUDPポート{_discoveryPort}を確認してください。{detail}");
            return;
        }

        _nextLegacyProbe = DateTimeOffset.UtcNow.AddSeconds(10);
        if (_udp is not null) _receiveTask = ReceiveAsync(_udp);
        _receiveV6Tasks = _udpV6.Select(socket => ReceiveAsync(socket.Socket)).ToArray();
        _announceTask = AnnounceAsync();
    }

    private List<(UdpClient Socket, int InterfaceIndex)> CreateIpv6MulticastSockets()
    {
        var sockets = new List<(UdpClient Socket, int InterfaceIndex)>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!TransferServer.IsLocalSendInterface(network)) continue;

            IPv6InterfaceProperties? ipv6;
            try { ipv6 = network.GetIPProperties().GetIPv6Properties(); }
            catch (NetworkInformationException) { continue; }
            if (ipv6 is null || ipv6.Index <= 0) continue;

            UdpClient? udp = null;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.DualMode = false;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, _discoveryPort));
                udp.JoinMulticastGroup(ipv6.Index, GroupV6);
                udp.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, ipv6.Index);
                udp.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 1);
                udp.MulticastLoopback = true;
                sockets.Add((udp, ipv6.Index));
                udp = null;
            }
            catch (Exception error) when (error is SocketException or ArgumentException or NetworkInformationException)
            {
                udp?.Dispose();
            }
        }
        return sockets;
    }

    private Announcement CreateAnnouncement(bool announce) => new(
        _device.Alias, "2.0", _device.DeviceModel ?? "Windows", _device.DeviceType ?? "desktop",
        _device.Fingerprint, _device.Port ?? 53317, _device.Protocol ?? "https", _device.Download, announce);

    private async Task SendMulticastAsync(bool announce, CancellationToken cancellationToken)
    {
        if (!_enableMulticast) return;
        var data = JsonSerializer.SerializeToUtf8Bytes(CreateAnnouncement(announce));
        if (_udp is not null)
        {
            var destination = new IPEndPoint(Group, _discoveryPort);
            var sent = false;
            foreach (var addressText in TransferServer.LocalIpv4Addresses())
            {
                try
                {
                    var address = IPAddress.Parse(addressText);
                    _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await _udp.SendAsync(data, destination, cancellationToken);
                    sent = true;
                }
                catch (Exception error) when (error is SocketException or ArgumentException or FormatException) { }
            }
            if (!sent)
            {
                try
                {
                    _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, 0);
                    await _udp.SendAsync(data, destination, cancellationToken);
                }
                catch (SocketException) { }
            }
        }

        foreach (var (udp, interfaceIndex) in _udpV6)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                var destinationAddress = new IPAddress(GroupV6.GetAddressBytes(), interfaceIndex);
                await udp.SendAsync(data, new IPEndPoint(destinationAddress, _discoveryPort), cancellationToken);
            }
            catch (Exception error) when (error is SocketException or ArgumentException or ObjectDisposedException) { }
        }
    }

    private async Task AnnounceAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await SendMulticastAsync(true, _stop.Token); }
                catch (SocketException) { }
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in _peers.Where(pair => now - pair.Value.SeenAt > TimeSpan.FromSeconds(20))) _peers.TryRemove(pair.Key, out _);
                Changed?.Invoke(_peers.Values.OrderBy(peer => peer.Name).ThenBy(peer => peer.Address).ToArray());
                if (_enableBroadcast && now >= _nextLegacyProbe)
                {
                    _nextLegacyProbe = now.AddSeconds(15);
                    _ = ProbeLegacyDevicesAsync();
                }
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReceiveAsync(UdpClient udp)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var datagram = await udp.ReceiveAsync(_stop.Token);
                if (datagram.Buffer.Length is 0 or > 4096) continue;
                if (!TransferServer.IsLocalSendPeerAddress(datagram.RemoteEndPoint.Address)) continue;
                Announcement? message;
                try { message = JsonSerializer.Deserialize<Announcement>(datagram.Buffer); }
                catch (JsonException) { continue; }
                if (!IsValidAnnouncement(message) || IsSelf(message!, datagram.RemoteEndPoint.Address)) continue;

                var peer = new PeerDevice(message!.Alias, datagram.RemoteEndPoint.Address.ToString(), message.Port,
                    message.Fingerprint ?? string.Empty, DateTimeOffset.UtcNow, "localsend/2", message.Protocol, message.DeviceType);
                _peers[PeerKey(peer)] = peer;
                Changed?.Invoke(_peers.Values.OrderBy(item => item.Name).ThenBy(item => item.Address).ToArray());

                if (message.Announce == true) _ = RespondToAnnouncementAsync(message, datagram.RemoteEndPoint.Address);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { if (!_stop.IsCancellationRequested) Warning?.Invoke("LocalSend互換の自動検出が停止しました。接続URLを使用してください。"); }
    }

    private bool IsSelf(Announcement message, IPAddress address)
    {
        if (!string.IsNullOrEmpty(message.Fingerprint) && message.Fingerprint.Equals(_device.Fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
        return message.Port == _device.Port && message.Protocol == _device.Protocol &&
            TransferServer.LocalAddresses().Contains(address.ToString(), StringComparer.Ordinal);
    }

    private static bool IsValidAnnouncement(Announcement? message) => message is not null &&
        !string.IsNullOrWhiteSpace(message.Alias) && message.Alias.Length <= 64 &&
        !string.IsNullOrWhiteSpace(message.Version) && message.Version.StartsWith("2.", StringComparison.Ordinal) && message.Version.Length <= 16 &&
        message.DeviceModel is not { Length: > 128 } && message.DeviceType is not { Length: > 32 } &&
        !string.IsNullOrWhiteSpace(message.Fingerprint) && message.Fingerprint.Length <= 256 && message.Port is >= 1 and <= 65535 &&
        (message.Protocol is "http" or "https") &&
        (message.Protocol != "https" || message.Fingerprint is { Length: 64 } && message.Fingerprint.All(Uri.IsHexDigit));

    private static string PeerKey(PeerDevice peer) => string.IsNullOrWhiteSpace(peer.Fingerprint)
        ? $"localsend|{peer.Address}|{peer.Port}"
        : $"localsend|{peer.Fingerprint}";

    private async Task RespondToAnnouncementAsync(Announcement remote, IPAddress address)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var origin = CreatePeerOrigin(remote.Protocol, address, remote.Port);
            var endpoint = new Uri(origin, "/api/localsend/v2/register");
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            if (remote.Protocol == "https")
            {
                var fingerprint = remote.Fingerprint ?? string.Empty;
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                    fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit) &&
                    Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
                AddClientCertificate(handler);
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.PostAsJsonAsync(endpoint, CreateAnnouncementInfo(), timeout.Token);
            if (!response.IsSuccessStatusCode) return;
            var info = await response.Content.ReadFromJsonAsync<LocalSendDeviceInfo>(timeout.Token);
            if (info is not null && IsValidPeerInfo(info, requireEndpoint: false))
            {
                var peer = new PeerDevice(info.Alias, address.ToString(), info.Port ?? remote.Port,
                    info.Fingerprint ?? remote.Fingerprint ?? string.Empty, DateTimeOffset.UtcNow, "localsend/2", info.Protocol ?? remote.Protocol, info.DeviceType);
                _peers[PeerKey(peer)] = peer;
                Changed?.Invoke(_peers.Values.OrderBy(item => item.Name).ThenBy(item => item.Address).ToArray());
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or System.Security.Authentication.AuthenticationException) { }
        finally
        {
            try { await SendMulticastAsync(false, _stop.Token); }
            catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static Uri CreatePeerOrigin(string protocol, IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? new Uri($"{protocol}://{TransferServer.FormatUriHost(address.ToString())}:{port}/", UriKind.Absolute)
            : new UriBuilder(protocol, address.ToString(), port).Uri;

    private LocalSendDeviceInfo CreateAnnouncementInfo() => new(_device.Alias, "2.0", _device.DeviceModel ?? "Windows",
        _device.DeviceType ?? "desktop", _device.Fingerprint, _device.Port, _device.Protocol, _device.Download);

    private async Task ProbeLegacyDevicesAsync()
    {
        if (Interlocked.Exchange(ref _legacyProbeRunning, 1) != 0) return;
        try
        {
            using var concurrency = new SemaphoreSlim(16, 16);
            var probes = GetLegacyProbeAddresses().Select(async address =>
            {
                await concurrency.WaitAsync(_stop.Token);
                try
                {
                    if (HasPeerAtAddress(address)) return;
                    await ProbeLegacyDeviceAsync(address, "http");
                    if (!_stop.IsCancellationRequested && !HasPeerAtAddress(address))
                        await ProbeLegacyDeviceAsync(address, "https");
                }
                finally { concurrency.Release(); }
            });
            await Task.WhenAll(probes);
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref _legacyProbeRunning, 0); }
    }

    private bool HasPeerAtAddress(IPAddress address) => !ShouldProbeLegacyAddress(_peers.Values, address);

    internal static bool ShouldProbeLegacyAddress(IEnumerable<PeerDevice> peers, IPAddress address) =>
        !peers.Any(peer => peer.Address.Equals(address.ToString(), StringComparison.Ordinal));

    internal async Task ProbeLegacyDeviceAsync(IPAddress address, string protocol, int port = DefaultHttpPort)
    {
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("IPv4またはIPv6アドレスを指定してください。", nameof(address));
        if (!TransferServer.IsLocalSendPeerAddress(address)) return;
        if (protocol is not ("http" or "https")) throw new ArgumentException("HTTPまたはHTTPSを指定してください。", nameof(protocol));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        var fingerprint = string.Empty;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        if (protocol == "https")
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
                return true;
            };
            AddClientCertificate(handler);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var origin = new UriBuilder(protocol, address.ToString(), port).Uri;
            using var response = await client.PostAsJsonAsync(new Uri(origin, "/api/localsend/v2/register"), CreateAnnouncementInfo(), timeout.Token);
            if (!response.IsSuccessStatusCode) return;
            var info = await response.Content.ReadFromJsonAsync<LocalSendDeviceInfo>(timeout.Token);
            if (info is null || !IsValidPeerInfo(info, requireEndpoint: false) ||
                protocol == "https" && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))) return;
            var peerProtocol = info.Protocol ?? protocol;
            var peerPort = info.Port ?? port;
            // LocalSend v2 explicitly ignores JSON fingerprint claims over HTTPS.
            // Pin the certificate observed on this connection, not an untrusted
            // or independently generated value returned in the registration body.
            var peerFingerprint = protocol == "https" ? fingerprint : info.Fingerprint ?? fingerprint;
            var peer = new PeerDevice(info.Alias, address.ToString(), peerPort, peerFingerprint,
                DateTimeOffset.UtcNow, "localsend/2", peerProtocol, info.DeviceType);
            if (IsSelf(new(info.Alias, info.Version, info.DeviceModel, info.DeviceType, peerFingerprint, peerPort, peerProtocol, info.Download, null), address)) return;
            _peers[PeerKey(peer)] = peer;
            Changed?.Invoke(_peers.Values.OrderBy(item => item.Name).ThenBy(item => item.Address).ToArray());
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or System.Security.Authentication.AuthenticationException) { }
    }

    internal static IReadOnlyList<IPAddress> EnumerateHostAddresses(IPAddress address, IPAddress subnetMask, int maximum)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || subnetMask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4アドレスとIPv4サブネットマスクを指定してください。");
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (maximum == 0) return [];

        var addressValue = (ulong)ToUInt32(address);
        var mask = ToUInt32(subnetMask);
        var network = addressValue & mask;
        var first = network + 1;
        var last = (ulong)((uint)network | ~mask);
        var result = new List<IPAddress>();

        // Large office subnets can contain far more than the bounded fallback
        // scan. Probe nearby addresses first so a /16 does not spend its whole
        // budget on hosts near the subnet's lowest address, far from this PC.
        for (ulong distance = 1; result.Count < maximum &&
             (addressValue >= first + distance || addressValue + distance < last); distance++)
        {
            if (addressValue >= first + distance)
                result.Add(ToAddress(addressValue - distance));
            if (result.Count < maximum && addressValue + distance < last)
                result.Add(ToAddress(addressValue + distance));
        }
        return result;
    }

    private static IPAddress ToAddress(ulong value)
    {
        var address = (uint)value;
        return new IPAddress(new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address });
    }

    private static IReadOnlyList<IPAddress> GetLegacyProbeAddresses()
    {
        const int maxAddresses = 512;
        var ranges = new List<(IPAddress Address, IPAddress Mask, bool LinkLocal, bool HasGateway)>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!TransferServer.IsLocalSendInterface(network)) continue;
            IPInterfaceProperties properties;
            try { properties = network.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            var hasGateway = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null ||
                    !TransferServer.IsLocalSendAddress(unicast.Address)) continue;
                ranges.Add((unicast.Address, unicast.IPv4Mask, IsLinkLocalIPv4(unicast.Address), hasGateway));
            }
        }

        // APIPA adapters (Bluetooth, disconnected Ethernet, etc.) can expose
        // 65K addresses; scan them after routed LAN interfaces so they cannot
        // consume the fallback budget before active Wi-Fi/Ethernet subnets.
        var result = new List<IPAddress>(maxAddresses);
        var seen = new HashSet<IPAddress>();
        foreach (var range in ranges.OrderBy(item => item.LinkLocal).ThenByDescending(item => item.HasGateway))
        {
            foreach (var candidate in EnumerateHostAddresses(range.Address, range.Mask, maxAddresses - result.Count))
                if (seen.Add(candidate)) result.Add(candidate);
            if (result.Count >= maxAddresses) break;
        }
        return result;
    }

    private static bool IsLinkLocalIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254;
    }

    private void AddClientCertificate(HttpClientHandler handler)
    {
        if (_clientCertificate is not { HasPrivateKey: true }) return;
        handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        handler.ClientCertificates.Add(_clientCertificate);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static bool IsValidPeerInfo(LocalSendDeviceInfo info, bool requireEndpoint) =>
        !string.IsNullOrWhiteSpace(info.Alias) && info.Alias.Length <= 64 && !string.IsNullOrWhiteSpace(info.Version) &&
        info.Version.Length <= 16 && info.Version.StartsWith("2.", StringComparison.Ordinal) &&
        info.Fingerprint is not { Length: > 256 } && info.DeviceModel is not { Length: > 128 } && info.DeviceType is not { Length: > 32 } &&
        (info.Protocol is null or "http" or "https") && (info.Port is null or >= 1 and <= 65535) &&
        (!requireEndpoint || (info.Protocol is "http" or "https") && info.Port is >= 1 and <= 65535);

    internal static IPAddress CalculateBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        if (address.AddressFamily != AddressFamily.InterNetwork || subnetMask.AddressFamily != AddressFamily.InterNetwork ||
            addressBytes.Length != 4 || maskBytes.Length != 4)
            throw new ArgumentException("IPv4アドレスとIPv4サブネットマスクを指定してください。");

        var broadcast = new byte[4];
        for (var index = 0; index < broadcast.Length; index++) broadcast[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
        return new IPAddress(broadcast);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_receiveTask is not null) await _receiveTask;
        if (_receiveV6Tasks.Length > 0) await Task.WhenAll(_receiveV6Tasks);
        if (_announceTask is not null) await _announceTask;
        _udp?.Dispose();
        foreach (var (socket, _) in _udpV6) socket.Dispose();
        _stop.Dispose();
    }
}
