// SPDX-License-Identifier: GPL-3.0-only
using Cairo;
using QRCoder;
using Vintagestory.API.Client;

namespace VintageDLab;

public sealed class LinkDialog : GuiDialog
{
    readonly VintageDLabSystem mod;
    LinkSettings draft;
    int page;
    QrDialog? preview;
    string? qrUrl;
    QRCodeData? qr;
    string deviceSignature="";
    readonly Dictionary<string,string> shown=new();
    public override string ToggleKeyCombinationCode=>"vintagedglab";
    public LinkDialog(ICoreClientAPI api,VintageDLabSystem mod):base(api){this.mod=mod;draft=mod.Controller.Settings;Compose();}
    static ElementBounds B(double x,double y,double w,double h=28)=>ElementBounds.Fixed(x,y,w,h);
    static CairoFont Font(float size=16)=>CairoFont.WhiteSmallText().WithFontSize(size);
    void Act(Action action){try{action();}catch(Exception e){mod.Controller.Notify(e.Message);}}
    void SwitchPage(int next){page=next;Compose();}
    void Compose()
    {
        shown.Clear();qrUrl=null;qr?.Dispose();qr=null;
        ClearComposers();
        var content=ElementBounds.Fixed(0,0,760,570);
        var bounds=ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle).WithFixedAlignmentOffset(0,0);
        SingleComposer=capi.Gui.CreateCompo("vintagedglab-control",bounds)
            .AddShadedDialogBG(content).AddDialogTitleBar("Vintage Story · 郊狼联动",()=>TryClose()).BeginChildElements(content);
        string[] pages=["手机连接","血量联动","通道与波形","诊断与关于"];
        for(int i=0;i<4;i++){int n=i;SingleComposer.AddSmallButton((page==i?"● ":"")+pages[i],()=>{SwitchPage(n);return true;},B(12+i*186,43,178,30));}
        SingleComposer.AddDynamicText("",Font(15),B(18,86,722,58),"summary");
        switch(page){case 0:ConnectionPage();break;case 1:HealthPage();break;case 2:OutputPage();break;default:AboutPage();break;}
        SingleComposer.AddDynamicText("",Font(14),B(18,453,722,54),"message");
        SingleComposer.AddSmallButton("启用联动",()=>{Act(()=>mod.Controller.SetEnabled(!mod.Controller.Enabled));return true;},B(18,514,142,32));
        SingleComposer.AddSmallButton("测试 20%",()=>{Act(()=>{mod.Save(draft);mod.Controller.Test(20);});return true;},B(173,514,130,32));
        SingleComposer.AddSmallButton("测试死亡",()=>{Act(()=>{mod.Save(draft);mod.Controller.Test(draft.DeathPercent,draft.DeathDurationMs);});return true;},B(316,514,130,32));
        SingleComposer.AddSmallButton("停止输出",()=>{mod.Controller.Stop();return true;},B(459,514,130,32));
        SingleComposer.AddSmallButton("保存设置",()=>{Act(()=>mod.Save(draft));return true;},B(602,514,137,32));
        SingleComposer.EndChildElements().Compose();
        foreach(var (key,value) in switches)SingleComposer.GetSwitch(key)?.SetValue(value);
        switches.Clear();
        foreach(var (key,value) in inputs)SingleComposer.GetTextInput(key)?.SetValue(value);
        inputs.Clear();
        Refresh(true);
    }
    void Text(string text,double x,double y,double width=340)=>SingleComposer.AddStaticText(text,Font(),B(x,y,width));
    readonly Dictionary<string,bool> switches=new();
    readonly Dictionary<string,string> inputs=new();
    void Toggle(string label,string key,bool value,double x,double y,Action<bool> changed){Text(label,x,y,300);SingleComposer.AddSwitch(v=>Act(()=>changed(v)),B(x+305,y,34,24),key);switches[key]=value;}
    void Number(string label,string key,double value,double x,double y,Action<double> changed){
        Text(label,x,y,230);SingleComposer.AddTextInput(B(x+233,y-2,100,28),text=>Act(()=>{
            if(!double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))throw new ArgumentException(label+"：请输入数字");changed(n);
        }),Font(),key);inputs[key]=value.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
    }
    void ConnectionPage()
    {
        SingleComposer.AddDropDown(["v4","lan"],["DG-LAB 4 · 官方中继","兼容扫码 · 同一局域网"],draft.Mode=="v4"?0:1,(value,_)=>{draft.Mode=value;},B(18,156,340),"mode");
        Text("中继地址（DG-LAB 4）",18,196);
        SingleComposer.AddTextInput(B(18,226,340),v=>draft.Relay=v,Font(14),"relay");inputs["relay"]=draft.Relay;
        Number("局域网端口","port",draft.WebSocketPort,18,272,v=>draft.WebSocketPort=(int)v);
        var addresses=DeviceLink.LanAddresses();if(addresses.Length==0)addresses=["127.0.0.1"];
        int index=Array.IndexOf(addresses,draft.LanAddress);if(index<0)index=0;
        SingleComposer.AddDropDown(addresses,addresses,index,(v,_)=>draft.LanAddress=v,B(18,312,340),"address");
        if(string.IsNullOrEmpty(draft.LanAddress))draft.LanAddress=addresses[index];
        SingleComposer.AddSmallButton("生成连接二维码",()=>{Act(()=>{mod.Save(draft);_ = mod.Controller.ConnectAsync();});return true;},B(18,354,210,32));
        SingleComposer.AddSmallButton("断开",()=>{_ = mod.Controller.DisconnectAsync();return true;},B(240,354,118,32));
        SingleComposer.AddDynamicCustomDraw(B(420,152,256,256),DrawQr,"qr");
        SingleComposer.AddSmallButton("放大二维码",()=>{ComposeLarge();return true;},B(405,418,142,26));
        SingleComposer.AddSmallButton("复制链接",()=>{Act(()=>capi.Input.ClipboardText=mod.Controller.Phone.Qr??throw new InvalidOperationException("请先生成二维码"));return true;},B(560,418,142,26));
        Text("扫码后等待设备和上限报告；连接本身不输出。",18,399,370);
    }
    void ComposeLarge(){
        if(mod.Controller.Phone.Qr==null){mod.Controller.Notify("请先点击“生成连接二维码”，等待服务器连接成功");return;}
        preview?.TryClose();preview?.Dispose();
        preview=new QrDialog(capi,()=>mod.Controller.Phone.Qr);preview.TryOpen();
    }
    void HealthPage(){
        Number("受伤基础系数","damage",draft.DamageScale,18,164,v=>draft.DamageScale=v);
        Number("连续受伤额外系数","bonus",draft.ChainBonus,18,213,v=>draft.ChainBonus=v);
        Number("回血衰减系数","healing",draft.HealingScale,18,262,v=>draft.HealingScale=v);
        Number("单次信号变化上限","step",draft.MaxStep,18,311,v=>draft.MaxStep=v);
        Number("死亡强度比例 %","death",draft.DeathPercent,395,164,v=>draft.DeathPercent=(int)v);
        Number("死亡反馈时长（秒）","duration",draft.DeathDurationMs/1000.0,395,213,v=>draft.DeathDurationMs=(int)Math.Round(v*1000));
        Toggle("设备就绪后自动联动","auto",draft.AutoEnable,395,270,v=>draft.AutoEnable=v);
        Text("受伤递增，回血降低，血量不变时保持。",18,374,690);
        Text("死亡只触发一次限时反馈；结束归零，复活重置。",18,409,690);
    }
    void OutputPage(){
        Toggle("A 通道输出","a",draft.ChannelA,18,164,v=>draft.ChannelA=v);
        Toggle("B 通道输出","b",draft.ChannelB,395,164,v=>draft.ChannelB=v);
        Number("A 通道输出比例 %","scaleA",draft.ScaleA,18,220,v=>draft.ScaleA=(int)v);
        Number("B 通道输出比例 %","scaleB",draft.ScaleB,395,220,v=>draft.ScaleB=(int)v);
        Text("波形",18,278);
        string[] codes=["bubble","steady","breathe"];
        SingleComposer.AddDropDown(codes,["气泡节奏（原创）","平稳（原创）","呼吸（原创）"],Math.Max(0,Array.IndexOf(codes,draft.Waveform)),(v,_)=>draft.Waveform=v,B(18,311,340),"wave");
        var phone=mod.Controller.Phone;
        if(phone.Devices.Length>0)SingleComposer.AddDropDown(phone.Devices.Select(d=>d.Slot).ToArray(),phone.Devices.Select(d=>d.Name).ToArray(),Math.Max(0,Array.FindIndex(phone.Devices,d=>d.Slot==phone.Slot)),(v,_)=>mod.Controller.SelectDevice(v),B(395,311,340),"device");
        SingleComposer.AddDynamicText("",Font(),B(18,363,714,65),"targets");
    }
    void AboutPage(){
        Text("Vintage-DGLab 1.3.0 · 客户端模组",18,164,710);
        Text("Ctrl+Alt+G / .dglab 打开；服务器无需安装。",18,204,710);
        Text("上限由手机管理；比例作用于实际通道数值。",18,244,710);
        Text("手动测试会暂停联动；测试结束归零。",18,284,710);
        Text("GPL v3 · Ghostknife 及贡献者；QRCoder 按 MIT。",18,324,710);
        Text("来源、许可与验证范围见安装包说明。",18,364,710);
        SingleComposer.AddSmallButton("恢复默认参数",()=>{draft=new();Compose();mod.Controller.Notify("默认参数已填入；保存后生效");return true;},B(18,404,260,30));
    }
    void DrawQr(Context ctx,ImageSurface surface,ElementBounds bounds){
        DrawMatrix(ctx,bounds,qr);
    }
    internal static void DrawMatrix(Context ctx,ElementBounds bounds,QRCodeData? data){
        ctx.SetSourceRGB(1,1,1);ctx.Rectangle(0,0,bounds.InnerWidth,bounds.InnerHeight);ctx.Fill();
        if(data==null){
            ctx.SetSourceRGB(0.25,0.29,0.32);ctx.SelectFontFace("Microsoft YaHei",FontSlant.Normal,FontWeight.Normal);ctx.SetFontSize(14);
            string[] lines=["准备手机扫码连接","点击左侧“生成连接二维码”"];
            for(int i=0;i<lines.Length;i++){var ext=ctx.TextExtents(lines[i]);ctx.MoveTo((bounds.InnerWidth-ext.Width)/2,bounds.InnerHeight/2-10+i*28);ctx.ShowText(lines[i]);}
            return;
        }
        int n=data.ModuleMatrix.Count;double size=Math.Floor(Math.Min(bounds.InnerWidth,bounds.InnerHeight)/(n+4));
        if(size<1)return;
        double x=Math.Floor((bounds.InnerWidth-n*size)/2),y=Math.Floor((bounds.InnerHeight-n*size)/2);
        ctx.SetSourceRGB(0,0,0);
        for(int row=0;row<n;row++)for(int col=0;col<n;col++)if(data.ModuleMatrix[row][col])ctx.Rectangle(x+col*size,y+row*size,size,size);
        ctx.Fill();
    }
    void UpdateText(string key,string value){if(shown.GetValueOrDefault(key)==value)return;shown[key]=value;SingleComposer.GetDynamicText(key)?.SetNewText(value);}
    public void Refresh(bool force=false){
        preview?.Refresh();
        if(!force&&!IsOpened())return;
        var c=mod.Controller;var phone=c.Phone;var h=c.Health;
        string N(int? n)=>n?.ToString()??"未上报";
        UpdateText("summary",$"{(c.Enabled?"● 联动已启用":"○ 联动未启用")}   {phone.Status}\n生命 {h.Health:0.##}/{h.Maximum:0.##}  信号 {h.Signal:0.##}/15  连续受伤阶数 {h.Chain}  {h.Trend}   A {N(phone.ActualA)}/{N(phone.LimitA)}{(phone.MutedA?"（静音）":"")}  B {N(phone.ActualB)}/{N(phone.LimitB)}{(phone.MutedB?"（静音）":"")}");
        UpdateText("message",c.Message);
        if(page==0&&qrUrl!=phone.Qr){qrUrl=phone.Qr;qr?.Dispose();qr=null;if(qrUrl!=null){using var gen=new QRCodeGenerator();qr=gen.CreateQrCode(qrUrl,QRCodeGenerator.ECCLevel.M);}SingleComposer.GetCustomDraw("qr")?.Redraw();}
        if(page==2){
            string target(int? max,int scale)=>max==null?"等待手机上限":HealthModel.Target(max,h.Signal/15*100,scale).ToString();
            UpdateText("targets",$"预计实际输出：A {target(phone.LimitA,draft.ScaleA)} / B {target(phone.LimitB,draft.ScaleB)}\n手机上限只读；0 上限不输出，未知上限不猜测。");
            string signature=string.Join("|",phone.Devices.Select(d=>d.Slot));
            if(deviceSignature!=signature){deviceSignature=signature;Compose();}
        }
    }
    public override void Dispose(){preview?.TryClose();preview?.Dispose();qr?.Dispose();base.Dispose();}
}

