// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VintageDLab;

public sealed record DeviceView(string Slot, string Name);
public sealed record PhoneState(bool Connected, bool Ready, int? LimitA, int? LimitB, int? ActualA, int? ActualB,
    string Status, string? Qr, string? Slot, DeviceView[] Devices, bool MutedA = false, bool MutedB = false);

public sealed class DeviceLink : IAsyncDisposable
{
    readonly LinkSettings settings;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim writer = new(1, 1);
    readonly object sync = new();
    readonly Dictionary<string, JsonObject> devices = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    WebSocket? socket;
    TcpListener? listener;
    Task? readerTask, heartbeatTask;
    string target = Guid.NewGuid().ToString(), peer = "", selected = "", status = "未连接";
    string? qr;
    bool bound, legacyReport;
    int? legacyA, legacyB, legacyMaxA, legacyMaxB;
    long pongAt = Environment.TickCount64;
    public string Session { get; } = Guid.NewGuid().ToString();
    public DeviceLink(LinkSettings settings) { settings.Validate(); this.settings = settings.Copy(); }
    public PhoneState Snapshot
    {
        get { lock (sync) {
            if (settings.Mode == "lan") return new(bound, bound && legacyReport, legacyMaxA, legacyMaxB, legacyA, legacyB, status, qr,
                bound ? "legacy" : null, bound ? [new("legacy", "郊狼 3.0 · 局域网")] : []);
            JsonObject? device = devices.GetValueOrDefault(selected);
            var p = device?["props"] as JsonObject;
            var slot = device?["slotState"] as JsonObject;
            bool ready = bound && Text(p?["connectState"]) == "connected" && Text(slot?["hasDevice"]) != "false";
            // V4 caps and mute belong to slotState; props contains measured intensity only.
            int? a = Number(slot?["channelA"]?["intensityMax"]), b = Number(slot?["channelB"]?["intensityMax"]);
            bool muteA = Text(slot?["channelA"]?["isMuted"]) == "true", muteB = Text(slot?["channelB"]?["isMuted"]) == "true";
            string currentStatus = !bound || device is null ? status : !ready ? "手机已配对，等待设备蓝牙连接"
                : a is null || b is null ? "设备已连接，等待手机通道上限" : "设备已就绪（手机上限已同步）";
            return new(bound, ready, a, b, Number(p?["intensityA"]), Number(p?["intensityB"]), currentStatus, qr,
                device is null ? null : selected, devices.Values.Select(d => new DeviceView(Text(d["slotId"]), Text(d["name"]))).ToArray(), muteA, muteB);
        }}
    }
    public void Select(string slot) { lock(sync) { if (!devices.ContainsKey(slot)) throw new ArgumentException("设备已断开"); selected = slot; } }
    internal static int? Number(JsonNode? n) => n is JsonValue value && value.TryGetValue<double>(out double number)
        && double.IsFinite(number) && number is >= 0 and <= 200 ? (int)Math.Floor(number) : null;
    static string Text(JsonNode? n) => n?.ToString() ?? "";
    void SetStatus(string text) { lock(sync) status = text; }

