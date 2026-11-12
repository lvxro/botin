namespace Botin;

/// <summary>Un lugar del mapa con lo que saqueaste ahí.</summary>
public sealed record PlaceStat(string Name, int Chests, int Matches);

/// <summary>Un lugar donde caíste (tu primer cofre) y cómo te fue esas partidas.</summary>
public sealed record LandingStat(string Name, double X, double Y, int Times, double AvgChests, int Best);

/// <summary>
/// Dónde saqueás: cada cofre con posición se asigna al lugar con nombre más cercano (a menos de 220 m), y el primer
/// cofre de cada partida dice dónde caíste. Solo cuentan las partidas analizadas con la versión que guarda posiciones.
/// </summary>
public sealed class LootPlaces
{
    public const string Outskirts = "Fuera de los lugares con nombre";

    public IReadOnlyList<(double X, double Y)> Points { get; init; } = Array.Empty<(double, double)>();
    public IReadOnlyList<PlaceStat> Places { get; init; } = Array.Empty<PlaceStat>();
    public IReadOnlyList<LandingStat> Landings { get; init; } = Array.Empty<LandingStat>();
    public int MatchesWithPosition { get; init; }
    public int MatchesTotal { get; init; }

    public static LootPlaces Build(IReadOnlyList<MatchRecord> matches)
    {
        var withPos = matches.Where(m => m.Chests.Any(c => c.X.HasValue)).ToList();
        var points = withPos.SelectMany(m => m.Chests).Where(c => c.X.HasValue).Select(c => ((double)c.X!.Value, (double)c.Y!.Value)).ToList();

        string NameAt(double x, double y) => MapService.NearestPoi(x, y)?.Name ?? Outskirts;

        var places = withPos
            .SelectMany(m => m.Chests.Where(c => c.X.HasValue).Select(c => (Match: m.SessionId, Name: NameAt(c.X!.Value, c.Y!.Value))))
            .GroupBy(e => e.Name)
            .Select(g => new PlaceStat(g.Key, g.Count(), g.Select(e => e.Match).Distinct().Count()))
            .OrderByDescending(p => p.Name == Outskirts ? -1 : p.Chests)
            .ToList();

        var landings = withPos
            .Select(m =>
            {
                var first = m.Chests.Where(c => c.X.HasValue).OrderBy(c => c.TimeSeconds).First();
                return (Match: m, Name: NameAt(first.X!.Value, first.Y!.Value), X: (double)first.X!.Value, Y: (double)first.Y!.Value);
            })
            .GroupBy(e => e.Name)
            .Select(g =>
            {
                // El marcador va en el lugar (si tiene nombre) o en el promedio de donde caíste.
                var poi = MapService.Pois.FirstOrDefault(p => p.Name == g.Key);
                return new LandingStat(g.Key, poi?.X ?? g.Average(e => e.X), poi?.Y ?? g.Average(e => e.Y),
                    g.Count(), g.Average(e => e.Match.ChestsOpened), g.Max(e => e.Match.ChestsOpened));
            })
            .OrderBy(l => l.Name == Outskirts)
            .ThenByDescending(l => l.Times)
            .ToList();

        return new LootPlaces
        {
            Points = points,
            Places = places,
            Landings = landings,
            MatchesWithPosition = withPos.Count,
            MatchesTotal = matches.Count,
        };
    }
}
