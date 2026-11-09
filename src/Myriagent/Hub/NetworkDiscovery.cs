using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// 找區域網路上有檔案分享的電腦（電腦狀態面板的檔案傳輸「網路」那一層；2026-11-09 實機：舊式的網路芳鄰列舉回 6118、什麼都拿不到）：
/// 同時做三件事，合起來約 2.5～6 秒——
/// <list type="bullet">
/// <item>對這台所在的網段逐一送 NetBIOS 節點狀態查詢（UDP 137，<see cref="NetBiosRules"/>），回覆裡有檔案分享服務（&lt;20&gt;）的才列，名稱用它回的電腦名稱；</item>
/// <item>試連網段裡每個位址的 445 埠（只建立 TCP 連線，不送 SMB 封包）：NetBIOS 關掉的 NAS 與 Windows 也找得到；</item>
/// <item>WS-Discovery 群播（239.255.255.250:3702）找 Windows 電腦（<c>pub:Computer</c>）與支援它的 NAS；NetBIOS 沒問到名稱的以反查 DNS 取名，取不到就用位址。</item>
/// </list>
/// 只送查詢、不連任何分享；這台自己的位址不查
/// </summary>
internal static class NetworkDiscovery
{
    public static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(2200);

    private static readonly IPEndPoint WsdEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    /// <summary>找到的電腦（名稱，或取不到名稱時的位址），依名稱排序、不重複</summary>
    /// <summary>上一次找的時候掃了哪些網段、找到幾台（找不到時給使用者看，判斷是不是掃錯了網卡）</summary>
    public static string? LastSummary { get; private set; }

    /// <summary>反查 DNS 取名稱最多一共等多久（取不到的用位址）</summary>
    private static readonly TimeSpan NameBudget = TimeSpan.FromSeconds(2);

    public static async Task<IReadOnlyList<string>> FindAsync(CancellationToken ct)
    {
        var adapters = LocalIPv4();
        var local = NetBiosRules.SweepAdapters(adapters);
        var own = adapters.Select(l => l.Address).ToHashSet();
        var targets = NetBiosRules.SweepTargets(local);
        var netbios = SweepNetBiosAsync(targets, ct);
        var wsd = ProbeWsdAsync(local.Select(l => l.Address).ToList(), ct);
        var smb = SweepSmbAsync(targets, ct);
        await Task.WhenAll(netbios, wsd, smb).ConfigureAwait(false);

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new HashSet<IPAddress>();
        foreach (var (ip, status) in netbios.Result)
        {
            if (status.ServerName is { } server)
            {
                names.Add(server);
                named.Add(ip);
            }
        }

        // 445 埠開著或 WS-Discovery 找到、NetBIOS 沒問到伺服器名稱的：工作站名稱、反查 DNS，取不到就用位址
        var extra = smb.Result.Concat(wsd.Result).Where(ip => !named.Contains(ip) && !own.Contains(ip)).Distinct().Take(64).ToList();
        var lookups = extra.Select(ip => (Ip: ip, Name: LookupAsync(ip))).ToList();
        try
        {
            await Task.WhenAll(lookups.Select(l => l.Name)).WaitAsync(NameBudget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 反查太慢：取到的用名稱，其他用位址
        }

        foreach (var (ip, name) in lookups)
            names.Add(name.IsCompletedSuccessfully ? name.Result : ip.ToString());
        names.RemoveWhere(n => string.Equals(n, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        LastSummary = NetBiosRules.SweepSummary(local, targets.Count, names.Count);
        return names.ToList();

        async Task<string> LookupAsync(IPAddress ip)
        {
            if (netbios.Result.TryGetValue(ip, out var status) && status.WorkstationName is { } workstation)
                return workstation;
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip.ToString(), ct).WaitAsync(NameBudget, ct).ConfigureAwait(false);
                var host = entry.HostName.Split('.')[0];
                return NetworkPathRules.IsHostName(host) && !IPAddress.TryParse(host, out _) ? host : ip.ToString();
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
            {
                return ip.ToString();
            }
        }
    }

    /// <summary>這台開著的網卡的 IPv4 位址、前綴、有沒有預設閘道與名稱（迴路與通道介面不算）</summary>
    private static List<DiscoveryAdapter> LocalIPv4()
    {
        var list = new List<DiscoveryAdapter>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(new DiscoveryAdapter(unicast.Address, unicast.PrefixLength, gateway, nic.Name, nic.Description));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"列不出這台的網卡：{ex.Message}");
        }

        return list;
    }

