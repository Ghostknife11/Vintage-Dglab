// SPDX-License-Identifier: GPL-3.0-only
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageDLab;

public sealed class VintageDLabSystem : ModSystem
{
    ICoreClientAPI? api;
    LinkDialog? dialog;
    public LinkController Controller {get;private set;}=null!;
    long listener;
    bool leaving;
    public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Client;
    public override void StartClientSide(ICoreClientAPI capi)
    {
        api=capi;LinkSettings cfg;
        try{cfg=capi.LoadModConfig<LinkSettings>("vintagedglab.json")??new();cfg.Validate();}
        catch(Exception e){capi.Logger.Warning("[Vintage-DGLab] Invalid old config; using defaults: {0}",e.Message);cfg=new();}
        Controller=new(cfg);
        capi.Input.RegisterHotKey("vintagedglab","郊狼联动控制台",GlKeys.G,HotkeyType.GUIOrOtherControls,true,true,false);
        capi.Input.SetHotKeyHandler("vintagedglab",_=>{Open();return true;});
        capi.ChatCommands.Create("dglab").WithDescription("打开游戏内郊狼控制台").HandleWith(_=>{Open();return TextCommandResult.Success();});
        Register("dglab-connect",()=>_ = Controller.ConnectAsync());
        Register("dglab-disconnect",()=>_ = Controller.DisconnectAsync());
        Register("dglab-track",()=>Controller.SetEnabled(true));
        Register("dglab-untrack",()=>Controller.Stop("追踪与输出已停止"));
        Register("dglab-stop",()=>Controller.Stop());
        Register("dglab-qr",Open);Register("dglab-status",Open);
        Register("dglab-copy",()=>capi.Input.ClipboardText=Controller.Phone.Qr??throw new InvalidOperationException("请先生成二维码"));
        capi.Event.LeaveWorld+=OnLeave;
        capi.Event.PauseResume+=OnPause;
        listener=capi.Event.RegisterGameTickListener(Tick,100);
        capi.Logger.Notification("[Vintage-DGLab] v1.3.1 ready. Ctrl+Alt+G or .dglab");
    }
    void Register(string name,Action action)=>api!.ChatCommands.Create(name).WithDescription("郊狼控制台兼容命令").HandleWith(_=>{
        try{action();return TextCommandResult.Success("已执行；使用 .dglab 查看控制台");}catch(Exception e){return TextCommandResult.Error(e.Message);}
    });
    void Tick(float dt)
    {
        if(api==null||leaving)return;
        var player=api.World?.Player;
        var entity=player?.Entity;
        var hp=entity?.WatchedAttributes.GetTreeAttribute("health");
        Controller.Sample(hp?.GetFloat("currenthealth")??double.NaN,hp?.GetFloat("maxhealth")??double.NaN,entity?.Alive??false,api.IsGamePaused,hp!=null);
        dialog?.Refresh();
    }
    void OnPause(bool paused){if(paused)Controller.Sample(0,0,false,true,true);}
    void OnLeave(){leaving=true;Controller.Stop("已退出世界");_ = Controller.DisconnectAsync();dialog?.TryClose();}
    public void Save(LinkSettings cfg){cfg.Validate();api!.StoreModConfig(cfg,"vintagedglab.json");Controller.Apply(cfg);}
    public void Open(){if(api==null||leaving)return;dialog??=new LinkDialog(api,this);dialog.Toggle();}
    public override void Dispose()
    {
        if(api!=null){api.Event.LeaveWorld-=OnLeave;api.Event.PauseResume-=OnPause;api.Event.UnregisterGameTickListener(listener);}
        dialog?.Dispose();if(Controller!=null)_ = Controller.DisposeAsync();base.Dispose();
    }
}
