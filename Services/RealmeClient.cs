using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace RealmeBuds;

// ----------------------------------------------------------------- models --
public sealed record BatteryEntry(int Device, int? Percent, bool Charging);
public sealed record GestureEntry(int Device, int Action, int Func);
public sealed record GestureQuery(List<GestureEntry>? Entries, byte[] Raw);
public sealed record Ack(bool Responded, int Status)
{
    public bool Ok => Responded && Status == 0;
}

// ------------------------------------------------------------- transports --
public interface ITransport : IDisposable
{
    bool IsOpen { get; }
    Task SendAsync(byte[] data);
    /// <summary>Reads at least one byte; throws OperationCanceledException when ct fires; 0 = closed.</summary>
    Task<int> ReceiveAsync(byte[] buf, CancellationToken ct);
}

/// <summary>SOCKADDR_BTH wrapper so System.Net.Sockets can open AF_BTH / RFCOMM sockets.</summary>
public sealed class BtEndPoint : EndPoint
{
    public const AddressFamily AF_BTH = (AddressFamily)32;
    public const ProtocolType BTHPROTO_RFCOMM = (ProtocolType)3;

    public ulong Address { get; }
    public int Port { get; }

    /// <summary>Serial Port Profile service class, used so Windows resolves the channel itself (SDP) when Port = 0.</summary>
    private static readonly Guid SerialPortService = new("00001101-0000-1000-8000-00805F9B34FB");

    public BtEndPoint(ulong address, int port) { Address = address; Port = port; }

    public override AddressFamily AddressFamily => AF_BTH;

    // struct SOCKADDR_BTH (packed): USHORT family; ULONGLONG btAddr; GUID serviceClassId; ULONG port;  = 30 bytes
    public override SocketAddress Serialize()
    {
        var sa = new SocketAddress(AF_BTH, 30);
        for (int i = 0; i < 8; i++) sa[2 + i] = (byte)(Address >> (8 * i));
        if (Port == 0)
        {
            var guid = SerialPortService.ToByteArray();
            for (int i = 0; i < 16; i++) sa[10 + i] = guid[i];
        }
        for (int i = 0; i < 4; i++) sa[26 + i] = (byte)(Port >> (8 * i));
        return sa;
    }

    public override EndPoint Create(SocketAddress socketAddress) => this;
}

internal sealed class SocketTransport : ITransport
{
    private readonly Socket _s;
    public SocketTransport(Socket s) => _s = s;
    public bool IsOpen => _s.Connected;

    public async Task SendAsync(byte[] data) =>
        await _s.SendAsync(data.AsMemory(), SocketFlags.None);

    public async Task<int> ReceiveAsync(byte[] buf, CancellationToken ct) =>
        await _s.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct);

    public void Dispose()
    {
        try { _s.Close(); } catch { }
        _s.Dispose();
    }
}

/// <summary>Bluetooth virtual COM port (the transport the working Python tool uses).</summary>
internal sealed class ComTransport : ITransport
{
    private readonly SerialPort _p;
    public ComTransport(SerialPort p) => _p = p;
    public bool IsOpen => _p.IsOpen;

    public Task SendAsync(byte[] data) => Task.Run(() => _p.Write(data, 0, data.Length));

    public async Task<int> ReceiveAsync(byte[] buf, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Each blocking read is awaited to completion (<= ReadTimeout) so no stale reader survives a cancel.
            int n = await Task.Run(() =>
            {
                try { return _p.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { return 0; }
            });
            if (n > 0) return n;
        }
    }

    public void Dispose()
    {
        try { _p.Close(); } catch { }
        _p.Dispose();
    }
}

// -------------------------------------------------------- protocol tables --
public static class Proto
{
    public const ushort CmdGetBattery = 0x0106;
    public const ushort CmdQueryGestures = 0x0108;
    public const ushort CmdQueryAnc = 0x010C;
    public const ushort CmdSetFeature = 0x0403;
    public const ushort CmdSetAnc = 0x0404;
    public const ushort CmdSetGestures = 0x0401;