    /// <summary>NetBIOS 節點狀態：一個 UDP 通訊端送給每個位址，等 <see cref="Wait"/> 收回覆</summary>
    private static async Task<Dictionary<IPAddress, NetBiosNodeStatus>> SweepNetBiosAsync(IReadOnlyList<IPAddress> targets, CancellationToken ct)
    {
        var result = new Dictionary<IPAddress, NetBiosNodeStatus>();
        if (targets.Count == 0)
            return result;
        try
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            IgnoreConnectionReset(socket);
            var query = NetBiosRules.NodeStatusQuery((ushort)Random.Shared.Next(1, ushort.MaxValue));
            var receiving = ReceiveAllAsync(socket, Wait, (remote, data) =>
            {
                if (NetBiosRules.ParseNodeStatus(data) is { } status)
                    result[remote.Address] = status;
            }, ct);
            for (var i = 0; i < targets.Count; i++)
            {
                try
                {
                    await socket.SendAsync(query, new IPEndPoint(targets[i], NetBiosRules.Port), ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // 送不出去的（沒有路由）：略過
                }

                if (i % 64 == 63)
                    await Task.Delay(5, ct).ConfigureAwait(false);
            }

            await receiving.ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            Log.Info($"NetBIOS 查詢失敗：{ex.Message}");
        }

        return result;
    }

    /// <summary>一個位址試連 445 埠最多等多久</summary>
    private static readonly TimeSpan SmbConnectTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// 試連每個位址的 445 埠（檔案分享；同時最多 256 個、每個最多等 <see cref="SmbConnectTimeout"/>），連得上的就是有檔案分享的電腦——
    /// NetBIOS 關掉的 NAS 與 Windows 也找得到。只建立 TCP 連線就關，不送任何 SMB 封包（不會送出登入資訊）
    /// </summary>
    private static async Task<List<IPAddress>> SweepSmbAsync(IReadOnlyList<IPAddress> targets, CancellationToken ct)
    {
        var open = new List<IPAddress>();
        using var gate = new SemaphoreSlim(256);
        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SmbConnectTimeout);
                await socket.ConnectAsync(new IPEndPoint(ip, 445), timeout.Token).ConfigureAwait(false);
                lock (open)
                    open.Add(ip);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // 沒開、沒有這台、逾時：略過
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return open;
    }

    /// <summary>WS-Discovery：從每個 IPv4 位址各送一次 Probe（找 Windows 電腦），收回覆的來源位址</summary>
    private static async Task<List<IPAddress>> ProbeWsdAsync(IReadOnlyList<IPAddress> localAddresses, CancellationToken ct)
    {
        var found = new List<IPAddress>();
        var probe = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            "xmlns:wsd=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" xmlns:wsdp=\"http://schemas.xmlsoap.org/ws/2006/02/devprof\" " +
            "xmlns:pub=\"http://schemas.microsoft.com/windows/pub/2005/07\"><soap:Header>" +
            "<wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>" +
            "<wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>" +
            "<wsa:MessageID>urn:uuid:" + Guid.NewGuid().ToString("D") + "</wsa:MessageID></soap:Header>" +
            "<soap:Body><wsd:Probe><wsd:Types>wsdp:Device</wsd:Types></wsd:Probe></soap:Body></soap:Envelope>");
        var sockets = new List<UdpClient>();
        try
        {
            var receivers = new List<Task>();
            foreach (var address in localAddresses.Where(a => NetworkPathRules.IsLanAddress(a) && a.GetAddressBytes()[0] != 169))
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(address, 0));
                    sockets.Add(socket);
                    IgnoreConnectionReset(socket);
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    receivers.Add(ReceiveAllAsync(socket, Wait, (remote, data) =>
                    {
                        // 只算電腦與檔案伺服器（Windows 是 pub:Computer；印表機、掃描器、媒體裝置不列）
                        var text = Encoding.UTF8.GetString(data);
                        if (text.Contains("ProbeMatch", StringComparison.Ordinal) && text.Contains("Computer", StringComparison.Ordinal))
                        {
                            lock (found)
                                found.Add(remote.Address);
                        }
                    }, ct));
                    await socket.SendAsync(probe, WsdEndpoint, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    Log.Info($"WS-Discovery 從 {address} 送不出去：{ex.Message}");
                }
            }

            await Task.WhenAll(receivers).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets)
                socket.Dispose();
        }

        lock (found)
            return found.Distinct().ToList();
    }

    /// <summary>不讓對方回的 ICMP「埠無法到達」變成下一次接收的例外（SIO_UDP_CONNRESET）</summary>
    private static void IgnoreConnectionReset(UdpClient socket)
    {
        try
        {
            socket.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // 不支援：照樣收（接收迴圈會略過那個例外）
        }
    }

    /// <summary>在 <paramref name="wait"/> 之內收所有回覆</summary>
    private static async Task ReceiveAllAsync(UdpClient socket, TimeSpan wait, Action<IPEndPoint, byte[]> onReply, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var reply = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                onReply(reply.RemoteEndPoint, reply.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // ICMP 埠無法到達（Windows 會讓下一次接收丟這個例外）：繼續收
            }
        }
    }
}
