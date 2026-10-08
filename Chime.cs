using System.IO;
using System.Media;

namespace WinNotch;

/// <summary>
/// A soft three-note chime (like a kitchen timer bell), generated in code so there's no sound file to ship.
/// Plays through your normal speakers/headphones at your current volume.
/// </summary>
internal static class Chime
{
    private static SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            if (_player == null)
            {
                _player = new SoundPlayer(new MemoryStream(BuildWav()));
                _player.Load();
            }
            _player.Play(); // plays in the background
        }
        catch
        {
            // No audio device: fall back to the Windows alert sound
            try { SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    public static void Stop()
    {
        try { _player?.Stop(); } catch { }
    }

    private static byte[] BuildWav()
    {
        const int sampleRate = 44100;
        const double length = 1.4; // seconds
        int samples = (int)(sampleRate * length);
        var mix = new double[samples];

        // C6, E6, G6 - each note rings like a bell and fades out
        var notes = new (double Freq, double Start)[] { (1046.5, 0.00), (1318.5, 0.16), (1568.0, 0.32) };
        foreach (var (freq, start) in notes)
        {
            int first = (int)(start * sampleRate);
            for (int i = first; i < samples; i++)
            {
                double t = (i - first) / (double)sampleRate;
                double envelope = Math.Exp(-t * 4.0) * Math.Min(1, t * 200); // quick attack, smooth fade
                double tone = Math.Sin(2 * Math.PI * freq * t)
                            + 0.35 * Math.Sin(2 * Math.PI * freq * 2 * t)  // a little shimmer
                            + 0.15 * Math.Sin(2 * Math.PI * freq * 3 * t);
                mix[i] += tone * envelope;
            }
        }

        // Normalise to a comfortable level and write a standard 16-bit mono WAV
        double peak = mix.Max(x => Math.Abs(x));
        double gain = peak > 0 ? 0.6 / peak : 0;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (var s in mix) w.Write((short)(s * gain * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
