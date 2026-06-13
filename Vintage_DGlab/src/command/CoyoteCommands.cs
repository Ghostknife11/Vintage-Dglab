using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageDLab.Util;
using VintageDLab.WebSocket;

namespace VintageDLab.Command;

public static class CoyoteCommands
{
    public static void Register(ICoreClientAPI capi)
    {
        // /dglab-connect
        capi.ChatCommands.Create("dglab-connect")
            .WithDescription("Start DG-Lab Coyote WebSocket server")
            .HandleWith(args => OnConnect());

        // /dglab-disconnect
        capi.ChatCommands.Create("dglab-disconnect")
            .WithDescription("Stop server and tracking")
            .HandleWith(args => OnDisconnect());

        // /dglab-status
        capi.ChatCommands.Create("dglab-status")
            .WithDescription("Show connection, health, and signal status")
            .HandleWith(args => OnStatus());

        // /dglab-port <N>
        capi.ChatCommands.Create("dglab-port")
            .WithDescription("Set WebSocket port (1024-65535)")
            .HandleWith(args => OnPort(args.RawArgs.PopAll() ?? ""));

        // /dglab-track
        capi.ChatCommands.Create("dglab-track")
            .WithDescription("Start tracking your health for DG-Lab output")
            .HandleWith(args => OnTrack(capi));

        // /dglab-untrack
        capi.ChatCommands.Create("dglab-untrack")
            .WithDescription("Stop health tracking")
            .HandleWith(args => OnUntrack());

        // /dglab-copy
        capi.ChatCommands.Create("dglab-copy")
            .WithDescription("Copy QR code URL to clipboard")
            .HandleWith(args => OnCopy());

        // /dglab-qr
        capi.ChatCommands.Create("dglab-qr")
            .WithDescription("Show QR code URL for pairing")
            .HandleWith(args => OnQr());
    }

    // ========== connect ==========

    private static TextCommandResult OnConnect()
    {
        var mod = VintageDLabSystem.Instance;

        if (mod.WsServer?.IsRunning == true)
            return TextCommandResult.Success(
                $"Coyote: Already running on port {mod.WsServer.ServerPort}\n" +
                $"Use /dglab-copy to copy QR URL to clipboard.");

        var port = mod.Config.WebSocketPort == 0 ? 8877 : mod.Config.WebSocketPort;
        mod.WsServer = new CoyoteWebSocketServer(mod.Mod.Logger);
        if (!mod.WsServer.Start(port))
            return TextCommandResult.Error("Coyote: Failed to start WebSocket server.");

        return TextCommandResult.Success(
            $"Coyote: WebSocket server started on ws://{mod.WsServer.ServerAddress}:{port}\n" +
            $"Use /dglab-copy to copy QR URL to clipboard.\n" +
            $"Use /dglab-track to start health monitoring.");
    }

    // ========== disconnect ==========

    private static TextCommandResult OnDisconnect()
    {
        var mod = VintageDLabSystem.Instance;
        mod.Tracker?.Stop();
        mod.WsServer?.Stop();
        mod.WsServer = null!;
        return TextCommandResult.Success("Coyote: Server and tracking stopped.");
    }

    // ========== status ==========

