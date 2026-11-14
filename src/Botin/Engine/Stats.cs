namespace Botin;

public static class Pretty
{
    public static string Playlist(string? playlist)
    {
        if (string.IsNullOrEmpty(playlist)) return "Modo desconocido";
        var p = playlist.ToLowerInvariant();
        var parts = new List<string>();
        if (p.Contains("solo")) parts.Add("Solo");
        else if (p.Contains("duo")) parts.Add("Dúos");
        else if (p.Contains("trio")) parts.Add("Tríos");
        else if (p.Contains("squad")) parts.Add("Escuadrones");
        if (p.Contains("nobuild")) parts.Add("Cero construcción");
        if (p.Contains("habanero") || p.Contains("ranked")) parts.Add("Clasificatoria");
        return parts.Count > 0 ? string.Join(" · ", parts) : playlist.Replace("Playlist_", "");
    }

    public static string Placement(MatchRecord m)
    {
        if (m.Placement is not int place || place <= 0) return "puesto ?";
        if (place == 1) return "¡Victoria!";
        return $"#{place}";
    }

    public static string Clock(float seconds)
    {
        var s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    public static string N(double n, int decimals = 0) => n.ToString(decimals == 0 ? "N0" : "N" + decimals, L.Culture);

    public static string Relative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return $"Hoy {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Ayer {local:HH:mm}";
        if (local > today.AddDays(-6)) return local.ToString("dddd HH:mm", L.Culture) is var s ? char.ToUpper(s[0]) + s[1..] : "";
        return local.ToString("dd MMM · HH:mm", L.Culture);
    }

    /// <summary>135 → "2 h 15 min", 120 → "2 h", 45 → "45 min".</summary>
    public static string Minutes(double minutes)
    {
        var m = (int)Math.Round(minutes);
        if (m < 60) return $"{m} min";
        return m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }
}

/// <summary>
/// Una sesión de juego: partidas seguidas con menos de 30 min entre el final de una y el comienzo de la siguiente.
/// Va desde que empieza la primera hasta que termina la última, así que incluye el lobby y las colas entre partidas.
/// </summary>
public sealed record PlaySession(DateTime StartUtc, DateTime EndUtc, int Matches)
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    public double Minutes => (EndUtc - StartUtc).TotalMinutes;
    /// <summary>Día al que se cuenta (el de su comienzo: una noche que pasa de las 0 h sigue siendo esa noche).</summary>
    public DateTime Day => StartUtc.ToLocalTime().Date;

    public static List<PlaySession> Build(IEnumerable<MatchRecord> matches)
    {
        var list = new List<PlaySession>();
        DateTime? start = null, end = null;
        var n = 0;
        foreach (var m in matches.OrderBy(m => m.When))
        {
            var s = m.When;
            var e = s.AddSeconds(Math.Max(60, m.DurationSeconds ?? 0));
            if (start != null && s - end!.Value <= Gap)
            {
                if (e > end) end = e;
                n++;
                continue;
            }
            if (start != null) list.Add(new PlaySession(start.Value, end!.Value, n));
            start = s;
            end = e;
            n = 1;
        }
        if (start != null) list.Add(new PlaySession(start.Value, end!.Value, n));
        return list;
    }
}

public sealed record ChestTypeSlice(string Name, int Count, string ColorKey);
/// <summary>
/// Token de la tienda pirata: Epic publicó que sale en 1 de cada 137.500 cofres (más durante las Treasure Hours).
/// Cada cofre es un sorteo aparte; la chance acumulada es la de que te haya salido al menos una vez esta temporada.
/// </summary>
public static class TokenOdds
{
    public const int OneIn = 137_500;
    private const double PerChest = 1.0 / OneIn;

    public static double Chance(int chests) => 1 - Math.Pow(1 - PerChest, chests);

    /// <summary>Cofres para llegar a una chance acumulada <paramref name="p"/> (0–1).</summary>
    public static int ChestsFor(double p) => (int)Math.Ceiling(Math.Log(1 - p) / Math.Log(1 - PerChest));

