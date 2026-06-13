using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageDLab.Config;
using VintageDLab.WebSocket;

namespace VintageDLab;

public class VintageDLabSystem : ModSystem
{
    public static VintageDLabSystem Instance { get; private set; } = null!;
    public ICoreClientAPI? Capi { get; private set; }

    public CoyoteWebSocketServer? WsServer { get; set; }
    public HealthTracker? Tracker { get; set; }
    public ModConfigData Config { get; private set; } = new();

    // Client-only mod — no server install needed
    public override bool ShouldLoad(EnumAppSide forSide)
        => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        Instance = this;
        Capi = api;
        api.Logger.Notification("[Vintage-DGLab] Loading config...");
        try
        {
            Config = api.LoadModConfig<ModConfigData>("vintagedglab.json")
                     ?? new ModConfigData();
        }
        catch { Config = new ModConfigData(); }

        api.Logger.Notification("[Vintage-DGLab] Registering commands...");
        Command.CoyoteCommands.Register(api);
        api.Logger.Notification("[Vintage-DGLab] Ready. Use /dglab-connect to start.");
    }

    public void SaveConfig()
    {
        try
        {
            var api = Instance.Capi;
            api?.StoreModConfig(Config, "vintagedglab.json");
        }
        catch { }
    }

    public override void Dispose()
    {
        Tracker?.Stop();
        WsServer?.Stop();
        base.Dispose();
    }
}
