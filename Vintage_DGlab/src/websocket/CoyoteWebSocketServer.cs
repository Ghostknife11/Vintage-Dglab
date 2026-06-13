using System.Text;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using Vintagestory.API.Common;
using VintageDLab.Util;
using Timer = System.Timers.Timer;

namespace VintageDLab.WebSocket;

/// <summary>
/// DG-Lab Socket WebSocket server — ported from DGLab-Craft (bilbillm/DGLab-Craft).
/// Protocol: bind → heartbeat(60s) → clear → pulse waveform → strength
/// </summary>
public class CoyoteWebSocketServer
{
    private const string FIXED_CLIENT_ID = "1234-123456789-12345-12345-01";
    private const int HEARTBEAT_MS = 60000; // 60s per DGLab-Craft

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ILogger _logger;
    private readonly object _sendLock = new();

    // Connection state
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private bool _isBound;

    // Identity
    private string? _generatedClientId;
    private string? _appClientId;  // App's clientId after bind
    private string? _targetId;     // Our targetId = generatedClientId (sent to app as clientId)

    // App-reported max strength
    public int AppAMax { get; private set; } = 100;
    public int AppBMax { get; private set; } = 100;

    // Waveform state
    private string[]? _waveData;
    private int _waveTick;
    private const int WAVEFORM_TICKS = 5;
    private long _lastPulseSentMs;
    private const long PULSE_WINDOW_MS = 700;

    private string? _cachedHost;

    public bool IsRunning { get; private set; }
    public bool IsConnected => _isBound && _stream != null && _tcpClient is { Connected: true };
    public string ServerAddress => _cachedHost ??= ResolveHost();
    public int ServerPort { get; private set; }

    public CoyoteWebSocketServer(ILogger logger) { _logger = logger; }

    // ==================== Lifecycle ====================

