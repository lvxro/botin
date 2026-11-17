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
public sealed record PlaySession(DateTime StartUtc, DateTime EndUtc, int Matches, int Chests)
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    public double Minutes => (EndUtc - StartUtc).TotalMinutes;
    /// <summary>Día al que se cuenta (el de su comienzo: una noche que pasa de las 0 h sigue siendo esa noche).</summary>
    public DateTime Day => StartUtc.ToLocalTime().Date;

    /// <summary>Cofres por hora de un grupo de sesiones (null si jugaste menos de 10 min: el número no dice nada).</summary>
    public static double? PerHour(IEnumerable<PlaySession> sessions)
    {
        var list = sessions.ToList();
        var minutes = list.Sum(x => x.Minutes);
        return minutes < 10 ? null : list.Sum(x => x.Chests) / (minutes / 60);
    }

    public static List<PlaySession> Build(IEnumerable<MatchRecord> matches)
    {
        var list = new List<PlaySession>();
        DateTime? start = null, end = null;
        int n = 0, chests = 0;
        foreach (var m in matches.OrderBy(m => m.When))
        {
            var s = m.When;
            var e = s.AddSeconds(Math.Max(60, m.DurationSeconds ?? 0));
            if (start != null && s - end!.Value <= Gap)
            {
                if (e > end) end = e;
                n++;
                chests += m.ChestsOpened;
                continue;
            }
            if (start != null) list.Add(new PlaySession(start.Value, end!.Value, n, chests));
            start = s;
            end = e;
            n = 1;
            chests = m.ChestsOpened;
        }
        if (start != null) list.Add(new PlaySession(start.Value, end!.Value, n, chests));
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

/// <summary>
/// Meta del día. Un día de juego termina cuando dejás de jugar <see cref="Break"/> o más: así una noche larga que pasa la
/// medianoche cuenta como un solo día. La meta automática es tu día típico: la mediana de tus últimos 7 días de juego.
/// </summary>
public static class GameDay
{
    public static readonly TimeSpan Break = TimeSpan.FromHours(5);

    private static DateTime End(MatchRecord m) => m.When.AddSeconds(m.DurationSeconds ?? 0);

    /// <summary>Parte las partidas (ordenadas) en días de juego.</summary>
    public static List<List<MatchRecord>> Split(IReadOnlyList<MatchRecord> ordered)
    {
        var days = new List<List<MatchRecord>>();
        foreach (var m in ordered)
        {
            if (days.Count == 0 || m.When - End(days[^1][^1]) >= Break) days.Add(new List<MatchRecord>());
            days[^1].Add(m);
        }
        return days;
    }

    /// <summary>Cofres del día de juego actual (con la partida en curso) y la meta automática, si hay historia suficiente.</summary>
    public static (int Chests, int? Auto, DateTime? StartUtc) Compute(IReadOnlyList<MatchRecord> ordered, bool inMatch, int liveChests, DateTime nowUtc)
    {
        var days = Split(ordered);
        var current = days.Count > 0 && (inMatch || nowUtc - End(days[^1][^1]) < Break) ? days[^1] : null;
        var chests = (current?.Sum(m => m.ChestsOpened) ?? 0) + (inMatch ? liveChests : 0);

        var past = days.Where(d => !ReferenceEquals(d, current)).TakeLast(7).Select(d => d.Sum(m => m.ChestsOpened)).Where(n => n > 0).OrderBy(n => n).ToList();
        int? auto = null;
        if (past.Count >= 3)
        {
            var median = past.Count % 2 == 1 ? past[past.Count / 2] : (past[past.Count / 2 - 1] + past[past.Count / 2]) / 2.0;
            auto = Math.Max(100, (int)Math.Round(median / 50) * 50);
        }
        return (chests, auto, current?[0].When);
    }
}

/// <summary>Un logro de la partida para el momento del overlay. Tone: gold, teal, violet o fire; Icon: bolt, star, fire, crown, check o chest.</summary>
public sealed record Accolade(string Tone, string Icon, string Text);

/// <summary>Nota de una partida (C, B, A, S o R de récord) y sus logros.</summary>
public sealed record MatchGrade(string Grade, string Name, IReadOnlyList<Accolade> Accolades);

/// <summary>
/// Nota personal de cada partida: se compara con tus últimas 150 (no con un número fijo), así una S siempre cuesta.
/// C = menos que tu mitad, B = mitad de arriba, A = tu 25 % mejor, S = tu 10 % mejor, R = récord.
/// </summary>
public static class Moments
{
    private const int History = 150;

    private static int Early(MatchRecord m)
    {
        if (m.Chests.Count == 0) return 0;
        var first = m.Chests.Min(c => c.TimeSeconds);
        return m.Chests.Count(c => c.TimeSeconds - first <= 180);
    }

    private static int Quantile(IReadOnlyList<int> sorted, double p) => sorted[(int)(p * (sorted.Count - 1))];

    /// <summary>Nota y logros de <paramref name="m"/> comparada con las partidas anteriores (null si hay menos de 10).</summary>
    public static MatchGrade? For(MatchRecord m, IReadOnlyList<MatchRecord> before)
    {
        var played = before.Where(x => x.ChestsOpened > 0).TakeLast(History).ToList();
        if (played.Count < 10) return null;
        var hist = played.Select(x => x.ChestsOpened).OrderBy(x => x).ToList();
        var prevMax = before.Max(x => x.ChestsOpened);
        var c = m.ChestsOpened;
        var (grade, name) = c > prevMax ? ("R", "Récord")
            : c >= Quantile(hist, 0.9) ? ("S", "Legendaria")
            : c >= Quantile(hist, 0.75) ? ("A", "Épica")
            : c >= Quantile(hist, 0.5) ? ("B", "Buena")
            : ("C", "Normal");

        var list = new List<Accolade>();
        if (grade == "R") list.Add(new Accolade("gold", "crown", $"Nuevo récord · antes {Pretty.N(prevMax)}"));

        var early = Early(m);
        var earlyHist = played.Where(x => x.Chests.Count > 0).Select(Early).OrderBy(x => x).ToList();
        if (earlyHist.Count >= 10 && early > 0)
        {
            if (early >= Quantile(earlyHist, 0.9)) list.Add(new Accolade("gold", "bolt", $"Aterrizaje dorado · {early} en 3 min"));
            else if (early >= Quantile(earlyHist, 0.75)) list.Add(new Accolade("teal", "bolt", $"Buen arranque · {early} en 3 min"));
        }

        var streak = StreakEnding(before.Append(m).ToList());
        if (streak >= 2) list.Add(new Accolade("fire", "fire", $"Racha x{streak} sobre tu promedio"));

        var t = m.TreasureFinds.Count;
        if (t > 0) list.Add(new Accolade("violet", "star", t == 1 ? "+1 tesoro" : $"+{t} tesoros"));

        return new MatchGrade(grade, name, list.Take(3).ToList());
    }

    /// <summary>Partidas seguidas, contando desde la última, por encima de tu promedio.</summary>
    public static int StreakEnding(IReadOnlyList<MatchRecord> ordered)
    {
        if (ordered.Count < 2) return 0;
        var avg = ordered.Average(x => x.ChestsOpened);
        var n = 0;
        for (var i = ordered.Count - 1; i >= 0 && ordered[i].ChestsOpened > avg; i--) n++;
        return n;
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
    /// <summary>Minutos jugados desde que abriste Botín (con la partida en curso si la hay).</summary>
    public double PlaySessionNow { get; init; }
    public PlaySession? LongestSession { get; init; }
    /// <summary>Cofres por hora jugada (con el lobby entre partidas): hoy, últimos 7 días, desde que abriste Botín y tu mejor día.</summary>
    public double? RateToday { get; init; }
    public double? RateWeek { get; init; }
    public double? RateSession { get; init; }
    public (DateTime Day, double Rate)? BestRateDay { get; init; }
    /// <summary>Cofres de esta temporada (con la partida en curso), para la chance del token.</summary>
    public int SeasonChests { get; init; }
    /// <summary>Cofres del día de juego actual (ver <see cref="GameDay"/>), la meta que se usa y la automática.</summary>
    public int GoalChests { get; init; }
    public int Goal { get; init; }
    public int? GoalAuto { get; init; }
    /// <summary>Nota y logros de la última partida, y la racha actual de partidas arriba de tu promedio.</summary>
    public MatchGrade? LastGrade { get; init; }
    public int Streak { get; init; }
    /// <summary>Minutos jugados y cofres por hora en el día de juego actual (con el lobby entre partidas).</summary>
    public double GoalMinutes { get; init; }
    public double? GoalRate { get; init; }
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
                    sessions[^1] = sessions[^1] with { EndUtc = now, Matches = sessions[^1].Matches + 1, Chests = sessions[^1].Chests + (liveChests ?? 0) };
                }
                else
                {
                    sessions.Add(new PlaySession(ls, now, 1, liveChests ?? 0));
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
                var rate = PlaySession.PerHour(g);
                var spans = string.Join(", ", g.Take(3).Select(x => $"{x.StartUtc.ToLocalTime():HH:mm}–{x.EndUtc.ToLocalTime():HH:mm}")) + (ns > 3 ? "…" : "");
                return new ChartPoint((int)Math.Round(g.Sum(x => x.Minutes)), 0,
                    $"{label} · {nm} {(nm == 1 ? "partida" : "partidas")}{(rate is double r ? $" · {Pretty.N(r)} cofres/h" : "")} · {(ns == 1 ? "de" : $"{ns} sesiones:")} {spans}",
                    t.InMatch && g.Key == DateTime.Today, ReferenceEquals(g, longestDay) && playDays.Count > 1, g.Key.ToString("ddd d", L.Culture));
            }).ToList();
            var weekStart = DateTime.Today.AddDays(-6);
            // "Esta sesión" es desde que abriste Botín: su tiempo y su ritmo salen solo de esas partidas (y la que está en curso).
            var appSessions = PlaySession.Build(session);
            if (liveStart is DateTime ls2)
            {
                if (appSessions.Count > 0 && ls2 - appSessions[^1].EndUtc <= PlaySession.Gap)
                    appSessions[^1] = appSessions[^1] with { EndUtc = now, Matches = appSessions[^1].Matches + 1, Chests = appSessions[^1].Chests + (liveChests ?? 0) };
                else
                    appSessions.Add(new PlaySession(ls2, now, 1, liveChests ?? 0));
            }

            var gameDay = GameDay.Compute(matches, t.InMatch, liveChests ?? 0, now);
            var dayStart = gameDay.StartUtc ?? liveStart;
            var daySessions = dayStart is DateTime ds ? sessions.Where(x => x.EndUtc >= ds).ToList() : new List<PlaySession>();

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
                PlaySessionNow = appSessions.Sum(x => x.Minutes),
                LongestSession = sessions.MaxBy(x => x.Minutes),
                RateToday = PlaySession.PerHour(sessions.Where(x => x.Day == DateTime.Today)),
                RateWeek = PlaySession.PerHour(sessions.Where(x => x.Day >= weekStart)),
                RateSession = PlaySession.PerHour(appSessions),
                // Mejor día: solo días con al menos 1 h jugada, para que una partida suelta no gane.
                BestRateDay = sessions.GroupBy(x => x.Day).Where(g => g.Sum(x => x.Minutes) >= 60)
                    .Select(g => (Day: g.Key, Rate: PlaySession.PerHour(g) ?? 0)).OrderByDescending(x => x.Rate).Cast<(DateTime, double)?>().FirstOrDefault(),
                SeasonChests = SeasonChestCount(matches) + (liveChests ?? 0),
                GoalChests = gameDay.Chests,
                GoalAuto = gameDay.Auto,
                GoalMinutes = daySessions.Sum(x => x.Minutes),
                LastGrade = matches.Count > 0 ? Moments.For(matches[^1], matches.Take(matches.Count - 1).ToList()) : null,
                Streak = Moments.StreakEnding(matches),
                GoalRate = PlaySession.PerHour(daySessions),
                Goal = c.DailyGoal is int g && g > 0 ? g : gameDay.Auto ?? 500,
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
