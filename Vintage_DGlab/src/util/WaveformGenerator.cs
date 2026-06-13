namespace VintageDLab.Util;

/// <summary>
/// Generates random waveform hex data for DG-Lab pulse commands.
/// Each hex string = 16 chars (8 bytes) = 4 frequency+intensity pairs = 100ms.
/// </summary>
public static class WaveformGenerator
{
    private static readonly Random RNG = new();

    /// <summary>Generate a fresh random waveform definition.</summary>
    public static WaveformDef Generate()
    {
        int len = 15 + RNG.Next(25); // 15-39 elements (1.5-3.9 seconds)
        var hex = new string[len];
        for (int i = 0; i < len; i++)
        {
            hex[i] = RandomHex();
        }
        return new WaveformDef("random", hex);
    }

    private static string RandomHex()
    {
        var sb = new System.Text.StringBuilder(16);
        for (int j = 0; j < 4; j++)
        {
            int f = 20 + RNG.Next(81);  // frequency: 20-100
            int a = 10 + RNG.Next(91);  // intensity: 10-100
            sb.Append($"{f:X2}{a:X2}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a pulse command string for the given channel.
    /// Format: pulse-A:["hex1","hex2",...]
    /// </summary>
    public static string BuildPulseCommand(string channel, string[] hexData)
    {
        var inner = string.Join(",", hexData.Select(h => $"\"{h}\""));
        return $"pulse-{channel}:[{inner}]";
    }

    public record WaveformDef(string Name, string[] HexData);
}