    public bool Start(int port)
    {
        if (IsRunning) return true;
        ServerPort = port;
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsRunning = true;
            _ = AcceptLoopAsync(_cts.Token);
            _logger.Notification($"[Vintage-DGLab] WS server started on port {port}");
            return true;
        }
        catch (Exception ex) { _logger.Error($"[Vintage-DGLab] Start failed: {ex.Message}"); return false; }
    }

    public void Stop()
    {
        IsRunning = false;
        _cts?.Cancel();
        Disconnect();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private void Disconnect()
    {
        lock (_sendLock)
        {
            _isBound = false;
            _appClientId = null;
            _targetId = null;
            _generatedClientId = null;
            AppAMax = 100; AppBMax = 100;
            _waveData = null; _waveTick = 0;
            try { _stream?.Dispose(); } catch { }
            try { _tcpClient?.Dispose(); } catch { }
            _stream = null;
            _tcpClient = null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(tcp, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _logger.Warning($"[Vintage-DGLab] Accept: {ex.Message}"); }
        }
    }

    // ==================== Client Handler ====================

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var stream = tcp.GetStream();
        try
        {
            // Handshake
            var req = await ReadHttpRequest(stream, ct);
            if (req == null || !Handshake(stream, req)) { tcp.Dispose(); return; }

            // Reject if already bound
            if (_isBound)
            {
                SendRaw(stream, BuildJson("error", "", "", "400"));
                tcp.Dispose(); return;
            }

            // Accept client
            lock (_sendLock)
            {
                Disconnect();
                _tcpClient = tcp;
                _stream = stream;
                _generatedClientId = Guid.NewGuid().ToString();
                _targetId = _generatedClientId;
            }

            // Send bind request: server asks app to identify
            SendRaw(stream, BuildJson("bind", _generatedClientId, "", "targetId"));

            // Start heartbeat timer
            using var hbTimer = new Timer(HEARTBEAT_MS);
            hbTimer.Elapsed += (_, _) => SendHeartbeat();
            hbTimer.AutoReset = true;
            hbTimer.Start();

            // Receive loop
            var buf = new byte[4096];
            while (!ct.IsCancellationRequested && tcp.Connected)
            {
                var msg = await ReadWsFrame(stream, buf, ct);
                if (msg == null) break;
                HandleMessage(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { _logger.Debug($"[Vintage-DGLab] Client: {ex.Message}"); }
        finally
        {
            Disconnect();
            try { tcp.Dispose(); } catch { }
        }
    }

    // ==================== Send ====================

    /// <summary>
    /// Main pulse send — called every tick from HealthTracker.
    /// Alternates clear/waveform phases every WAVEFORM_TICKS.
    /// Always sends strength.
    /// </summary>
    public void SendPulse(int strengthA, int strengthB)
    {
        if (!IsConnected) return;

        _waveTick++;
        bool refreshWave = _waveData == null || _waveTick >= WAVEFORM_TICKS;
        if (refreshWave)
        {
            _waveData = WaveformGenerator.Generate().HexData;
            _waveTick = 0;
        }

        lock (_sendLock)
        {
            if (!IsConnected || _stream == null) return;

            var a = Math.Clamp(strengthA, 0, 200);
            var b = Math.Clamp(strengthB, 0, 200);

            if (refreshWave)
            {
                // DGLab-Craft protocol: clear → pulse waveform → strength
                SendMsg(_stream, "clear-1");
                SendMsg(_stream, "clear-2");
                SendPulseMsg(_stream, "A", _waveData!);
                SendPulseMsg(_stream, "B", _waveData!);
                _lastPulseSentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            // Always send strength
            SendMsg(_stream, $"strength-1+2+{a}");
            SendMsg(_stream, $"strength-2+2+{b}");
        }
    }

    public void SendStopPulse()
    {
        if (!IsConnected) return;
        lock (_sendLock)
        {
            if (!IsConnected || _stream == null) return;
            SendMsg(_stream, "clear-1");
            SendMsg(_stream, "clear-2");
            SendMsg(_stream, "strength-1+2+0");
            SendMsg(_stream, "strength-2+2+0");
        }
    }

    private void SendHeartbeat()
    {
        lock (_sendLock)
        {
            if (!IsConnected || _stream == null) return;
            SendRaw(_stream, BuildJson("heartbeat", FIXED_CLIENT_ID, _targetId ?? "", "200"));
        }
    }

    private void SendMsg(NetworkStream s, string message)
    {
        SendRaw(s, BuildJson("msg", FIXED_CLIENT_ID, _targetId ?? "", message));
    }

    private void SendPulseMsg(NetworkStream s, string channel, string[] hexData)
    {
        // Format: pulse-A:["HEX1","HEX2",...]
        // iOS requires uppercase 16-char hex, quoted
        var sb = new StringBuilder("pulse-").Append(channel).Append(":[");
        for (int i = 0; i < hexData.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var hex = hexData[i].ToUpperInvariant();
            while (hex.Length < 16) hex = "0" + hex;
            if (hex.Length > 16) hex = hex[..16];
            sb.Append('"').Append(hex).Append('"');
        }
        sb.Append(']');
        SendMsg(s, sb.ToString());
    }

    private static void SendRaw(NetworkStream s, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var len = data.Length;
        if (len < 126)
        {
            var frame = new byte[len + 2];
            frame[0] = 0x81; frame[1] = (byte)len;
            Buffer.BlockCopy(data, 0, frame, 2, len);
            s.Write(frame, 0, frame.Length);
        }
        else if (len <= 65535)
        {
            var frame = new byte[len + 4];
            frame[0] = 0x81; frame[1] = 126;
            frame[2] = (byte)(len >> 8); frame[3] = (byte)len;
            Buffer.BlockCopy(data, 0, frame, 4, len);
            s.Write(frame, 0, frame.Length);
        }
        else
        {
            var frame = new byte[len + 10];
            frame[0] = 0x81; frame[1] = 127;
            frame[6] = (byte)(len >> 24); frame[7] = (byte)(len >> 16);
            frame[8] = (byte)(len >> 8); frame[9] = (byte)len;
            Buffer.BlockCopy(data, 0, frame, 10, len);
            s.Write(frame, 0, frame.Length);
        }
        s.Flush();
    }

    // ==================== Receive ====================

    private void HandleMessage(string text)
    {
        try
        {
            var json = SimpleJson.Parse(text);
            if (json == null) return;
            json.TryGetValue("type", out var type);
            json.TryGetValue("message", out var msg);
            json.TryGetValue("clientId", out var cId);

            switch (type)
            {
                case "bind":
                {
                    json.TryGetValue("targetId", out var tId);
                    // DG-Lab Socket protocol: msg=="DGLAB", targetId==generatedClientId
                    if (msg == "DGLAB"
                        && tId == _generatedClientId)
                    {
                        _appClientId = cId;
                        _isBound = true;
                        lock (_sendLock)
                        {
                            if (_stream != null)
                                SendRaw(_stream, BuildBindConfirm(FIXED_CLIENT_ID, _appClientId));
                        }
                        _logger.Notification($"[Vintage-DGLab] Bound: {_appClientId}");
                    }
                    break;
                }
                case "heartbeat":
                {
                    lock (_sendLock)
                    {
                        if (_stream != null)
                            SendRaw(_stream, BuildJson("heartbeat", FIXED_CLIENT_ID, _targetId ?? "", "200"));
                    }
                    break;
                }
                case "msg":
                {
                    if (msg.StartsWith("strength-"))
                    {
                        ParseAppStrength(msg);
                    }
                    break;
                }
            }
        }
        catch (Exception ex) { _logger.Debug($"[Vintage-DGLab] Parse: {ex.Message}"); }
    }

    private void ParseAppStrength(string msg)
    {
        // Format from app: strength-0+0+<maxA>+<maxB> (dual channel)
        // Or legacy: strength-<ch>+<mode>+<value>
        var parts = msg[9..].Split('+');
        if (parts.Length >= 4)
        {
            if (int.TryParse(parts[2], out var ma)) AppAMax = Math.Clamp(ma, 1, 200);
            if (int.TryParse(parts[3], out var mb)) AppBMax = Math.Clamp(mb, 1, 200);
        }
        else if (parts.Length >= 3)
        {
            if (int.TryParse(parts[0], out var ch) && int.TryParse(parts[2], out var val))
            {
                if (ch == 1) AppAMax = Math.Clamp(val, 1, 200);
                else if (ch == 2) AppBMax = Math.Clamp(val, 1, 200);
            }
        }
    }

    // ==================== WebSocket Protocol ====================

    private static async Task<string?> ReadHttpRequest(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        int crlf = 0;
        while (!ct.IsCancellationRequested)
        {
            if (await s.ReadAsync(buf, 0, 1, ct) == 0) return null;
            var c = (char)buf[0]; sb.Append(c);
            if (c == '\r' || c == '\n') { crlf++; if (crlf >= 4) break; }
            else crlf = 0;
        }
        return sb.ToString();
    }

    private static bool Handshake(NetworkStream s, string request)
    {
        var m = Regex.Match(request, "Sec-WebSocket-Key:\\s*(.+)\\s*", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var key = m.Groups[1].Value.Trim();
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var resp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        var data = Encoding.ASCII.GetBytes(resp);
        s.Write(data, 0, data.Length);
        s.Flush();
        return true;
    }

    private static async Task<string?> ReadWsFrame(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        if (await ReadExact(s, buf, 2, ct) < 2) return null;
        int opcode = buf[0] & 0x0F;
        if (opcode == 0x08) return null;
        if (opcode == 0x09) { buf[0] = 0x8A; await s.WriteAsync(buf, 0, 2, ct); return await ReadWsFrame(s, buf, ct); }

        bool masked = (buf[1] & 0x80) != 0;
        int plen = buf[1] & 0x7F;
        if (plen == 126) { await ReadExact(s, buf, 2, ct); plen = (buf[0] << 8) | buf[1]; }
        else if (plen == 127) { await ReadExact(s, buf, 8, ct); plen = (int)(((long)buf[0] << 56) | ((long)buf[1] << 48) | ((long)buf[2] << 40) | ((long)buf[3] << 32) | ((long)buf[4] << 24) | ((long)buf[5] << 16) | ((long)buf[6] << 8) | buf[7]); }

        byte[] mk = new byte[4];
        if (masked) await ReadExact(s, mk, 4, ct);

        var pay = new byte[plen];
        await ReadExact(s, pay, plen, ct);
        if (masked) for (int i = 0; i < plen; i++) pay[i] ^= mk[i % 4];
        return Encoding.UTF8.GetString(pay);
    }

    private static async Task<int> ReadExact(NetworkStream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count) { int r = await s.ReadAsync(buf, total, count - total, ct); if (r == 0) return total; total += r; }
        return total;
    }

    // ==================== JSON Builders ====================

    private static string BuildJson(string type, string clientId, string targetId, string message)
        => $"{{\"type\":\"{Esc(type)}\",\"clientId\":\"{Esc(clientId)}\",\"targetId\":\"{Esc(targetId)}\",\"message\":\"{Esc(message)}\"}}";

    private static string BuildBindConfirm(string clientId, string targetId)
        => $"{{\"type\":\"bind\",\"clientId\":\"{Esc(clientId)}\",\"targetId\":\"{Esc(targetId)}\",\"message\":\"200\",\"statusCode\":200}}";

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ==================== QR URL ====================

    public string GetQrCodeUrl()
        => $"https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#ws://{ServerAddress}:{ServerPort}/{FIXED_CLIENT_ID}";

    private string ResolveHost()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        if (ip.StartsWith("10.") || ip.StartsWith("192.168.")
                            || (ip.StartsWith("172.") && int.Parse(ip.Split('.')[1]) is >= 16 and <= 31))
                            return ip;
                    }
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }
}