    /// <summary>"++Fortnite+Release-43.10" → 43: la temporada es la versión mayor.</summary>
    public static int? Season(string? version)
    {
        if (version == null) return null;
        var i = version.LastIndexOf('-');
        var s = i >= 0 ? version[(i + 1)..] : version;
        var dot = s.IndexOf('.');
        return int.TryParse(dot > 0 ? s[..dot] : s, out var major) ? major : null;
    }
}

/// <summary>Una barra: cofres (o minutos, en el gráfico de tiempo).</summary>
public sealed record ChartPoint(int Value, int Treasures, string Label, bool Live, bool Record = false, string? Axis = null);

public sealed record MatchRow(
    string When, string Mode, string Place, bool Win, string Kills, int Chests, double ChestsRatio,
    int Treasures, string Duration, string TreasureText, bool IsRecord, MatchRecord Match);

/// <summary>Todo lo que muestra la app en un momento dado, calculado de una vez (inmutable, fácil de pasar a la UI).</summary>
public sealed class Snapshot
{
    public TrackerState State { get; init; }
    public (int Done, int Total)? Progress { get; init; }
    public bool InMatch { get; init; }
    public int? LiveChests { get; init; }
    public bool FortniteRunning { get; init; }

    public string? ProfileName { get; init; }
    public IReadOnlyList<Profile> Profiles { get; init; } = Array.Empty<Profile>();

    public int TotalChests { get; init; }
    public int Matches { get; init; }
    public double Average { get; init; }
    /// <summary>Promedio de las últimas 10 partidas (para la tendencia).</summary>
    public double? RecentAverage { get; init; }
    public int Treasures { get; init; }
    public int Wins { get; init; }
    public int Kills { get; init; }
    public int Today { get; init; }
    public int TodayMatches { get; init; }

    public int SessionChests { get; init; }
    public int SessionMatches { get; init; }

    public MatchRecord? Last { get; init; }
    public MatchRecord? Best { get; init; }
    public int? ChestsPerTreasure { get; init; }

    public IReadOnlyList<ChartPoint> Chart { get; init; } = Array.Empty<ChartPoint>();
    /// <summary>Cofres por día (últimos 30 días con partidas).</summary>
    public IReadOnlyList<ChartPoint> Daily { get; init; } = Array.Empty<ChartPoint>();
    /// <summary>Minutos jugados por día (últimos 30 días con partidas).</summary>
    public IReadOnlyList<ChartPoint> PlayDaily { get; init; } = Array.Empty<ChartPoint>();
    public double PlayToday { get; init; }
    public double PlayWeek { get; init; }
    /// <summary>Minutos de la sesión de juego en curso (o la última de hoy), con la partida en curso si la hay.</summary>
    public double PlaySessionNow { get; init; }
    public PlaySession? LongestSession { get; init; }
    /// <summary>Cofres de esta temporada (con la partida en curso), para la chance del token.</summary>
    public int SeasonChests { get; init; }
    public LootCurve Curve { get; init; } = new();
    public LootPlaces Places { get; init; } = new();

    public static string ColorKey(string kind) => ChestKinds.Canonical(kind) switch
    {
        ChestKinds.Rare => "Rare",
        ChestKinds.Buried => "Buried",
        ChestKinds.Extraction or ChestKinds.PosterChest => "Sprite",
        ChestKinds.Normal => "Normal",
        _ => "Other",
    };
    public IReadOnlyList<ChestTypeSlice> Types { get; init; } = Array.Empty<ChestTypeSlice>();
    public IReadOnlyList<MatchRow> Rows { get; init; } = Array.Empty<MatchRow>();
    public IReadOnlyList<ActivityItem> Activity { get; init; } = Array.Empty<ActivityItem>();

    public static readonly DateTime SessionStartUtc = DateTime.UtcNow;

    public static bool IsThisSession(MatchRecord m) => m.When.AddSeconds(m.DurationSeconds ?? 0) >= SessionStartUtc.AddMinutes(-2);

