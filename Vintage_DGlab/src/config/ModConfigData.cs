namespace VintageDLab.Config;

/// <summary>
/// Mod configuration data, persisted to vintagedglab.json in the mod config folder.
/// </summary>
public class ModConfigData
{
    /// <summary>WebSocket server port. Default 8877.</summary>
    public int WebSocketPort { get; set; } = 8877;

    /// <summary>
    /// Tick interval between signal checks on the gimbal sensor.
    /// Higher values reduce server load but make response less smooth.
    /// </summary>
    public int SensorTickInterval { get; set; } = 4;

    /// <summary>
    /// How often to regenerate the random waveform (in sensor ticks).
    /// 5 × SensorTickInterval = waveform refresh rate in game ticks.
    /// </summary>
    public int WaveformRefreshInterval { get; set; } = 5;
}