    public const ushort CmdQueryFeatures = 0x010D;
    public const ushort RespFeatures = 0x810D;   // feature-status replies do NOT use cmd | 0x8000

    public const int FeatWind = 26, FeatGame = 6;

    public static readonly Dictionary<int, string> Devices = new() { [1] = "Left earbud", [2] = "Right earbud", [3] = "Charging case" };
    public static readonly Dictionary<int, string> Actions = new() { [2] = "Double tap", [3] = "Triple tap", [4] = "Touch & hold" };
    public static readonly Dictionary<int, string> Funcs = new()
    {
        [1] = "Play / Pause", [6] = "Next track", [5] = "Previous track", [3] = "Voice assistant",
        [8] = "Switch noise mode", [11] = "Volume up", [12] = "Volume down", [17] = "Game mode toggle",
        [13] = "Device switch", [0] = "None"
    };
    public static readonly Dictionary<int, string> AncModes = new()
    {
        [1] = "Off", [2] = "Transparency", [16] = "Light", [4] = "Medium", [8] = "Strong", [32] = "Smart"
    };

    /// <summary>Order: left double, left triple, left hold, right double, right triple, right hold.</summary>
    public static readonly Dictionary<string, int[]> Presets = new()
    {
        ["Default"] = new[] { 1, 6, 8, 1, 6, 8 },
        ["Media"]   = new[] { 1, 5, 8, 1, 6, 8 },
        ["Volume"]  = new[] { 12, 5, 8, 11, 6, 8 },
        ["Gaming"]  = new[] { 1, 5, 17, 1, 6, 8 },
    };
}

// ------------------------------------------------------------------ client --
public sealed class RealmeClient : IDisposable
{
    private ITransport? _tp;

    public int LenMode { get; set; }          // 0 = len(packet)+2 (confirmed), 1 = len(packet), 2 = total
    public int Channel { get; private set; }
    public string? ComPort { get; private set; }
    public bool Connected => _tp is { IsOpen: true };
    public event Action<string>? Log;

    // ---- framing: AA | len | 00 00 | cmd(2 LE) seq plen(2 LE) payload ----
    private byte[] BuildFrame(ushort cmd, byte[] payload)
    {
        var packet = new byte[5 + payload.Length];
        packet[0] = (byte)(cmd & 0xFF);
        packet[1] = (byte)(cmd >> 8);
        packet[2] = 0;                                   // seq: the working tool always sends 0
        packet[3] = (byte)(payload.Length & 0xFF);
        packet[4] = (byte)(payload.Length >> 8);
        payload.CopyTo(packet, 5);

        int len = LenMode switch { 1 => packet.Length, 2 => 4 + packet.Length, _ => packet.Length + 2 };
        var frame = new byte[4 + packet.Length];
        frame[0] = 0xAA;
        frame[1] = (byte)len;
        packet.CopyTo(frame, 4);
        return frame;
    }

    private static byte[]? FindResponse(List<byte> data, ushort want)
    {
        for (int i = 0; i + 5 <= data.Count; i++)
        {
            if (data[i] != (want & 0xFF) || data[i + 1] != (want >> 8)) continue;
            int pl = data[i + 3] | (data[i + 4] << 8);
            if (i + 5 + pl > data.Count) continue;
            return data.GetRange(i + 5, pl).ToArray();
        }
        return null;
    }

    public static string Hex(IEnumerable<byte> b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    // ---- connection ----
    private static async Task<ITransport?> TryConnectSocketAsync(ulong addr, int channel, int timeoutMs, CancellationToken ct)
    {
        var s = new Socket(BtEndPoint.AF_BTH, SocketType.Stream, BtEndPoint.BTHPROTO_RFCOMM);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await s.ConnectAsync(new BtEndPoint(addr, channel), cts.Token);
            return new SocketTransport(s);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            s.Dispose();
            throw;
        }
        catch
        {
            s.Dispose();
            return null;
        }
    }

