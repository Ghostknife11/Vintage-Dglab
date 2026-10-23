// SPDX-License-Identifier: GPL-3.0-only
namespace VintageDLab;

public sealed class LinkSettings
{
    public int Version { get; set; } = 2;
    public string Mode { get; set; } = "v4";
    public string Relay { get; set; } = "wss://trex.dungeon-lab.cn/v4";
    public int WebSocketPort { get; set; } = 8877;
    public string LanAddress { get; set; } = "";
    public bool ChannelA { get; set; } = true;
    public bool ChannelB { get; set; } = true;
    public int ScaleA { get; set; } = 100;
    public int ScaleB { get; set; } = 100;
    public double DamageScale { get; set; } = 1;
    public double ChainBonus { get; set; } = .5;
    public double HealingScale { get; set; } = 1;
    public double MaxStep { get; set; } = 4;
    public int DeathPercent { get; set; } = 100;
    public int DeathDurationMs { get; set; } = 1000;
    public string Waveform { get; set; } = "bubble";
    public bool AutoEnable { get; set; } = true;
    public LinkSettings Copy() => (LinkSettings)MemberwiseClone();
    public void Validate()
    {
        if (Mode is not ("v4" or "lan")) throw new ArgumentException("连接方式无效");
        if (!Uri.TryCreate(Relay, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss")) throw new ArgumentException("服务器必须为 ws:// 或 wss:// 地址");
        if (WebSocketPort is < 1024 or > 65535) throw new ArgumentException("端口范围为 1024～65535");
        if (ScaleA is < 0 or > 100 || ScaleB is < 0 or > 100 || DeathPercent is < 0 or > 100) throw new ArgumentException("比例范围为 0～100%");
        if (DeathDurationMs is < 100 or > 10000 || DeathDurationMs % 100 != 0) throw new ArgumentException("时长为 0.1～10 秒，步长 0.1 秒");
        foreach (var x in new[] { DamageScale, ChainBonus, HealingScale, MaxStep })
            if (!double.IsFinite(x) || x < 0 || x > 15) throw new ArgumentException("血量系数范围为 0～15");
        if (Waveform is not ("bubble" or "steady" or "breathe")) throw new ArgumentException("未知波形");
    }
}

public sealed class HealthModel
{
    public double Signal { get; private set; }
    public double Health { get; private set; }
    public double Maximum { get; private set; }
    public int Chain { get; private set; }
    public string Trend { get; private set; } = "等待血量";
    public bool Alive { get; private set; }
    bool initialized;
    public void Reset() { initialized = false; Signal = 0; Chain = 0; Trend = "等待新的血量变化"; }
    public bool Step(double health, double maximum, bool alive, LinkSettings settings)
    {
        if (!double.IsFinite(health) || !double.IsFinite(maximum) || maximum <= 0) { Reset(); return false; }
        if (!initialized || alive && !Alive) {
            Health = health; Maximum = maximum; Alive = alive; initialized = true;
            Signal = 0; Chain = 0; Trend = alive ? "血量稳定" : "等待复活"; return false;
        }
        bool died = Alive && !alive;
        double delta = Health - health;
        Health = health; Maximum = maximum; Alive = alive;
        if (died || !alive) { Signal = 0; Chain = 0; Trend = "死亡"; return died; }
        if (delta > .0001) {
            Chain++;
            Signal = Math.Min(15, Signal + Math.Min(settings.MaxStep, delta * (settings.DamageScale + settings.ChainBonus * (Chain - 1))));
            Trend = "受伤增强";
        } else if (delta < -.0001) {
            Chain = 0; Signal = Math.Max(0, Signal - Math.Min(settings.MaxStep, -delta * settings.HealingScale)); Trend = "回血减弱";
        } else Trend = "血量稳定";
        return false;
    }
    public static int Target(int? limit, double percent, int channelPercent)
    {
        if (limit is null or < 0 or > 200 || !double.IsFinite(percent)) throw new InvalidOperationException("手机上限未知或无效");
        return (int)Math.Floor(limit.Value * Math.Clamp(percent, 0, 100) / 100 * Math.Clamp(channelPercent, 0, 100) / 100);
    }
}

public static class Waves
{
    // Original mathematical presets; no upstream waveform table is copied.
    public static string[] Frames(string name, int durationMs, int offset = 0)
    {
        int count = Math.Clamp((durationMs + 99) / 100, 1, 100);
        return Enumerable.Range(0, count).Select(i => {
            int amplitude = name switch { "steady" => 65, "breathe" => 20 + (int)(70 * (.5 + .5 * Math.Sin((i + offset) * Math.PI / 10))), _ => (i + offset) % 2 == 0 ? 0 : 100 };
            return "2D2D2D2D" + string.Concat(Enumerable.Repeat(amplitude.ToString("X2"), 4));
        }).ToArray();
    }
}
