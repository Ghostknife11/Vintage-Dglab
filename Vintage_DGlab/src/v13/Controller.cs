// SPDX-License-Identifier: GPL-3.0-only
namespace VintageDLab;

public sealed class LinkController : IAsyncDisposable
{
    readonly object sync=new();
    readonly CancellationTokenSource lifetime=new();
    readonly SemaphoreSlim connection=new(1,1);
    CancellationTokenSource output=new();
    readonly Task loop;
    DeviceLink? link;
    LinkSettings settings;
    readonly HealthModel health=new();
    bool enabled,valid,paused,clearRequested;
    string readyKey="",message="打开连接页，生成二维码后扫码";
    long manualUntil,deathUntil,lastSent;
    int manualPercent,offset;
    public LinkController(LinkSettings settings){settings.Validate();this.settings=settings.Copy();loop=RunAsync();}
    public LinkSettings Settings{get{lock(sync)return settings.Copy();}}
    public PhoneState Phone=>link?.Snapshot??new(false,false,null,null,null,null,"未连接",null,null,[]);
    public bool Enabled{get{lock(sync)return enabled;}}
    public string Message{get{lock(sync)return message;}}
    public (double Health,double Maximum,double Signal,int Chain,string Trend) Health{get{lock(sync)return(health.Health,health.Maximum,health.Signal,health.Chain,health.Trend);}}
    public void Notify(string text){lock(sync)message=text;}
    public async Task ConnectAsync(){
        await connection.WaitAsync(lifetime.Token);
        try{
            Stop("正在连接服务器，请稍候…");var old=link;link=null;if(old!=null)await old.DisposeAsync();
            var next=new DeviceLink(Settings);link=next;readyKey="";
            try{await next.StartAsync();Notify("二维码会在服务器就绪后自动显示");}catch(Exception e){await next.DisposeAsync();if(link==next)link=null;Notify("连接失败："+e.Message);}
        }finally{connection.Release();}
    }
    public async Task DisconnectAsync(){await connection.WaitAsync();try{Stop("手机连接已关闭");var old=link;link=null;if(old!=null)await old.DisposeAsync();}finally{connection.Release();}}
    public void SelectDevice(string slot){Stop("已切换设备，等待状态报告");link?.Select(slot);readyKey="";}
    public void Apply(LinkSettings value){value.Validate();Stop("参数已保存，点击启用联动或进行测试");lock(sync)settings=value.Copy();}
    void CancelOutput(){output.Cancel();output=new();clearRequested=true;lastSent=0;}
    void UpdateReadiness(DeviceLink? active,PhoneState phone,LinkSettings cfg){
        string key=active!=null&&Usable(phone,cfg)?active.Session+"/"+phone.Slot:"";
        if(key==readyKey)return;
        readyKey=key;CancelOutput();health.Reset();manualUntil=deathUntil=0;
        enabled=key!=""&&cfg.AutoEnable;message=enabled?"设备就绪，已自动启用联动；连接本身不会输出":"等待设备和手机上限";
    }
    public void SetEnabled(bool value){lock(sync){
        UpdateReadiness(link,Phone,settings);
        if(value&&!Usable(Phone,settings))throw new InvalidOperationException(NotReadyReason(Phone,settings));
        enabled=value;health.Reset();deathUntil=0;
        if(manualUntil==0)CancelOutput();
        message=value?"联动已启用，等待新的血量变化":"联动已关闭";
    }}
    public void Stop(string reason="已停止输出"){lock(sync){UpdateReadiness(link,Phone,settings);enabled=false;manualUntil=deathUntil=0;health.Reset();CancelOutput();message=reason;}}
    public void Test(int percent,int durationMs=1000){lock(sync){
        UpdateReadiness(link,Phone,settings);
        if(!Usable(Phone,settings))throw new InvalidOperationException(NotReadyReason(Phone,settings));
        if(percent is <0 or >100||durationMs is <100 or >10000)throw new ArgumentException("测试参数无效");
        CancelOutput();enabled=false;deathUntil=0;health.Reset();manualPercent=percent;manualUntil=Environment.TickCount64+durationMs;
        message=$"正在测试 {percent}%（{durationMs/1000.0:0.0} 秒）；结束归零，联动暂时关闭";
    }}
    public void Sample(double current,double maximum,bool alive,bool isPaused,bool available){lock(sync){
        UpdateReadiness(link,Phone,settings);
        if(!available||isPaused){
            if(valid&&!paused&&manualUntil==0)CancelOutput();
            valid=available;paused=isPaused;health.Reset();deathUntil=0;return;
        }
        if(!valid||paused)health.Reset();valid=true;paused=false;
        if(enabled){if(health.Step(current,maximum,alive,settings)){deathUntil=Environment.TickCount64+settings.DeathDurationMs;CancelOutput();message="死亡限时反馈，结束归零";}}
        else {health.Reset();health.Step(current,maximum,alive,settings);}
    }}
    static bool Usable(PhoneState p,LinkSettings s)=>p.Ready&&(s.ChannelA||s.ChannelB)&&(!s.ChannelA||p.MutedA||p.LimitA!=null)&&(!s.ChannelB||p.MutedB||p.LimitB!=null);
    static string NotReadyReason(PhoneState p,LinkSettings s){
        if(!p.Connected)return "手机尚未配对，请先生成二维码并扫码";
        if(p.Slot==null)return p.Devices.Length>1?"请在通道页选择一个郊狼设备":"手机已配对，等待手机连接郊狼 3.0";
        if(!p.Ready)return "手机已配对，但郊狼蓝牙连接尚未就绪";
        if(!s.ChannelA&&!s.ChannelB)return "请在通道页至少启用一个输出通道";
        string missing=string.Join("、",new[]{s.ChannelA&&!p.MutedA&&p.LimitA==null?"A":null,s.ChannelB&&!p.MutedB&&p.LimitB==null?"B":null}.Where(x=>x!=null));
        return $"手机尚未上报 {missing} 通道上限，请在手机确认通道设置";
    }
    async Task RunAsync(){
        bool wasOutput=false;
        while(!lifetime.IsCancellationRequested){
            try{
                await Task.Delay(50,lifetime.Token);
                var active=link;if(active==null)continue;
                var phone=active.Snapshot;
                bool clear;int a=0,b=0,duration=0;LinkSettings cfg;CancellationToken token;
                lock(sync){
                    cfg=settings.Copy();long now=Environment.TickCount64;
                    UpdateReadiness(active,phone,cfg);
                    if(manualUntil!=0&&now>=manualUntil){manualUntil=0;CancelOutput();message="测试已结束，输出归零";}
                    if(deathUntil!=0&&now>=deathUntil){deathUntil=0;CancelOutput();message="死亡反馈结束，等待复活";}
                    clear=clearRequested;clearRequested=false;token=output.Token;
                    double pct=manualUntil>now?manualPercent:enabled&&valid&&!paused?(deathUntil>now?cfg.DeathPercent:health.Signal/15*100):0;
                    long remaining=manualUntil>now?manualUntil-now:deathUntil>now?deathUntil-now:600;
                    if(pct>0&&Usable(phone,cfg)&&now-lastSent>=500){
                        duration=(int)Math.Min(cfg.Mode=="lan"?500:600,remaining);
                        if(cfg.ChannelA&&!phone.MutedA)a=HealthModel.Target(phone.LimitA,pct,cfg.ScaleA);
                        if(cfg.ChannelB&&!phone.MutedB)b=HealthModel.Target(phone.LimitB,pct,cfg.ScaleB);
                        if(a==0&&b==0)duration=0;
                        lastSent=now;
                    }
                    if(pct<=0&&wasOutput){clear=true;wasOutput=false;}
                }
                if(clear){using var stopTimeout=new CancellationTokenSource(1800);await active.StopOutputAsync(stopTimeout.Token);wasOutput=false;}
                if(duration>0&&!token.IsCancellationRequested&&ReferenceEquals(link,active)){
                    await active.WindowAsync(a,b,cfg.ChannelA,cfg.ChannelB,cfg.Waveform,duration,offset,token);offset+=(duration+99)/100;wasOutput=true;
                }
            }catch(OperationCanceledException){}catch(Exception e){lock(sync){enabled=false;manualUntil=deathUntil=0;message="输出已停止："+e.Message;CancelOutput();}wasOutput=false;}
        }
    }
    public async ValueTask DisposeAsync(){await DisconnectAsync();lifetime.Cancel();try{await loop;}catch(OperationCanceledException){}lifetime.Dispose();}
}