    private async Task<ITransport?> TryOpenComAsync(string name)
    {
        var port = new SerialPort(name, 115200) { ReadTimeout = 200, WriteTimeout = 1500 };
        try
        {
            await Task.Run(() => port.Open());
            return new ComTransport(port);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Cannot open {name}: {ex.Message}");
            port.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Connects via a COM port (comPort != null) or an RFCOMM socket. With channel = 0 the socket asks Windows
    /// to find the Serial Port service itself, so there is no channel scanning.
    /// </summary>
    public async Task<bool> ConnectAsync(ulong addr, int channel, string? comPort, int timeoutMs,
                                         IProgress<string>? progress, CancellationToken ct)
    {
        Disconnect();

        if (!string.IsNullOrEmpty(comPort))
        {
            progress?.Report($"Opening {comPort}…");
            _tp = await TryOpenComAsync(comPort);
            ComPort = comPort;
            Channel = 0;
            return _tp != null;
        }

        ComPort = null;
        progress?.Report(channel > 0 ? $"Connecting on RFCOMM channel {channel}…" : "Connecting to the Serial Port service…");
        _tp = await TryConnectSocketAsync(addr, channel, timeoutMs, ct);
        Channel = channel;
        return _tp != null;
    }

    public void Disconnect()
    {
        _tp?.Dispose();
        _tp = null;
    }

    public void Dispose() => Disconnect();

    // ---- request / response ----
    public async Task<byte[]?> TransactAsync(ushort cmd, byte[] payload, int timeoutMs, ushort? respCmd = null)
    {
        var tp = _tp ?? throw new InvalidOperationException("Not connected");
        var frame = BuildFrame(cmd, payload);
        Log?.Invoke("TX " + Hex(frame));
        await tp.SendAsync(frame);

        using var cts = new CancellationTokenSource(timeoutMs);
        var acc = new List<byte>();
        var buf = new byte[512];
        var want = respCmd ?? (ushort)(cmd | 0x8000);
        try
        {
            while (true)
            {
                int n = await tp.ReceiveAsync(buf, cts.Token);
                if (n == 0) throw new IOException("Connection closed by the earbuds");
                var chunk = buf.AsSpan(0, n).ToArray();
                Log?.Invoke("RX " + Hex(chunk));
                acc.AddRange(chunk);
                var p = FindResponse(acc, want);
                if (p != null) return p;
            }
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke($"(no response to 0x{cmd:X4} within {timeoutMs} ms)");
            return null;
        }
    }

    private static Ack ToAck(byte[]? p) =>
        p == null ? new Ack(false, -1) : new Ack(true, p.Length > 0 ? p[0] : 0);

    // ---- high-level commands ----
    public async Task<List<BatteryEntry>?> GetBatteryAsync(int timeoutMs = 3000)
    {
        var p = await TransactAsync(Proto.CmdGetBattery, Array.Empty<byte>(), timeoutMs);
        if (p == null || p.Length < 2 || p[0] != 0 || 2 + p[1] * 2 > p.Length) return null;
        var list = new List<BatteryEntry>();
        for (int i = 0; i < p[1]; i++)
        {
            int dev = p[2 + i * 2], b = p[3 + i * 2], lvl = b & 0x7F;
            list.Add(new BatteryEntry(dev, lvl == 127 ? null : lvl, (b & 0x80) != 0));
        }
        return list;
    }

    /// <summary>Response layout (confirmed): [status][count] then count * [dev, btn, action, func].</summary>
    public async Task<GestureQuery?> QueryGesturesAsync(int timeoutMs = 3000)
    {
        byte[] req = { 14, 1, 3, 4, 5, 6, 11, 12, 8, 13, 17, 31, 26, 27, 0 };
        var p = await TransactAsync(Proto.CmdQueryGestures, req, timeoutMs);
        if (p == null) return null;
        if (p.Length < 2 || p[0] != 0) return new GestureQuery(null, p);

        var entries = new List<GestureEntry>();
        int count = p[1];
        for (int i = 0; i < count && 2 + i * 4 + 4 <= p.Length; i++)
            entries.Add(new GestureEntry(p[2 + i * 4], p[4 + i * 4], p[5 + i * 4]));
        return new GestureQuery(entries, p);
    }

    public async Task<Ack> SetGesturesAsync(IReadOnlyList<GestureEntry> entries, int timeoutMs = 3000)
    {
        var payload = new List<byte> { (byte)entries.Count };
        foreach (var e in entries)
        {
            payload.Add((byte)e.Device);
            payload.Add(1);                    // button = touch sensor
            payload.Add((byte)e.Action);
            payload.Add((byte)e.Func);
        }
        return ToAck(await TransactAsync(Proto.CmdSetGestures, payload.ToArray(), timeoutMs));
    }

    /// <summary>
    /// Reads the current gestures, applies <paramref name="updates"/> on top and sends all six slots
    /// (same approach as the working Python tool; slots the earbuds don't report default to Play/Pause).
    /// </summary>
    public async Task<Ack> ApplyGesturesAsync(IReadOnlyDictionary<(int dev, int act), int> updates, int timeoutMs = 3000)
    {
        var q = await QueryGesturesAsync(timeoutMs);
        if (q?.Entries == null)
        {
            Log?.Invoke("Cannot read current gestures before applying changes.");
            return new Ack(false, -1);
        }

        var all = new List<GestureEntry>();
        foreach (int dev in new[] { 1, 2 })
            foreach (int act in new[] { 2, 3, 4 })
            {
                int func = q.Entries.FirstOrDefault(e => e.Device == dev && e.Action == act)?.Func ?? 1;
                if (updates.TryGetValue((dev, act), out int f)) func = f;
                all.Add(new GestureEntry(dev, act, func));
            }
        return await SetGesturesAsync(all, timeoutMs);
    }

    /// <summary>Reply layout (confirmed): [status][01][01][mode]. Returns the mode byte or null.</summary>
    public async Task<int?> GetNoiseModeAsync(int timeoutMs = 3000)
    {
        var p = await TransactAsync(Proto.CmdQueryAnc, new byte[] { 1, 1 }, timeoutMs);
        return p is { Length: >= 4 } && p[0] == 0 ? p[3] : null;
    }

    /// <summary>
    /// Live on/off state of features. Reply (0x810D): [status][N] then N * { id, value (1 byte) }.
    /// A feature the earbuds don't report stays null.
    /// </summary>
    public async Task<Dictionary<int, bool?>> QueryFeaturesAsync(IReadOnlyList<int> ids, int timeoutMs = 3000)
    {
        var result = ids.ToDictionary(i => i, _ => (bool?)null);
        if (ids.Count == 0) return result;

        var req = new byte[1 + ids.Count];
        req[0] = (byte)ids.Count;
        for (int i = 0; i < ids.Count; i++) req[1 + i] = (byte)ids[i];

        var p = await TransactAsync(Proto.CmdQueryFeatures, req, timeoutMs, Proto.RespFeatures);
        if (p == null || p.Length < 2 || p[0] != 0) return result;

        int n = p[1];
        if (p.Length < 2 + n * 2) return result;
        for (int i = 0; i < n; i++)
        {
            int id = p[2 + i * 2];
            byte value = p[3 + i * 2];
            if (result.ContainsKey(id)) result[id] = value != 0;
        }
        return result;
    }

    public async Task<Ack> SetAncAsync(int mode, int timeoutMs = 3000) =>
        ToAck(await TransactAsync(Proto.CmdSetAnc, new byte[] { 1, 1, (byte)mode }, timeoutMs));

    /// <summary>Any ack frame means the command was accepted (some firmware returns a non-zero status); verify with QueryFeaturesAsync.</summary>
    public async Task<Ack> SetFeatureAsync(int featureId, bool on, int timeoutMs = 3000) =>
        ToAck(await TransactAsync(Proto.CmdSetFeature, new byte[] { (byte)featureId, (byte)(on ? 1 : 0) }, timeoutMs));
}
