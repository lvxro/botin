using System.Text.Json;
using System.Text.Json.Serialization;

namespace Botin;

// Mismo formato de data.json que Fortnite Chest Counter (LED-N, MIT): el historial se puede importar tal cual.

public sealed class ChestRecord
{
    public float TimeSeconds { get; set; }
    public string Kind { get; set; } = "";
    public string Blueprint { get; set; } = "";
    public bool Destroyed { get; set; }
    public List<string> Items { get; set; } = new();
    public List<string> TreasureItems { get; set; } = new();
    /// <summary>Posición en el mundo del juego (la del jugador al abrirlo: queda a pocos metros del cofre). Null en partidas viejas.</summary>
    public float? X { get; set; }
    public float? Y { get; set; }
}

public sealed class TreasureFind
{
    public string DisplayName => PrizeId != null ? TreasureNames.Friendly(PrizeId) : L.Name(Item);
    public string Item { get; set; } = "";
    public string RawItem { get; set; } = "";
    public string? PrizeId { get; set; }
    public bool FromGoldenChest { get; set; }
    public bool FromPrizeHandler { get; set; }
    public bool TimeExact { get; set; }
    public bool SourceDestroyed { get; set; }
    public string SourceLabel => SourceDestroyed ? $"{L.Name(SourceKind)} (destruido)" : L.Name(SourceKind);
    public float? ObtainedAtSeconds { get; set; }
    public string SourceKind { get; set; } = "";
    public string SourceBlueprint { get; set; } = "";
    public bool SourceIsChest { get; set; } = true;
    public bool SourceCertain { get; set; }
    public List<string> AlsoPossible { get; set; } = new();
    public string? Teammate { get; set; }
}

public sealed class MatchRecord
{
    public string SessionId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public DateTime AnalyzedAtUtc { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Playlist { get; set; }
    public string? GameVersion { get; set; }
    public string? PlayerName { get; set; }
    public string? EpicId { get; set; }
    public bool TrackedByUsername { get; set; }
    public string? NotTrackedOwner { get; set; }
    public bool IsReplayOwner { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }
    public int ChestsOpened { get; set; }
    public int AmmoBoxesOpened { get; set; }
    public int OtherContainersOpened { get; set; }
    public Dictionary<string, int> OtherInteractions { get; set; } = new();
    public int AnalyzerVersion { get; set; } = 1;
    public bool IsBattleRoyale { get; set; } = true;
    public List<ChestRecord> Chests { get; set; } = new();
    public List<TreasureFind> TreasureFinds { get; set; } = new();
    public List<TreasureFind> TeamTreasureFinds { get; set; } = new();
    /// <summary>Recorrido del jugador: [segundo, x, y] cada ~5 s, en coordenadas del mundo. Null en partidas viejas.</summary>
    public List<int[]>? Path { get; set; }

    /// <summary>Inicio de la partida en UTC.</summary>
    [JsonIgnore]
    public DateTime When
    {
        get
        {
            var d = MatchStartUtc ?? AnalyzedAtUtc;
            return d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
        }
    }
}

public sealed class TrackerData
{
    public int Version { get; set; } = 1;
    public int AnalyzerVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<MatchRecord> Matches { get; set; } = new();
    /// <summary>Clave = nombre|tamaño|fecha. Valor = id de partida, "skipped", "incomplete" o "error: ...".</summary>
    public Dictionary<string, string> ProcessedFiles { get; set; } = new();
}

public sealed class Config
{
    public string DemosFolder { get; set; } = "";
    public string? Profile { get; set; }

    public bool OverlayEnabled { get; set; } = true;
    /// <summary>"auto" = solo mientras Fortnite está abierto; "always" = siempre visible.</summary>
    public string OverlayMode { get; set; } = "auto";
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Opacidad del fondo del overlay (los números siempre se ven al 100 %).</summary>
    public double OverlayOpacity { get; set; } = 0.9;
    /// <summary>Compacto: solo el total; el resto aparece unos segundos al terminar cada partida.</summary>
    public bool OverlayCompact { get; set; }
    /// <summary>Versión del diseño del overlay con la que se guardaron estos ajustes (para migrarlos una vez).</summary>
    public int OverlayLookVersion { get; set; }
    public bool OverlayShowSession { get; set; } = true;
    public bool OverlayShowLast { get; set; } = true;
    public bool OverlayShowTreasures { get; set; } = true;

    public bool StartWithWindows { get; set; }
    /// <summary>Botín se cierra solo cuando cerrás Fortnite (después de contar la última partida) y vuelve a abrirse con Fortnite.</summary>
    public bool CloseWithFortnite { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool LiveUpdate { get; set; } = true;
    /// <summary>Bajar el mapa y los lugares de la temporada de fortnite-api.com.</summary>
    public bool DownloadMap { get; set; } = true;
    /// <summary>Consultar en GitHub si hay una versión nueva (no instala nada sin que el usuario toque Actualizar).</summary>
    public bool CheckUpdates { get; set; } = true;
    public bool Imported { get; set; }
}

/// <summary>Lee y escribe los JSON de %APPDATA%\Botin de forma atómica.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; }
    public string ConfigPath => Path.Combine(Folder, "config.json");
    public string DataPath => Path.Combine(Folder, "data.json");
    public string CsvPath => Path.Combine(Folder, "partidas.csv");
    public string ErrorLogPath => Path.Combine(Folder, "errores.log");

    public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Botin");
    public static string DefaultDemos() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    /// <summary>Historial de Fortnite Chest Counter, si estaba instalado (se ofrece importarlo).</summary>
    public static string ChestCounterData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounter", "data.json");

    public Store(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
    }

    public Config LoadConfig() => Load<Config>(ConfigPath) ?? new Config();
    public void SaveConfig(Config c) => Save(ConfigPath, c);
    public TrackerData LoadData() => Load<TrackerData>(DataPath) ?? new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
    public void SaveData(TrackerData d) => Save(DataPath, d);
    public static TrackerData? LoadDataFrom(string path) => Load<TrackerData>(path);

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch
        {
            try { File.Copy(path, path + $".danado-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
