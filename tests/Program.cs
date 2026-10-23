// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VintageDLab;

int passed=0;
void Check(bool result,string name){if(!result)throw new Exception("FAIL: "+name);Console.WriteLine("PASS "+name);passed++;}
async Task Until(Func<bool> f){for(int i=0;i<160;i++){if(f())return;await Task.Delay(50);}throw new TimeoutException("Condition not met");}
var cfg=new LinkSettings();var model=new HealthModel();
Check(!model.Step(15,15,true,cfg)&&model.Signal==0,"initial baseline produces no output");
model.Step(14,15,true,cfg);Check(model.Signal==1&&model.Chain==1,"first damage");
model.Step(13,15,true,cfg);Check(model.Signal==2.5&&model.Chain==2,"chain bonus preserves fractional signal");
model.Step(13.1,15,true,cfg);Check(Math.Abs(model.Signal-2.4)<.00001&&model.Chain==0,"small heal is not rounded to a whole point");
double s=model.Signal;model.Step(13.1,15,true,cfg);Check(model.Signal==s,"stable health holds signal");
Check(model.Step(0,15,false,cfg)&&model.Signal==0,"death is one transition");
Check(!model.Step(0,15,false,cfg),"dead state never retriggers death");
model.Step(15,15,true,cfg);Check(model.Signal==0&&model.Chain==0,"respawn resets baseline");
Check(HealthModel.Target(100,20,100)==20&&HealthModel.Target(60,20,100)==12,"A/B percentages use independent phone limits");
Check(HealthModel.Target(0,100,100)==0,"zero limit stays zero");
Check(HealthModel.Target(33,20,100)==6,"target rounds down");
try{HealthModel.Target(null,20,100);Check(false,"unknown limits");}catch(InvalidOperationException){Check(true,"unknown limit rejected");}
Check(DeviceLink.Number(JsonNode.Parse("33.9"))==33,"fractional phone cap rounds down");
Check(new[]{"null","true","\"100\"","-1","201"}.All(json=>DeviceLink.Number(JsonNode.Parse(json))==null),"invalid phone caps are never guessed");
foreach(string wave in new[]{"bubble","steady","breathe"}){
 var frames=Waves.Frames(wave,1000);Check(frames.Length==10&&frames.All(f=>f.Length==16&&f.StartsWith("2D2D2D2D")&&Enumerable.Range(0,4).All(i=>Convert.ToInt32(f.Substring(8+i*2,2),16)<=100)),"wave frame layout "+wave);
}
var invalid=cfg.Copy();invalid.DeathDurationMs=123;try{invalid.Validate();Check(false,"invalid duration");}catch(ArgumentException){Check(true,"duration step validation");}

// Actual managed WebSocket I/O: fragmentation, identity binding and zero limits.
int port;using(var temp=new TcpListener(IPAddress.Loopback,0)){temp.Start();port=((IPEndPoint)temp.LocalEndpoint).Port;}
var lanCfg=new LinkSettings{Mode="lan",LanAddress="127.0.0.1",WebSocketPort=port};
await using(var link=new DeviceLink(lanCfg)){
 await link.StartAsync();string qr=link.Snapshot.Qr!;string wsUrl=qr[(qr.IndexOf("#ws://",StringComparison.Ordinal)+1)..];
 using var app=new ClientWebSocket();await app.ConnectAsync(new Uri(wsUrl),CancellationToken.None);
 var bind=await Receive(app);string id=bind["clientId"]!.ToString();
 await Send(app,new{type="bind",clientId="test-phone",targetId=id,message="DGLAB"});await Receive(app);
 Check(link.Snapshot.LimitA==null&&!link.Snapshot.Ready,"LAN pair does not invent default limits");
 var report=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type="msg",clientId="test-phone",targetId=id,message="strength-8+4+0+60"}));
 await app.SendAsync(report.AsMemory(0,10),WebSocketMessageType.Text,false,CancellationToken.None);
 await app.SendAsync(report.AsMemory(10),WebSocketMessageType.Text,true,CancellationToken.None);
 await Until(()=>link.Snapshot.Ready);Check(link.Snapshot.LimitA==0&&link.Snapshot.LimitB==60,"fragmented LAN report and zero cap");
 await link.WindowAsync(0,12,true,true,"bubble",500,0,CancellationToken.None);
 var messages=new List<string>();for(int i=0;i<4;i++)messages.Add((await Receive(app))["message"]!.ToString());
 Check(messages.Contains("strength-1+2+0")&&messages.Contains("strength-2+2+12"),"LAN actual numeric targets");
 Check(messages.Any(m=>m.StartsWith("pulse-B:[\"2D2D2D2D")),"LAN pulse frame payload");
 await link.StopOutputAsync();messages.Clear();for(int i=0;i<4;i++)messages.Add((await Receive(app))["message"]!.ToString());
 Check(messages.Contains("clear-1")&&messages.Contains("clear-2")&&messages.Contains("strength-2+2+0"),"LAN stop clears both channels and zeroes strength");
}

