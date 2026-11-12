using System.Text.Json;

namespace Botin;

/// <summary>Un lugar con nombre del mapa de la temporada (coordenadas del mundo del juego).</summary>
public sealed record MapPoi(string Name, double X, double Y);

/// <summary>
/// Mapa de la temporada: imagen y lugares con nombre, bajados de fortnite-api.com (API pública y gratuita) y guardados
/// en %APPDATA%\Botin\map. Es la única conexión de Botín a internet y se puede apagar en Ajustes.
/// </summary>
public static class MapService
{
    private const string ApiUrl = "https://fortnite-api.com/v1/map?language=es";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(3);

    /// <summary>Tamaño de la imagen del mapa de fortnite-api.com (cuadrada).</summary>
    public const double ImageSize = 2048;

    // Coordenadas del mundo → píxeles de la imagen de 2048 px. Calibrado con los 17 lugares de la temporada
    // (error de 1–2 px): la X del mundo va hacia la derecha y la Y hacia abajo.
    private const double OffsetX = 977.5, ScaleX = 0.006599, OffsetY = 1016.0, ScaleY = 0.006612;

    public static (double Px, double Py) ToImage(double x, double y) => (OffsetX + ScaleX * x, OffsetY + ScaleY * y);

    public static string Folder { get; private set; } = "";
    public static string ImagePath => Path.Combine(Folder, "map.png");
    private static string PoisPath => Path.Combine(Folder, "pois.json");

    public static IReadOnlyList<MapPoi> Pois { get; private set; } = Array.Empty<MapPoi>();
    public static bool HasImage => File.Exists(ImagePath);

    /// <summary>Se dispara cuando terminó una descarga (en otro hilo).</summary>
    public static event Action? Updated;

    public static void Init(string dataFolder)
    {
        Folder = Path.Combine(dataFolder, "map");
        Directory.CreateDirectory(Folder);
        LoadCache();
    }

    private static void LoadCache()
    {
        try
        {
            if (File.Exists(PoisPath))
            {
                Pois = JsonSerializer.Deserialize<List<MapPoi>>(File.ReadAllText(PoisPath)) ?? new List<MapPoi>();
            }
        }
        catch
        {
            Pois = Array.Empty<MapPoi>();
        }
    }

    /// <summary>Baja el mapa si no está o tiene más de 3 días (cambia con cada temporada). No bloquea.</summary>
    public static void RefreshInBackground(bool force = false)
    {
        var stale = !HasImage || !File.Exists(PoisPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(PoisPath) > RefreshEvery;
        if (!force && !stale) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Botin/1.0");
                using var doc = JsonDocument.Parse(await http.GetStringAsync(ApiUrl));
                var data = doc.RootElement.GetProperty("data");

                var pois = new List<MapPoi>();
                foreach (var p in data.GetProperty("pois").EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var loc = p.GetProperty("location");
                    pois.Add(new MapPoi(Pretty(name), loc.GetProperty("x").GetDouble(), loc.GetProperty("y").GetDouble()));
                }

                var imageUrl = data.GetProperty("images").GetProperty("blank").GetString();
                if (imageUrl != null)
                {
                    var bytes = await http.GetByteArrayAsync(imageUrl);
                    var tmp = ImagePath + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes);
                    File.Move(tmp, ImagePath, overwrite: true);
                }
                await File.WriteAllTextAsync(PoisPath, JsonSerializer.Serialize(pois));
                Pois = pois;
                Updated?.Invoke();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("No se pudo bajar el mapa de fortnite-api.com", ex);
            }
        });
    }

    /// <summary>"BAHÍA BOTÍN" / "bahía botín" → "Bahía Botín" (la API a veces los manda en mayúsculas).</summary>
    private static string Pretty(string name)
    {
        name = name.Trim();
        if (name.Any(char.IsLower)) return name;
        var lower = name.ToLower(L.Culture);
        return string.Join(' ', lower.Split(' ').Select(w => w.Length > 0 ? char.ToUpper(w[0], L.Culture) + w[1..] : w));
    }

    /// <summary>Lugar con nombre más cercano a una posición, si está a menos de ~220 m del centro del lugar.</summary>
    public static MapPoi? NearestPoi(double x, double y, double maxDistance = 22000)
    {
        MapPoi? best = null;
        var bestD = double.MaxValue;
        foreach (var p in Pois)
        {
            var d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
            if (d < bestD)
            {
                bestD = d;
                best = p;
            }
        }
        return best != null && Math.Sqrt(bestD) <= maxDistance ? best : null;
    }
}
