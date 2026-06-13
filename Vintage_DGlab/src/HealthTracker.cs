using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageDLab;

/// <summary>
/// Client-side health tracker.
///
/// Damage without healing: each hit is stronger than the last (consecutive hit bonus).
/// Healing: resets the hit chain and decreases output.
/// Death: output jumps to maximum immediately.
/// Health stable: output holds.
/// </summary>
public class HealthTracker
{
    private readonly ICoreClientAPI _capi;
    private float _previousHealth;
    private long _listenerId;
    private bool _running;
    private int _tickCounter;
    private bool _wasAlive;
    private int _consecutiveHits;   // Number of damage ticks since last heal/death
    private float _totalDmgThisChain; // Total damage taken since last heal (for display)

    public int Signal { get; private set; }
    public float CurrentHealth { get; private set; }
    public float MaxHealth { get; private set; } = 15f;
    public string Trend => _trend;
    public int ConsecutiveHits => _consecutiveHits;

    private string _trend = "=";
    private float _lastDelta;

    // Scaling
    private const float BASE_DMG_SCALE = 1.0f;     // Base signal per 1 HP damage
    private const float CONSECUTIVE_BONUS = 0.5f;   // Extra signal per 1 HP per consecutive hit
    private const float HEAL_SCALE = 1.0f;           // Signal decrease per 1 HP healed
    private const float MAX_DELTA_PER_TICK = 4f;     // Max signal change per tick

    public bool IsRunning => _running;

    public HealthTracker(ICoreClientAPI capi)
    {
        _capi = capi;
    }

    public void Start()
    {
        if (_running) return;

        var player = _capi.World.Player;
        var healthTree = player.Entity.WatchedAttributes.GetTreeAttribute("health");
        _previousHealth = healthTree?.GetFloat("currenthealth", 15f) ?? 15f;
        CurrentHealth = _previousHealth;
        MaxHealth = healthTree?.GetFloat("maxhealth", 15f) ?? 15f;
        _lastDelta = 0;
        _tickCounter = 0;
        _wasAlive = player.Entity.Alive;
        _consecutiveHits = 0;
        _totalDmgThisChain = 0;
        _trend = "=";
        Signal = 0;
        _running = true;

        _listenerId = _capi.Event.RegisterGameTickListener(OnTick, 250);
        _capi.Logger.Notification("[Vintage-DGLab] Health tracking started.");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _capi.Event.UnregisterGameTickListener(_listenerId);
        _capi.Logger.Notification("[Vintage-DGLab] Health tracking stopped.");
        Signal = 0;
    }

    private void OnTick(float dt)
    {
        if (!_running) return;

        var mod = VintageDLabSystem.Instance;
        if (mod?.WsServer == null || !mod.WsServer.IsRunning) return;

        var interval = mod.Config.SensorTickInterval;
        _tickCounter++;
        if (_tickCounter < interval) return;
        _tickCounter = 0;

        var player = _capi.World.Player;
        if (player?.Entity == null) return;

        // Detect death transition: just died → max signal
        if (!player.Entity.Alive && _wasAlive)
        {
            Signal = 15;
            _trend = "X";
            _consecutiveHits = 0;
            _wasAlive = false;
            SendToDevice(mod);
            return;
        }

        // Still dead: hold at max
        if (!player.Entity.Alive)
        {
            Signal = Math.Max(Signal, 15);
            _trend = "X";
            SendToDevice(mod);
            return;
        }

        // Respawned (was dead, now alive): reset
        if (player.Entity.Alive && !_wasAlive)
        {
            Signal = 0;
            _consecutiveHits = 0;
            _totalDmgThisChain = 0;
            _trend = "=";
            var healthTree = player.Entity.WatchedAttributes.GetTreeAttribute("health");
            _previousHealth = healthTree?.GetFloat("currenthealth", 15f) ?? 15f;
            CurrentHealth = _previousHealth;
            MaxHealth = healthTree?.GetFloat("maxhealth", 15f) ?? 15f;
            _wasAlive = true;
            return;
        }

        _wasAlive = true;

        // Read health
        var ht = player.Entity.WatchedAttributes.GetTreeAttribute("health");
        if (ht == null) return;

        var curHp = ht.GetFloat("currenthealth", 0f);
        var maxHp = ht.GetFloat("maxhealth", 15f);
        if (maxHp <= 0) maxHp = 15f;
        CurrentHealth = curHp;
        MaxHealth = maxHp;

        float delta = _previousHealth - curHp; // + = damage, - = heal
        _lastDelta = delta;

        if (delta > 0.01f)
        {
            // ===== DAMAGE TAKEN =====
            _consecutiveHits++;
            _totalDmgThisChain += delta;

            // Scale: base + consecutive_bonus * hit_count
            float scale = BASE_DMG_SCALE + CONSECUTIVE_BONUS * (_consecutiveHits - 1);
            int increase = (int)Math.Clamp(delta * scale, 1, MAX_DELTA_PER_TICK);
            Signal = Math.Min(15, Signal + increase);

            _trend = _consecutiveHits >= 3 ? "↑↑" :
                     _consecutiveHits >= 2 ? "↑" : "↗";
        }
        else if (delta < -0.01f)
        {
            // ===== HEALING =====
            // Reset consecutive hit chain
            _consecutiveHits = 0;
            _totalDmgThisChain = 0;

            int decrease = (int)Math.Clamp(-delta * HEAL_SCALE, 1, MAX_DELTA_PER_TICK);
            Signal = Math.Max(0, Signal - decrease);

            _trend = "↓";
        }
        else
        {
            // ===== STABLE =====
            _trend = "=";
        }

        _previousHealth = curHp;

        SendToDevice(mod);
    }

    private void SendToDevice(VintageDLabSystem mod)
    {
        if (!mod.WsServer.IsConnected) return;
        float pct = Signal / 15f;
        int strA = (int)(pct * mod.WsServer.AppAMax);
        int strB = (int)(pct * mod.WsServer.AppBMax);
        mod.WsServer.SendPulse(strA, strB);
    }
}