    public async Task StartAsync()
    {
        if (settings.Mode == "v4") {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            socket = ws;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(10000);
            await ws.ConnectAsync(new Uri(settings.Relay.TrimEnd('/')), timeout.Token);
            pongAt=Environment.TickCount64;
            SetStatus("服务器已连接，正在生成二维码");
            readerTask = ReadLoopAsync(ws);
            heartbeatTask = HeartbeatAsync();
        } else {
            string host = settings.LanAddress;
            if (string.IsNullOrWhiteSpace(host)) host = LanAddresses().FirstOrDefault() ?? "127.0.0.1";
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("请选择本机 IPv4 地址");
            listener = new TcpListener(ip, settings.WebSocketPort);
            listener.Start();
            lock(sync) { qr = $"https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#ws://{host}:{settings.WebSocketPort}/{target}"; status = "等待手机扫码（同一局域网）"; }
            readerTask = AcceptAsync();
            heartbeatTask = HeartbeatAsync();
        }
    }
    public static string[] LanAddresses() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()).Distinct().ToArray();

    async Task AcceptAsync()
    {
        try { while (!lifetime.IsCancellationRequested && listener != null) {
            var tcp = await listener.AcceptTcpClientAsync(lifetime.Token);
            _ = HandleAcceptedAsync(tcp);
        }} catch (OperationCanceledException) {} catch (Exception e) { if(!lifetime.IsCancellationRequested) SetStatus("监听失败：" + e.Message); }
    }
    async Task HandleAcceptedAsync(TcpClient tcp)
    {
        WebSocket? accepted = null;
        try {
            var stream = tcp.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(10000);
            var bytes = new List<byte>(); byte[] one = new byte[1];
            while (bytes.Count < 8192) {
                if (await stream.ReadAsync(one, timeout.Token) == 0) return;
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            string request = Encoding.ASCII.GetString(bytes.ToArray());
            var lines = request.Split("\r\n");
            if (!lines[0].StartsWith("GET /" + target + " ", StringComparison.Ordinal)) return;
            var headers = lines.Skip(1).Where(l=>l.Contains(':')).Select(l=>l.Split(':',2)).ToDictionary(x=>x[0].Trim(), x=>x[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || !headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket",StringComparison.OrdinalIgnoreCase)) return;
            if (Convert.FromBase64String(key).Length != 16) return;
            lock(sync) { if(socket != null) return; }
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            accepted = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
            lock(sync) { if(socket != null) return; socket = accepted; }
            await SendAsync(new {type="bind",clientId=target,targetId="",message="targetId"}, lifetime.Token);
            await ReadLoopAsync(accepted);
        } catch(Exception e) { if(!lifetime.IsCancellationRequested) SetStatus("兼容连接：" + e.Message); }
        finally { accepted?.Dispose(); tcp.Dispose(); }
    }
    async Task ReadLoopAsync(WebSocket ws)
    {
        try {
            while (ws.State == WebSocketState.Open && !lifetime.IsCancellationRequested) {
                byte[] buffer = new byte[4096]; using var message = new MemoryStream(); WebSocketReceiveResult result;
                do {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536) throw new InvalidDataException("消息类型或长度无效");
                    message.Write(buffer,0,result.Count);
                } while(!result.EndOfMessage);
                var json = JsonNode.Parse(message.ToArray()) as JsonObject;
                if(json != null) await HandleAsync(json);
            }
        } catch(OperationCanceledException) {} catch(Exception e) { if(!lifetime.IsCancellationRequested) SetStatus("连接中断："+e.Message); }
        finally {
            lock(sync) { if(ReferenceEquals(socket,ws)) {
                bound=false; devices.Clear(); selected=""; legacyReport=false; legacyMaxA=legacyMaxB=legacyA=legacyB=null; socket=null;
                if(settings.Mode=="v4") qr=null;
                status="手机已断开，请重新连接";
            }}
            foreach(var p in pending.Values) p.TrySetException(new IOException("手机已断开")); pending.Clear();
        }
    }
    async Task HandleAsync(JsonObject frame)
    {
        string type=Text(frame["type"]);
        if(settings.Mode=="lan") {
            if(type=="bind" && Text(frame["message"])=="DGLAB" && Text(frame["targetId"])==target && Text(frame["clientId"]).Length>0) {
                lock(sync){peer=Text(frame["clientId"]);bound=true;status="手机已配对，等待上限报告";}
                await SendAsync(new{type="bind",clientId=target,targetId=peer,message="200"},lifetime.Token);
            } else if(type=="msg" && bound && Text(frame["clientId"])==peer) {
                string text=Text(frame["message"]);
                if(text.StartsWith("strength-")) {
                    var p=text[9..].Split('+');
                    if(p.Length==4 && p.All(x=>int.TryParse(x,out int n)&&n is >=0 and <=200)) lock(sync) {
                        legacyA=int.Parse(p[0]);legacyB=int.Parse(p[1]);legacyMaxA=int.Parse(p[2]);legacyMaxB=int.Parse(p[3]);legacyReport=true;status="局域网已就绪";
                    }
                }
            }
            return;
        }
        if(type=="hello") {
            target=Text(frame["clientId"]); if(target.Length==0)throw new InvalidDataException("缺少连接标识");
            string url=settings.Relay.TrimEnd('/') + (settings.Relay.Contains('?') ? "&" : "?") + "tid=" + Uri.EscapeDataString(target);
            lock(sync){qr="https://dungeon-lab.cn/s/?v=1&action=socket&url="+Uri.EscapeDataString(url);status="等待 DG-LAB 4 扫码";}
        } else if(type=="pong") pongAt=Environment.TickCount64;
        else if(type=="client_attached") {
            lock(sync){if(bound && peer!=Text(frame["clientId"]))return;peer=Text(frame["clientId"]);bound=true;status="手机已连接，等待设备";}
            _ = RequestDevicesAsync();
        } else if(type=="client_disconnected" && Text(frame["clientId"])==peer) {
            lock(sync){bound=false;devices.Clear();selected="";status="手机已断开，请重新扫码";}
        } else if(type=="message" && Text(frame["clientId"])==peer && frame["data"] is JsonObject data) {
            if(Text(data["t"])=="resp") {
                string id=Text(data["requestId"]??data["reqId"]);
                if(pending.TryRemove(id,out var p)) {
                    if(data["error"]!=null)p.TrySetException(new IOException(data["error"]!.ToJsonString())); else p.TrySetResult(data["result"]?.DeepClone());
                }
                if(data["result"]?["devices"] is JsonArray list) SnapshotDevices(list);
            } else if(Text(data["ev"])=="devices.snapshot" && data["devices"] is JsonArray list) SnapshotDevices(list);
            else if(Text(data["ev"])=="devices.patch") {
                lock(sync){foreach(var d in data["added"] as JsonArray??[])Upsert(d);foreach(var d in data["removed"] as JsonArray??[])devices.Remove(Text(d));Choose();}
            } else if(Text(data["ev"])=="slots.patch") {
                lock(sync){foreach(var patch in data["slots"] as JsonArray??[]) {
                    if(patch is JsonObject p && devices.TryGetValue(Text(p["slotId"]),out var old))Merge(old,p);
                }Choose();}
            }
        } else if(type is "error" or "idle_timeout") throw new IOException(Text(frame["message"]??frame["code"]));
    }
    async Task RequestDevicesAsync(){try{await RpcAsync("devices.get",null,lifetime.Token);}catch(Exception e){SetStatus("读取设备失败："+e.Message);}}
    void SnapshotDevices(JsonArray list){lock(sync){devices.Clear();foreach(var d in list)Upsert(d);Choose();}}
    void Upsert(JsonNode? d){if(d is JsonObject o && Text(o["type"])=="COYOTE_030" && Text(o["slotId"]).Length>0)devices[Text(o["slotId"])]=(JsonObject)o.DeepClone();}
    void Choose(){if(!devices.ContainsKey(selected))selected=devices.Count==1?devices.Keys.First():"";status=devices.Count==0?"等待手机连接郊狼 3.0":selected==""?"请选择一个设备":"设备状态已更新";}
    internal static void Merge(JsonObject old,JsonObject patch){foreach(var p in patch){if(p.Value is JsonObject o && old[p.Key] is JsonObject d)Merge(d,o);else old[p.Key]=p.Value?.DeepClone();}}
    async Task HeartbeatAsync(){try{while(!lifetime.IsCancellationRequested){await Task.Delay(settings.Mode=="v4"?2000:30000,lifetime.Token);if(settings.Mode=="v4") {
        if(Environment.TickCount64-pongAt>10000)throw new IOException("中继心跳超时");await SendAsync(new{type="ping"},lifetime.Token);
    }else if(bound)await LegacyAsync("heartbeat","200",lifetime.Token);}}catch(OperationCanceledException){}catch(Exception e){SetStatus(e.Message);lock(sync){bound=false;legacyReport=false;devices.Clear();qr=null;}socket?.Abort();}}
    async Task SendAsync(object value,CancellationToken token){await writer.WaitAsync(token);try{
        var ws=socket??throw new IOException("尚未连接");if(ws.State!=WebSocketState.Open)throw new IOException("连接不可用");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(3000);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value),WebSocketMessageType.Text,true,timeout.Token);
    }finally{writer.Release();}}
    Task LegacyAsync(string type,string message,CancellationToken token)=>SendAsync(new{type,clientId=target,targetId=peer,message},token);
    async Task<JsonNode?> RpcAsync(string method,object? payload,CancellationToken token){
        string id=Guid.NewGuid().ToString("N");var tcs=new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=tcs;
        try{await SendAsync(new{type="message",clientId=peer,data=new{t="req",reqId=id,requestId=id,m=method,data=payload}},token);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5),token);
        }finally{pending.TryRemove(id,out _);}
    }
    public async Task WindowAsync(int a,int b,bool useA,bool useB,string wave,int ms,int offset,CancellationToken token)
    {
        var view=Snapshot;if(!view.Ready)throw new IOException("设备尚未就绪");
        useA &= !view.MutedA; useB &= !view.MutedB;
        if((useA&&(view.LimitA is null||a>view.LimitA))||(useB&&(view.LimitB is null||b>view.LimitB)))throw new IOException("手机上限已改变或未知");
        var frames=Waves.Frames(wave,ms,offset);
        if(settings.Mode=="lan") {
            if(useA){await LegacyAsync("msg",$"strength-1+2+{a}",token);await LegacyAsync("msg","pulse-A:"+JsonSerializer.Serialize(frames),token);}
            if(useB){await LegacyAsync("msg",$"strength-2+2+{b}",token);await LegacyAsync("msg","pulse-B:"+JsonSerializer.Serialize(frames),token);}
        } else {
            async Task Channel(int c,int n){
                await RpcAsync("device.op.clear",new{s=view.Slot,c},token);
                await RpcAsync("device.op",new{s=view.Slot,c,im=true,t=7,v=0},token);
                await Task.WhenAll(RpcAsync("device.op",new{s=view.Slot,c,im=true,t=4,v=n,d=ms},token),
                    RpcAsync("device.op",new{s=view.Slot,c,im=true,t=0,v=frames,d=ms,ver=3},token));
            }
            await Task.WhenAll(useA?Channel(0,a):Task.CompletedTask,useB?Channel(1,b):Task.CompletedTask);
        }
    }
    public async Task StopOutputAsync(CancellationToken token=default)
    {
        if(!Snapshot.Connected)return;
        if(settings.Mode=="lan"){
            await LegacyAsync("msg","strength-1+2+0",token);await LegacyAsync("msg","strength-2+2+0",token);
            await LegacyAsync("msg","clear-1",token);await LegacyAsync("msg","clear-2",token);
        }else{
            var slot=Snapshot.Slot;if(slot==null)return;
            await Task.WhenAll(Enumerable.Range(0,2).Select(async c=>{await RpcAsync("device.op.clear",new{s=slot,c},token);await RpcAsync("device.op",new{s=slot,c,im=true,t=7,v=0},token);}));
        }
    }
    public async ValueTask DisposeAsync(){
        using var timeout=new CancellationTokenSource(1500);try{await StopOutputAsync(timeout.Token);}catch{}
        lifetime.Cancel();listener?.Stop();socket?.Abort();
        if(readerTask!=null)try{await readerTask.WaitAsync(TimeSpan.FromSeconds(2));}catch{}
        socket?.Dispose();lifetime.Dispose();
    }
}
