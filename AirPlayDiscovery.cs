using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ChannelBridge;

// Windows owns the announcements; registration is scoped to the receiver lifetime.
internal sealed class AirPlayDiscovery : IDisposable
{
    static readonly SemaphoreSlim Lifetime = new(1, 1);
    readonly CancellationTokenSource stop = new();
    readonly Task task;
    public AirPlayDiscovery(int pid, string name, string deviceId, Action<string> log)
    { task = Run(pid, name, deviceId, log, stop.Token); }
    public void Dispose()
    {
        stop.Cancel();
        // Do not block the audio worker on an asynchronous DNS callback.
        _ = task.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
    internal sealed record Lan(uint Index, IPAddress Address);
    internal static Lan[] Networks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => {
            var p = n.GetIPProperties();
            // Host-only VM adapters have no LAN gateway. Never disable adapters.
            if (!p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) return Array.Empty<Lan>();
            return p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => new Lan((uint)p.GetIPv4Properties().Index, a.Address));
        }).OrderBy(n => n.Index).ThenBy(n => n.Address.ToString()).ToArray();

    static async Task Run(int pid, string name, string deviceId, Action<string> log, CancellationToken token)
    {
        try { await Lifetime.WaitAsync(token); }
        catch (OperationCanceledException) { return; }
        var records = new List<Registration>();
        string signature = "", lastError = "";
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var networks = Networks();
                    string next = string.Join(";", networks.Select(n => $"{n.Index}:{n.Address}"));
                    if (next != signature || records.Count == 0 || records.Any(r => r.Status != 0))
                    {
                        foreach (var r in records) await r.Close();
                        records.Clear(); signature = "";
                        if (networks.Length == 0) throw new IOException("等待可用的局域网网卡。");
                        var (port, info) = await ReceiverInfo(pid, token);
                        if ((string)info["deviceID"] != deviceId) throw new IOException("接收端标识不匹配。");
                        var (air, raop) = Properties(info);
                        foreach (var lan in networks)
                        {
                            foreach (var entry in new[] { (name + "._airplay._tcp.local", air),
                                (deviceId.Replace(":", "").ToUpperInvariant() + "@" + name + "._raop._tcp.local", raop) })
                            {
                                var r = new Registration(entry.Item1, lan, port, entry.Item2);
                                records.Add(r); await r.Open();
                                if (r.Status != 0) throw new IOException("Windows DNS-SD 注册失败：" + r.Status);
                            }
                        }
                        signature = next; lastError = "";
                        log($"Windows AirPlay discovery active: {next}, port {port}");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (lastError != ex.Message) { lastError = ex.Message; log("AirPlay discovery: " + lastError); }
                    foreach (var r in records) await r.Close();
                    records.Clear(); signature = "";
                }
                await Task.Delay(10000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            try { foreach (var r in records) await r.Close(); }
            finally { Lifetime.Release(); }
        }
    }

    internal static (Dictionary<string, string>, Dictionary<string, string>) Properties(Dictionary<string, object> info)
    {
        var features = (ulong)info["features"];
        string ft = $"0x{features & 0xffffffff:X},0x{features >> 32:X}", pk = Convert.ToHexString((byte[])info["pk"]).ToLowerInvariant();
        string model = (string)info["model"], version = (string)info["sourceVersion"];
        return (new() { ["deviceid"] = (string)info["deviceID"], ["features"] = ft, ["pw"] = "false", ["flags"] = "0x4",
            ["model"] = model, ["pk"] = pk, ["pi"] = (string)info["pi"], ["srcvers"] = version, ["vv"] = info["vv"].ToString()! },
            new() { ["ch"] = "2", ["cn"] = "0,1,2,3", ["da"] = "true", ["et"] = "0,3,5", ["vv"] = "2", ["ft"] = ft,
                ["am"] = model, ["md"] = "0,1,2", ["rhd"] = "5.6.0.0", ["pw"] = "false", ["sr"] = "44100", ["ss"] = "16",
                ["sv"] = "false", ["tp"] = "UDP", ["txtvers"] = "1", ["sf"] = "0x4", ["vs"] = version, ["vn"] = "65537", ["pk"] = pk });
    }

    static async Task<(ushort, Dictionary<string, object>)> ReceiverInfo(int pid, CancellationToken token)
    {
        foreach (ushort port in Ports(pid))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(2000);
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                using var stream = client.GetStream();
                await stream.WriteAsync("GET /info RTSP/1.0\r\nCSeq: 1\r\n\r\n"u8.ToArray(), timeout.Token);
                var header = new List<byte>(); byte[] one = new byte[1];
                while (header.Count < 8192)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token); header.Add(one[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                string text = Encoding.ASCII.GetString(header.ToArray());
                if (!text.StartsWith("RTSP/1.0 200") && !text.StartsWith("HTTP/1.1 200")) continue;
                string value = text.Split("\r\n").First(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                int length = int.Parse(value); if (length < 8 || length > 262144) throw new IOException("Invalid receiver info size.");
                var data = new byte[length]; await stream.ReadExactlyAsync(data, timeout.Token);
                return (port, BinaryPlist.Read(data));
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("等待接收引擎监听端口及公开信息。");
    }

    static ushort[] Ports(int pid)
    {
        int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint result = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
            var ports = new List<ushort>(); int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = IntPtr.Add(buffer, 4 + i * 24);
                if (Marshal.ReadInt32(row) == 2 && Marshal.ReadInt32(row, 20) == pid)
                    ports.Add((ushort)((Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9)));
            }
            return ports.Distinct().ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    sealed class Registration
    {
        IntPtr instance;
        readonly uint index;
        public uint Status { get; private set; } = uint.MaxValue;
        public Registration(string name, Lan lan, ushort port, Dictionary<string, string> props)
        {
            index = lan.Index; uint address = BitConverter.ToUInt32(lan.Address.GetAddressBytes());
            instance = DnsServiceConstructInstance(name, Dns.GetHostName() + ".local", ref address, IntPtr.Zero,
                port, 0, 0, (uint)props.Count, props.Keys.ToArray(), props.Values.ToArray());
            if (instance == IntPtr.Zero) throw new IOException("无法创建 Windows DNS-SD 注册信息。");
        }
        public async Task Open() { Status = await Invoke(false); }
        public async Task Close()
        {
            if (instance == IntPtr.Zero) return;
            try { if (Status == 0) await Invoke(true); }
            finally { DnsServiceFreeInstance(instance); instance = IntPtr.Zero; }
        }
        Task<uint> Invoke(bool remove)
        {
            var operation = new Operation();
            var handle = GCHandle.Alloc(operation);
            var request = new Request { Version = 1, InterfaceIndex = index, Instance = instance,
                Callback = Marshal.GetFunctionPointerForDelegate(Completion), Context = GCHandle.ToIntPtr(handle) };
            operation.Request = Marshal.AllocHGlobal(Marshal.SizeOf<Request>());
            Marshal.StructureToPtr(request, operation.Request, false);
            uint status = remove ? DnsServiceDeRegister(operation.Request, IntPtr.Zero) : DnsServiceRegister(operation.Request, IntPtr.Zero);
            if (status != 9506) { Marshal.FreeHGlobal(operation.Request); handle.Free(); operation.Done.TrySetResult(status); }
            return operation.Done.Task;
        }
    }
    sealed class Operation
    {
        public IntPtr Request;
        public readonly TaskCompletionSource<uint> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    [StructLayout(LayoutKind.Sequential)] struct Request
    { public uint Version, InterfaceIndex; public IntPtr Instance, Callback, Context, Credentials; public int Unicast; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Callback(uint status, IntPtr context, IntPtr instance);
    static readonly Callback Completion = (status, context, instance) => {
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
        var handle = GCHandle.FromIntPtr(context); var operation = (Operation)handle.Target!;
        Marshal.FreeHGlobal(operation.Request); handle.Free(); operation.Done.TrySetResult(status);
    };
    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr DnsServiceConstructInstance(string name, string host,
        ref uint ipv4, IntPtr ipv6, ushort port, ushort priority, ushort weight, uint count,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);
    [DllImport("dnsapi.dll")] static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] static extern void DnsServiceFreeInstance(IntPtr instance);
}

// Bounded parser for the public metadata dictionary returned by UxPlay GET /info.
internal static class BinaryPlist
{
    public static Dictionary<string, object> Read(byte[] data)
    {
        if (data.Length < 40 || Encoding.ASCII.GetString(data, 0, 8) != "bplist00") throw new InvalidDataException("Invalid plist header.");
        ulong UInt(int pos, int bytes)
        {
            if (bytes is < 1 or > 8 || pos < 0 || pos > data.Length - bytes) throw new InvalidDataException("Invalid plist integer.");
            ulong value = 0; for (int i = 0; i < bytes; i++) value = (value << 8) | data[pos + i]; return value;
        }
        int trailer = data.Length - 32, width = data[trailer + 6], refs = data[trailer + 7];
        int count = checked((int)UInt(trailer + 8, 8)), top = checked((int)UInt(trailer + 16, 8)), offsets = checked((int)UInt(trailer + 24, 8));
        if (count < 1 || count > 16384 || width is < 1 or > 8 || refs is < 1 or > 8 || offsets < 8 || (long)offsets + (long)count * width > trailer) throw new InvalidDataException("Invalid plist table.");
        object Item(int id, int depth)
        {
            if (id < 0 || id >= count || depth > 12) throw new InvalidDataException("Invalid plist reference.");
            int p = checked((int)UInt(offsets + id * width, width));
            if (p < 8 || p >= offsets) throw new InvalidDataException("Invalid plist offset.");
            int marker = data[p++], kind = marker >> 4, length = marker & 15;
            if (kind == 0) return marker == 9;
            if (kind == 1) return UInt(p, 1 << length);
            if (length == 15)
            {
                int m = data[p++]; if ((m >> 4) != 1) throw new InvalidDataException("Invalid plist length.");
                int bytes = 1 << (m & 15); length = checked((int)UInt(p, bytes)); p += bytes;
            }
            int unit = kind == 6 ? 2 : kind is 10 or 13 ? refs : 1;
            long end = p + (long)length * unit * (kind == 13 ? 2 : 1);
            if (length < 0 || end > offsets) throw new InvalidDataException("Invalid plist range.");
            if (kind == 4) return data.AsSpan(p, length).ToArray();
            if (kind == 5) return Encoding.UTF8.GetString(data, p, length);
            if (kind == 6) return Encoding.BigEndianUnicode.GetString(data, p, length * 2);
            if (kind == 10) return Enumerable.Range(0, length).Select(i => Item(checked((int)UInt(p + i * refs, refs)), depth + 1)).ToArray();
            if (kind == 13)
            {
                var dict = new Dictionary<string, object>();
                for (int i = 0; i < length; i++) dict.Add((string)Item(checked((int)UInt(p + i * refs, refs)), depth + 1), Item(checked((int)UInt(p + (length + i) * refs, refs)), depth + 1));
                return dict;
            }
            // /info may include real/date values that aren't used for DNS-SD.
            if (kind is 2 or 3) return 0UL;
            throw new InvalidDataException("Unsupported plist type.");
        }
        return Item(top, 0) as Dictionary<string, object> ?? throw new InvalidDataException("Invalid plist root.");
    }
}