    public static Snapshot Build(Tracker t, Config c, bool fortniteRunning)
    {
        lock (t.Sync)
        {
            var all = t.Data;
            var profiles = Botin.Profiles.List(all);
            var current = Botin.Profiles.Current(all, c);
            var matches = Botin.Profiles.View(all, c).Matches.OrderBy(m => m.When).ToList();
            var live = t.LiveMatch;
            var liveChests = live?.ChestsOpened;

            var total = matches.Sum(m => m.ChestsOpened);
            var treasures = matches.Sum(m => m.TreasureFinds.Count);
            var session = matches.Where(IsThisSession).ToList();
            var today = matches.Where(m => m.When.ToLocalTime().Date == DateTime.Today).ToList();
            var best = matches.OrderByDescending(m => m.ChestsOpened).ThenBy(m => m.When).FirstOrDefault();
            var max = Math.Max(1, matches.Count == 0 ? 1 : matches.Max(m => m.ChestsOpened));

            var chart = matches.TakeLast(t.InMatch ? 29 : 30)
                .Select(m => new ChartPoint(m.ChestsOpened, m.TreasureFinds.Count, $"{Pretty.Relative(m.When)} · {Pretty.Playlist(m.Playlist)} · {Pretty.Placement(m)}", false, ReferenceEquals(m, best) && m.ChestsOpened > 0))
                .ToList();
            if (t.InMatch) chart.Add(new ChartPoint(liveChests ?? 0, live?.TreasureFinds.Count ?? 0, "Partida en curso", true));

            var days = matches.GroupBy(m => m.When.ToLocalTime().Date).OrderBy(g => g.Key).TakeLast(30).ToList();
            var bestDay = days.Count == 0 ? null : days.MaxBy(g => g.Sum(m => m.ChestsOpened));
            var daily = days.Select(g =>
            {
                var n = g.Count();
                var label = g.Key.ToString("dddd d MMM", L.Culture);
                label = char.ToUpper(label[0]) + label[1..];
                var chests = g.Sum(m => m.ChestsOpened) + (t.InMatch && g.Key == DateTime.Today ? liveChests ?? 0 : 0);
                return new ChartPoint(chests, g.Sum(m => m.TreasureFinds.Count), $"{label} · {n} {(n == 1 ? "partida" : "partidas")}", false, ReferenceEquals(g, bestDay), g.Key.ToString("ddd d", L.Culture));
            }).ToList();

            var sessions = PlaySession.Build(matches);
            var now = DateTime.UtcNow;
            // La partida en curso estira la sesión actual (o abre una nueva) hasta ahora.
            var liveStart = t.InMatch ? now.AddSeconds(-(live?.DurationSeconds ?? 0)) : (DateTime?)null;
            if (liveStart is DateTime ls)
            {
                if (sessions.Count > 0 && ls - sessions[^1].EndUtc <= PlaySession.Gap)
                {
                    sessions[^1] = sessions[^1] with { EndUtc = now, Matches = sessions[^1].Matches + 1 };
                }
                else
                {
                    sessions.Add(new PlaySession(ls, now, 1));
                }
            }
            var playDays = sessions.GroupBy(x => x.Day).OrderBy(g => g.Key).TakeLast(30).ToList();
            var longestDay = playDays.Count == 0 ? null : playDays.MaxBy(g => g.Sum(x => x.Minutes));
            var playDaily = playDays.Select(g =>
            {
                var label = g.Key.ToString("dddd d MMM", L.Culture);
                label = char.ToUpper(label[0]) + label[1..];
                var ns = g.Count();
                var nm = g.Sum(x => x.Matches);
                var spans = string.Join(", ", g.Take(3).Select(x => $"{x.StartUtc.ToLocalTime():HH:mm}–{x.EndUtc.ToLocalTime():HH:mm}")) + (ns > 3 ? "…" : "");
                return new ChartPoint((int)Math.Round(g.Sum(x => x.Minutes)), 0,
                    $"{label} · {nm} {(nm == 1 ? "partida" : "partidas")} · {(ns == 1 ? "de" : $"{ns} sesiones:")} {spans}",
                    t.InMatch && g.Key == DateTime.Today, ReferenceEquals(g, longestDay) && playDays.Count > 1, g.Key.ToString("ddd d", L.Culture));
            }).ToList();
            var weekStart = DateTime.Today.AddDays(-6);
            var playNow = sessions.LastOrDefault(x => now - x.EndUtc <= PlaySession.Gap || x.Day == DateTime.Today);

            var types = matches.SelectMany(m => m.Chests)
                .GroupBy(ch => ChestKinds.Canonical(ch.Kind))
                .Select(g => new ChestTypeSlice(L.Name(g.Key), g.Count(), ColorKey(g.Key)))
                .OrderByDescending(s => s.Count)
                .ToList();

            var rows = matches.AsEnumerable().Reverse().Take(200).Select(m => new MatchRow(
                Pretty.Relative(m.When),
                Pretty.Playlist(m.Playlist),
                Pretty.Placement(m),
                m.Placement == 1,
                m.Eliminations?.ToString() ?? "–",
                m.ChestsOpened,
                (double)m.ChestsOpened / max,
                m.TreasureFinds.Count,
                m.DurationSeconds is int d ? $"{d / 60} min" : "",
                string.Join("\n", m.TreasureFinds.Select(f => $"★ {f.DisplayName}" + (f.ObtainedAtSeconds is float s ? $" ({Pretty.Clock(s)})" : ""))),
                best != null && ReferenceEquals(m, best) && m.ChestsOpened > 0,
                m)).ToList();

            return new Snapshot
            {
                State = t.State,
                Progress = t.Progress,
                InMatch = t.InMatch,
                LiveChests = liveChests,
                FortniteRunning = fortniteRunning,
                ProfileName = current?.Name,
                Profiles = profiles,
                TotalChests = total,
                Matches = matches.Count,
                Average = matches.Count > 0 ? (double)total / matches.Count : 0,
                RecentAverage = matches.Count >= 15 ? matches.TakeLast(10).Average(m => m.ChestsOpened) : null,
                Treasures = treasures,
                Wins = matches.Count(m => m.Placement == 1),
                Kills = matches.Sum(m => m.Eliminations ?? 0),
                Today = today.Sum(m => m.ChestsOpened),
                TodayMatches = today.Count,
                SessionChests = session.Sum(m => m.ChestsOpened),
                SessionMatches = session.Count,
                Last = matches.LastOrDefault(),
                Best = best,
                ChestsPerTreasure = treasures > 0 ? (int)Math.Round((double)total / treasures) : null,
                Chart = chart,
                Daily = daily,
                PlayDaily = playDaily,
                PlayToday = sessions.Where(x => x.Day == DateTime.Today).Sum(x => x.Minutes),
                PlayWeek = sessions.Where(x => x.Day >= weekStart).Sum(x => x.Minutes),
                PlaySessionNow = playNow?.Minutes ?? 0,
                LongestSession = sessions.MaxBy(x => x.Minutes),
                SeasonChests = SeasonChestCount(matches) + (liveChests ?? 0),
                Curve = LootCurve.Build(matches),
                Places = LootPlaces.Build(matches),
                Types = types,
                Rows = rows,
                Activity = t.Activity,
            };
        }
    }

    /// <summary>Cofres de la temporada más nueva que aparece en tus partidas (las partidas sin versión no cuentan).</summary>
    private static int SeasonChestCount(IReadOnlyList<MatchRecord> matches)
    {
        var season = matches.Select(m => TokenOdds.Season(m.GameVersion)).Max();
        return season == null ? 0 : matches.Where(m => TokenOdds.Season(m.GameVersion) == season).Sum(m => m.ChestsOpened);
    }

    public static string Csv(TrackerData d)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("fecha;modo;puesto;eliminaciones;cofres;tesoros;duracion_min;jugador");
        foreach (var m in d.Matches.OrderBy(m => m.When))
        {
            sb.AppendLine(string.Join(';',
                m.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Pretty.Playlist(m.Playlist),
                m.Placement?.ToString() ?? "",
                m.Eliminations?.ToString() ?? "",
                m.ChestsOpened,
                m.TreasureFinds.Count,
                m.DurationSeconds is int s ? (s / 60).ToString() : "",
                (m.PlayerName ?? "").Replace(';', ',')));
        }
        return sb.ToString();
    }
}