// Simulated DG-LAB 4 app over the real RPC envelope.
await using(var phone=new MockV4()){
 await using var controller=new LinkController(new LinkSettings{Relay=phone.Url,DeathDurationMs=300});
 await controller.ConnectAsync();await Until(()=>controller.Phone.Ready&&controller.Enabled);
 Check(controller.Phone.LimitA==100&&controller.Phone.LimitB==60,"V4 device snapshot caps");
 Check(controller.Phone.Qr!.StartsWith("https://dungeon-lab.cn/s/?v=1&action=socket&url="),"V4 official QR prefix");
 await Task.Delay(150);Check(!phone.Commands.Any(c=>c["t"]?.ToString()=="4"),"auto-enable never sends stimulation on connection");
 controller.Sample(15,15,true,false,true);controller.Sample(14,15,true,false,true);
 await Until(()=>phone.Commands.Any(c=>c["t"]?.ToString()=="4"));
 Check(phone.Commands.Any(c=>c["t"]?.ToString()=="4"&&c["v"]?.ToString()=="6"),"V4 damage maps to numeric cap percentage");
 controller.Stop();await Task.Delay(200);Check(!controller.Enabled,"manual stop stays disabled");
 controller.Test(20,300);await Until(()=>phone.Commands.Any(c=>c["t"]?.ToString()=="4"&&c["v"]?.ToString()=="20"));
 Check(phone.Commands.Any(c=>c["t"]?.ToString()=="4"&&c["c"]?.ToString()=="1"&&c["v"]?.ToString()=="12"),"V4 manual A20/B12 without game tracking");
 controller.Sample(0,0,false,true,true);await Task.Delay(700);
 Check(!controller.Enabled&&controller.Message.Contains("归零"),"paused game does not cancel manual expiry cleanup");
 Check(phone.Commands.Any(c=>c["t"]?.ToString()=="7"&&c["v"]?.ToString()=="0"),"V4 stop resets base strength");
 int count=phone.Commands.Count(c=>c["t"]?.ToString()=="4");await Task.Delay(600);
 Check(phone.Commands.Count(c=>c["t"]?.ToString()=="4")==count,"expired manual feedback never replays");
 Check(phone.Commands.Where(c=>c["t"]?.ToString()=="4").All(c=>int.Parse(c["d"]!.ToString())<=600),"V4 temporary strength tasks are bounded");
 await phone.Patch(new{channelA=new{intensityMax=33.9}});await Until(()=>controller.Phone.LimitA==33);
 Check(controller.Phone.LimitB==60,"partial slot patch preserves other channel cap");
 int from=phone.Commands.Count;controller.Test(20,300);
 await Until(()=>phone.Commands.Skip(from).Any(c=>c["t"]?.ToString()=="4"&&c["c"]?.ToString()=="0"&&c["v"]?.ToString()=="6"));
 Check(true,"updated phone cap changes actual numeric output");await Task.Delay(700);
 await phone.Patch(new{channelA=new{isMuted=true}});await Until(()=>controller.Phone.MutedA);
 Check(controller.Phone.LimitA==33,"phone mute preserves the reported cap");
 from=phone.Commands.Count;controller.Test(20,300);await Task.Delay(750);
 Check(!phone.Commands.Skip(from).Any(c=>c["t"]?.ToString()=="4"&&c["c"]?.ToString()=="0")&&phone.Commands.Skip(from).Any(c=>c["t"]?.ToString()=="4"&&c["c"]?.ToString()=="1"),"phone-muted A is skipped while B remains usable");
 await phone.Patch(new{channelA=new{intensityMax=(int?)null,isMuted=false}});await Until(()=>controller.Phone.LimitA==null);
 try{controller.Test(20);Check(false,"missing V4 cap");}catch(InvalidOperationException e){Check(e.Message.Contains("A 通道上限"),"missing V4 cap reports the actual missing channel");}
 await phone.Patch(new{channelA=new{intensityMax=100,isMuted=false}});await Until(()=>controller.Phone.LimitA==100);
 controller.SetEnabled(true);controller.Sample(15,15,true,false,true);from=phone.Commands.Count;controller.Sample(0,15,false,false,true);
 await Until(()=>phone.Commands.Skip(from).Any(c=>c["t"]?.ToString()=="4"&&c["v"]?.ToString()=="100"));await Task.Delay(800);
 Check(controller.Message.Contains("等待复活"),"death feedback expires and waits for respawn");
 count=phone.Commands.Count(c=>c["t"]?.ToString()=="4");controller.Sample(0,15,false,false,true);await Task.Delay(650);
 Check(phone.Commands.Count(c=>c["t"]?.ToString()=="4")==count,"remaining dead never replays death feedback");
}
Console.WriteLine($"ALL {passed} CHECKS PASSED");