sealed class QrDialog : GuiDialog
{
    readonly Func<string?> url;
    QRCodeData? data;
    string? current;
    public override string ToggleKeyCombinationCode=>"";
    public override bool UnregisterOnClose=>true;
    public QrDialog(ICoreClientAPI api,Func<string?> url):base(api){this.url=url;
        UpdateData();
        var content=ElementBounds.Fixed(0,0,500,550);
        SingleComposer=capi.Gui.CreateCompo("vintagedglab-qr",ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(content).AddDialogTitleBar("手机扫码连接",()=>TryClose()).BeginChildElements(content)
            .AddDynamicCustomDraw(ElementBounds.Fixed(20,50,460,460),(ctx,s,b)=>LinkDialog.DrawMatrix(ctx,b,data),"qr")
            .AddSmallButton("关闭",()=>{TryClose();return true;},ElementBounds.Fixed(150,515,200,28)).EndChildElements().Compose();
    }
    void UpdateData(){current=url();data?.Dispose();data=null;if(current!=null){using var generator=new QRCodeGenerator();data=generator.CreateQrCode(current,QRCodeGenerator.ECCLevel.M);}}
    public void Refresh(){if(IsOpened()&&current!=url()){UpdateData();SingleComposer.GetCustomDraw("qr")?.Redraw();}}
    public override void Dispose(){data?.Dispose();base.Dispose();}
}
