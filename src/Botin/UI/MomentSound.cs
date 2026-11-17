using System.Media;

namespace Botin.UI;

/// <summary>
/// Sonido del momento de fin de partida, generado por código (sin archivos de audio): una sola pista sincronizada con la
/// animación. Tic de los cofres que sube de tono mientras cuentan, el sello de la nota (un acorde que crece con la nota),
/// un "blip" por logro y los acordes de meta cumplida e hito. Volumen bajo: suena encima del juego.
/// </summary>
internal static class MomentSound
{
    private const int Rate = 22050;
    private const double Master = 0.55;
    private static SoundPlayer? _player;
    private static MemoryStream? _stream;

    /// <summary>Tiempos en milisegundos desde que aparece el momento (los mismos que usa la animación).</summary>
    public sealed record Plan(int Chests, string? Grade, int Chips, double CountStart, double CountDuration, double Stamp,
        double ChipsStart, double ChipGap, double? GoalAt, double? MilestoneAt);

    public static void Play(Plan p)
    {
        try
        {
            var end = Math.Max(p.ChipsStart + p.Chips * p.ChipGap, Math.Max(p.GoalAt ?? 0, p.MilestoneAt ?? 0)) + 900;
            var buf = new float[(int)(Rate * end / 1000)];

            Tone(buf, 0, 520, 80, 0.10, Wave.Sine);

            // Tic de los cofres: cae en cada paso del número (con la misma curva de frenado que la animación).
            if (p.Chests > 0)
            {
                var step = p.Chests > 60 ? 3 : 2;
                for (var v = step; v <= p.Chests; v += step)
                {
                    var k = 1 - Math.Pow(1 - (double)v / p.Chests, 1.0 / 3);
                    Tone(buf, p.CountStart + k * p.CountDuration, 500 + k * 700, 35, 0.07, Wave.Triangle);
                }
            }

            // Sello de la nota: cuanto mejor la nota, más notas tiene el acorde.
            double[] chord = p.Grade switch
            {
                "B" => [659, 880],
                "A" => [659, 830, 988],
                "S" => [784, 988, 1175, 1568],
                "R" => [784, 988, 1175, 1568, 2093],
                _ => [440],
            };
            for (var i = 0; i < chord.Length; i++) Tone(buf, p.Stamp + i * 65, chord[i], 520, p.Grade is "S" or "R" ? 0.15 : 0.12, Wave.Sine);

            for (var i = 0; i < p.Chips; i++) Tone(buf, p.ChipsStart + i * p.ChipGap, 880 + i * 120, 70, 0.07, Wave.Sine);

            if (p.GoalAt is double g)
            {
                double[] notes = [523, 659, 784, 1047];
                for (var i = 0; i < notes.Length; i++) Tone(buf, g + i * 80, notes[i], 520, 0.13, Wave.Sine);
            }
            if (p.MilestoneAt is double m)
            {
                double[] notes = [1047, 1319, 1568, 2093];
                for (var i = 0; i < notes.Length; i++) Tone(buf, m + i * 60, notes[i], 520, 0.11, Wave.Sine);
            }

            var wav = ToWav(buf);
            _player?.Stop();
            _stream?.Dispose();
            _stream = new MemoryStream(wav);
            _player = new SoundPlayer(_stream);
            _player.Play();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo reproducir el sonido del momento", ex);
        }
    }

    private enum Wave { Sine, Triangle }

    private static void Tone(float[] buf, double atMs, double freq, double durMs, double vol, Wave wave)
    {
        var start = (int)(atMs / 1000 * Rate);
        var n = (int)(durMs / 1000 * Rate);
        for (var i = 0; i < n && start + i < buf.Length; i++)
        {
            if (start + i < 0) continue;
            var t = (double)i / Rate;
            var phase = freq * t;
            var s = wave == Wave.Sine ? Math.Sin(2 * Math.PI * phase) : 4 * Math.Abs(phase - Math.Floor(phase + 0.5)) - 1;
            // Ataque de 5 ms y caída exponencial.
            var env = Math.Min(1, t / 0.005) * Math.Exp(-5.5 * t / (durMs / 1000));
            buf[start + i] += (float)(s * env * vol);
        }
    }

    private static byte[] ToWav(float[] buf)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataLen = buf.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        foreach (var f in buf)
        {
            // Suave: tanh evita que los acordes que se pisan saturen.
            w.Write((short)(Math.Tanh(f * Master * 2.2) * 26000));
        }
        w.Flush();
        return ms.ToArray();
    }
}