static async Task<JsonObject> Receive(WebSocket ws){byte[] buf=new byte[16384];using var memory=new MemoryStream();WebSocketReceiveResult r;do{r=await ws.ReceiveAsync(buf,CancellationToken.None);memory.Write(buf,0,r.Count);}while(!r.EndOfMessage);return(JsonObject)JsonNode.Parse(memory.ToArray())!;}
static Task Send(WebSocket ws,object value)=>ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)),WebSocketMessageType.Text,true,CancellationToken.None);

sealed class MockV4:IAsyncDisposable
{
 readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource cts=new();WebSocket? ws;readonly Task run;
 readonly SemaphoreSlim sender=new(1,1);
 public ConcurrentQueue<JsonObject> Commands {get;}=new();public string Url{get;}
 public MockV4(){listener.Start();Url=$"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v4";run=Run();}
 async Task Send(object o){await sender.WaitAsync(cts.Token);try{await ws!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o),WebSocketMessageType.Text,true,cts.Token);}finally{sender.Release();}}
 public Task Patch(object slotState)=>Send(new{type="message",clientId="phone-test",data=new{t="ev",ev="slots.patch",slots=new[]{new{slotId="coyote-test",slotState}}}});
 async Task Run(){try{using var tcp=await listener.AcceptTcpClientAsync(cts.Token);var stream=tcp.GetStream();var raw=new List<byte>();byte[] one=new byte[1];while(true){await stream.ReadExactlyAsync(one,cts.Token);raw.Add(one[0]);if(raw.Count>4&&Encoding.ASCII.GetString(raw.TakeLast(4).ToArray())=="\r\n\r\n")break;}
 var headers=Encoding.ASCII.GetString(raw.ToArray());string key=headers.Split("\r\n").First(l=>l.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase)).Split(':',2)[1].Trim();string accept=Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
 await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"),cts.Token);
 ws=WebSocket.CreateFromStream(stream,true,null,TimeSpan.FromSeconds(30));await Send(new{type="hello",clientId="controller-test"});await Send(new{type="client_attached",clientId="phone-test"});
 while(!cts.IsCancellationRequested){byte[] b=new byte[20000];using var m=new MemoryStream();WebSocketReceiveResult r;do{r=await ws.ReceiveAsync(b,cts.Token);if(r.MessageType==WebSocketMessageType.Close)return;m.Write(b,0,r.Count);}while(!r.EndOfMessage);
 var frame=(JsonObject)JsonNode.Parse(m.ToArray())!;if(frame["type"]?.ToString()=="ping"){await Send(new{type="pong"});continue;}
 var data=frame["data"]!.AsObject();var method=data["m"]!.ToString();if(method=="device.op")Commands.Enqueue((JsonObject)data["data"]!.DeepClone());
 object result=method=="devices.get"?new{devices=new[]{new{slotId="coyote-test",type="COYOTE_030",name="Test Coyote",props=new{connectState="connected",intensityA=0,intensityB=0},slotState=new{hasDevice=true,channelA=new{intensityMax=100,isMuted=false},channelB=new{intensityMax=60,isMuted=false}}}}}:new{};
 await Send(new{type="message",clientId="phone-test",data=new{t="resp",requestId=data["requestId"]!.ToString(),result}});
 }
 }catch(OperationCanceledException){}catch(WebSocketException){}catch(IOException){}}
 public async ValueTask DisposeAsync(){cts.Cancel();ws?.Abort();listener.Stop();try{await run;}catch(OperationCanceledException){}cts.Dispose();}
}