    private static TextCommandResult OnStatus()
    {
        var mod = VintageDLabSystem.Instance;
        var ws = mod.WsServer;
        var tr = mod.Tracker;

        if (ws == null || !ws.IsRunning)
            return TextCommandResult.Success("Coyote: Offline. Use /dglab-connect to start.");

        var conn = ws.IsConnected ? "● Connected" : "○ Waiting for app...";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Coyote Status:");
        sb.AppendLine($"  Server: ws://{ws.ServerAddress}:{ws.ServerPort}  {conn}");
        sb.AppendLine($"  Channel max: A={ws.AppAMax}  B={ws.AppBMax}");

        if (tr is { IsRunning: true })
        {
            var hpPct = tr.MaxHealth > 0 ? tr.CurrentHealth / tr.MaxHealth * 100f : 100f;
            var trendLabel = tr.Trend switch {
                "↑↑" => "!! Escalating (x" + tr.ConsecutiveHits + ")",
                "↑" => "^^ Damage",
                "↗" => "^ Damage",
                "↓" => "v Healing",
                "X" => "X DEAD (max)",
                _ => "- Holding"
            };
            sb.AppendLine($"  Health: {tr.CurrentHealth:F0}/{tr.MaxHealth:F0} ({hpPct:F0}%)  {trendLabel}");
            sb.AppendLine($"  Signal: {tr.Signal}/15");
            if (ws.IsConnected)
            {
                int a = (int)(tr.Signal / 15f * ws.AppAMax);
                int b = (int)(tr.Signal / 15f * ws.AppBMax);
                sb.AppendLine($"  Output: A={a}  B={b}");
            }
        }
        else
        {
            sb.AppendLine($"  Tracking: none (use /dglab-track)");
        }

        return TextCommandResult.Success(sb.ToString().TrimEnd());
    }

    // ========== port ==========

    private static TextCommandResult OnPort(string raw)
    {
        var mod = VintageDLabSystem.Instance;
        if (!int.TryParse(raw.Trim(), out var port))
            return TextCommandResult.Error("Coyote: Usage: /dglab-port <1024-65535>");
        if (port < 1024 || port > 65535)
            return TextCommandResult.Error("Coyote: Port must be 1024-65535.");

        mod.Config.WebSocketPort = port;
        mod.SaveConfig();
        return TextCommandResult.Success($"Coyote: Port set to {port}. Use /dglab-connect to apply.");
    }

    // ========== track ==========

    private static TextCommandResult OnTrack(ICoreClientAPI capi)
    {
        var mod = VintageDLabSystem.Instance;

        if (mod.WsServer == null || !mod.WsServer.IsRunning)
            return TextCommandResult.Error("Coyote: Server not running. Use /dglab-connect first.");

        if (mod.Tracker is { IsRunning: true })
            return TextCommandResult.Success("Coyote: Already tracking. Use /dglab-untrack to stop.");

        mod.Tracker = new HealthTracker(capi);
        mod.Tracker.Start();

        return TextCommandResult.Success(
            "Coyote: Tracking your health.\n" +
            "  Damage → output rises\n" +
            "  Healing → output falls\n" +
            "  Stable → output holds\n" +
            "Use /dglab-status to see current state.");
    }

    // ========== untrack ==========

    private static TextCommandResult OnUntrack()
    {
        var mod = VintageDLabSystem.Instance;
        if (mod.Tracker is not { IsRunning: true })
            return TextCommandResult.Success("Coyote: Not tracking anyone.");

        mod.Tracker.Stop();
        return TextCommandResult.Success("Coyote: Tracking stopped.");
    }

    // ========== copy ==========

    private static TextCommandResult OnCopy()
    {
        var mod = VintageDLabSystem.Instance;
        if (mod.WsServer == null || !mod.WsServer.IsRunning)
            return TextCommandResult.Error("Coyote: Server not running. Use /dglab-connect first.");

        var url = mod.WsServer.GetQrCodeUrl();
        if (ClipboardHelper.SetText(url))
            return TextCommandResult.Success("Coyote: QR URL copied! Paste into browser and scan with DG-Lab app.");
        else
            return TextCommandResult.Error("Coyote: Failed to copy. Manually: " + url);
    }

    // ========== qr ==========

    private static TextCommandResult OnQr()
    {
        var mod = VintageDLabSystem.Instance;
        if (mod.WsServer == null || !mod.WsServer.IsRunning)
            return TextCommandResult.Error("Coyote: Server not running.");

        return TextCommandResult.Success("Coyote QR URL:\n" + mod.WsServer.GetQrCodeUrl());
    }
}
