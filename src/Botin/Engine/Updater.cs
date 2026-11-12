using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Botin;

/// <summary>Una versión publicada en GitHub más nueva que la que está corriendo.</summary>
public sealed record UpdateInfo(Version Version, string Url, long Size);

/// <summary>
/// Actualizaciones: consulta la última Release de github.com/lvxro/botin y, si el usuario toca "Actualizar", baja el
/// Botin.exe nuevo, lo pone en lugar del actual (el viejo queda como Botin.exe.old y se borra al abrir) y reinicia.
/// Los datos y la configuración están en %APPDATA%\Botin, así que no se tocan.
/// </summary>
public static class Updater
{
    private const string LatestUrl = "https://api.github.com/repos/lvxro/botin/releases/latest";
    private const string AssetName = "Botin.exe";

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));
    public static string CurrentText => Current.ToString(3);

    public static UpdateInfo? Available { get; private set; }

    /// <summary>Se dispara (en otro hilo) cuando se encuentra una versión nueva.</summary>
    public static event Action? Found;

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Botin/{CurrentText}");
        return http;
    }

    public static async Task CheckAsync()
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(20));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestUrl));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var latest)) return;
            latest = Normalize(latest);
            if (latest <= Current) return;

            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.GetProperty("browser_download_url").GetString();
                if (url == null || !url.StartsWith("https://github.com/lvxro/botin/releases/download/", StringComparison.OrdinalIgnoreCase)) return;
                Available = new UpdateInfo(latest, url, a.GetProperty("size").GetInt64());
                Found?.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo consultar si hay una versión nueva", ex);
        }
    }

    /// <summary>
    /// Baja la versión nueva y la deja en lugar del exe actual. Devuelve la ruta del exe para reiniciar.
    /// Tira una excepción con un mensaje para el usuario si algo sale mal (el exe actual queda como estaba).
    /// </summary>
    public static async Task<string> DownloadAndSwapAsync(UpdateInfo info, IProgress<double> progress)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No encuentro dónde está Botín.");
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, AssetName + ".download");
        var old = exe + ".old";

        try
        {
            using (var http = NewClient(TimeSpan.FromMinutes(10)))
            using (var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? info.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress.Report(Math.Min(1, (double)done / total));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("No tengo permiso para escribir en la carpeta de Botín. Movelo a otra carpeta (por ejemplo, Documentos) o bajá la versión nueva a mano.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            TryDelete(tmp);
            throw new InvalidOperationException("No se pudo bajar la versión nueva. Revisá la conexión y probá de nuevo.", ex);
        }

        // Que sea el archivo completo y un ejecutable de verdad antes de reemplazar nada.
        var len = new FileInfo(tmp).Length;
        if ((info.Size > 0 && len != info.Size) || !LooksLikeExe(tmp))
        {
            TryDelete(tmp);
            throw new InvalidOperationException("La descarga llegó incompleta. Probá de nuevo.");
        }

        // Windows deja renombrar un exe que está corriendo (no borrarlo): el actual pasa a .old y el nuevo toma su lugar.
        try
        {
            TryDelete(old);
            File.Move(exe, old, overwrite: true);
            try
            {
                File.Move(tmp, exe);
            }
            catch
            {
                File.Move(old, exe, overwrite: true);
                throw;
            }
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            throw new InvalidOperationException("No se pudo reemplazar Botin.exe. Bajá la versión nueva a mano desde GitHub.", ex);
        }
        return exe;
    }

    /// <summary>Al abrir: borra lo que dejó una actualización anterior (el exe viejo y descargas a medias).</summary>
    public static void CleanUp()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var dir = Path.GetDirectoryName(exe)!;
        _ = Task.Run(async () =>
        {
            // El proceso viejo puede tardar un momento en terminar de cerrarse.
            for (var i = 0; i < 10; i++)
            {
                var left = false;
                foreach (var f in new[] { exe + ".old", Path.Combine(dir, AssetName + ".download") })
                {
                    if (File.Exists(f) && !TryDelete(f)) left = true;
                }
                if (!left) return;
                await Task.Delay(2000);
            }
        });
    }

    public static void Restart(string exe)
    {
        Process.Start(new ProcessStartInfo(exe, $"--updated {Environment.ProcessId}") { UseShellExecute = false });
    }

    private static bool LooksLikeExe(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
