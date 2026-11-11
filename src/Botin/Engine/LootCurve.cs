namespace Botin;

/// <summary>Un minuto de la curva: cofres promedio por partida en ese minuto y % acumulado del total.</summary>
public sealed record MinuteStat(int Minute, double Average, int Matches, double CumulativePct);

/// <summary>
/// En qué momento de la partida abrís los cofres, sumando todas tus partidas. Se mide desde tu primer cofre
/// (prácticamente el aterrizaje): así sirve para todo el historial, sin depender de la hora del autobús.
/// </summary>
public sealed class LootCurve
{
    public IReadOnlyList<MinuteStat> Minutes { get; init; } = Array.Empty<MinuteStat>();
    public int MatchesUsed { get; init; }
    public double EarlyAverage { get; init; }
    public double EarlyPct { get; init; }
    public int? PeakMinute { get; init; }
    public double PeakAverage { get; init; }
    public int? HalfMinute { get; init; }
    public string? Tip { get; init; }

    public const int EarlyMinutes = 3;

    public static LootCurve Build(IReadOnlyList<MatchRecord> matches)
    {
        var used = matches.Where(m => m.Chests.Count > 0).ToList();
        if (used.Count < 3) return new LootCurve { MatchesUsed = used.Count };

        var counts = new Dictionary<int, int>();
        var alive = new Dictionary<int, int>();
        var total = 0;
        foreach (var m in used)
        {
            var t0 = m.Chests.Min(c => c.TimeSeconds);
            var lastChest = m.Chests.Max(c => c.TimeSeconds);
            var end = Math.Max(lastChest, m.DurationSeconds ?? 0) - t0;
            foreach (var c in m.Chests)
            {
                var minute = (int)((c.TimeSeconds - t0) / 60);
                counts[minute] = counts.GetValueOrDefault(minute) + 1;
                total++;
            }
            for (var minute = 0; minute * 60 <= end && minute < 40; minute++)
            {
                alive[minute] = alive.GetValueOrDefault(minute) + 1;
            }
        }

        // Solo minutos con suficientes partidas todavía en juego (si no, el promedio es ruido).
        var minAlive = Math.Max(3, (int)(used.Count * 0.15));
        var stats = new List<MinuteStat>();
        var cumulative = 0;
        for (var minute = 0; minute < 40; minute++)
        {
            var a = alive.GetValueOrDefault(minute);
            var n = counts.GetValueOrDefault(minute);
            cumulative += n;
            if (a < minAlive) break;
            stats.Add(new MinuteStat(minute, (double)n / a, a, 100.0 * cumulative / total));
        }
        if (stats.Count == 0) return new LootCurve { MatchesUsed = used.Count };

        var early = stats.Take(EarlyMinutes).Sum(s => s.Average);
        var earlyCount = stats.Take(EarlyMinutes).Sum(s => counts.GetValueOrDefault(s.Minute));
        var peak = stats.MaxBy(s => s.Average)!;
        var half = stats.FirstOrDefault(s => s.CumulativePct >= 50)?.Minute;

        string? tip = null;
        var first = stats[0].Average;
        if (stats.Count >= 4 && peak.Minute >= 2 && first < peak.Average * 0.6)
        {
            tip = $"Arrancás lento: en tu primer minuto abrís {Pretty.N(first, 1)} cofres, y tu mejor ritmo es {Pretty.N(peak.Average, 1)} en el minuto {peak.Minute}. Caer más cerca de cofres te suma desde el principio.";
        }
        else if (peak.Minute <= 1 && stats.Count >= 9)
        {
            var late = stats.Skip(7).Average(s => s.Average);
            tip = $"Arrancás fuerte: tu mejor minuto es el del aterrizaje y después vas bajando. Del minuto 7 en adelante abrís {Pretty.N(late, 1)} cofres por minuto; si querés sumar, seguí abriendo cofres en las rotaciones.";
        }
        else if (half is int h && h <= 3)
        {
            tip = $"Saqueás fuerte al caer: la mitad de tus cofres los abrís antes del minuto {h + 1}. Después de eso casi no abrís más; si querés sumar, seguí abriendo cofres en las rotaciones.";
        }
        else if (half is int h2)
        {
            tip = $"Saqueás parejo durante la partida: llegás a la mitad de tus cofres recién en el minuto {h2 + 1}.";
        }

        return new LootCurve
        {
            Minutes = stats,
            MatchesUsed = used.Count,
            EarlyAverage = early,
            EarlyPct = total > 0 ? 100.0 * earlyCount / total : 0,
            PeakMinute = peak.Minute,
            PeakAverage = peak.Average,
            HalfMinute = half,
            Tip = tip,
        };
    }
}
