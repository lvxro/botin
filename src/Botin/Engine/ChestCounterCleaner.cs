using System.Diagnostics;
using Microsoft.Win32;

namespace Botin;

/// <summary>Lo que quedó instalado de Fortnite Chest Counter en esta PC.</summary>
public sealed class ChestCounterInstall
{
    public List<string> Exes { get; } = new();
    public List<string> DataFolders { get; } = new();
    public bool StartsWithWindows { get; set; }
    public bool Running { get; set; }

    public bool Found => Exes.Count > 0 || DataFolders.Count > 0 || StartsWithWindows || Running;

    /// <summary>Lista legible para el aviso de confirmación.</summary>
    public string Describe()
    {
        var lines = new List<string>();
        if (Running) lines.Add("• Cerrar Chest Counter (está abierto)");
        if (StartsWithWindows) lines.Add("• Sacarlo del inicio de Windows");
        lines.AddRange(Exes.Select(e => "• " + e));
        lines.AddRange(DataFolders.Select(d => "• " + d + "  (sus datos)"));
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Detecta y desinstala Fortnite Chest Counter. No tiene instalador: es un .exe suelto, una carpeta de datos en
/// %APPDATA%, un valor en el inicio de Windows y, a veces, un proceso en segundo plano (--standby).
/// Todo lo que se borra va a la papelera de reciclaje.
/// </summary>
public static class ChestCounterCleaner
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ChestCounter";
    private const string StandbyStopEvent = @"Local\ChestCounter.StandbyStop";

    public static ChestCounterInstall Detect()
    {
        var found = new ChestCounterInstall();
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcessesByName("ChestCounter"))
        {
            found.Running = true;
            try
            {
                if (p.MainModule?.FileName is { } path) exes.Add(path);
            }
            catch
            {
            }
            p.Dispose();
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(RunValue) is string cmd)
            {
                found.StartsWithWindows = true;
                if (ExeFromCommand(cmd) is { } exe && File.Exists(exe)) exes.Add(exe);
            }
        }
        catch
        {
        }

        // Lugares típicos donde queda el .exe descargado.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop) })
        {
            var candidate = Path.Combine(dir, "ChestCounter.exe");
            if (File.Exists(candidate)) exes.Add(candidate);
        }

        found.Exes.AddRange(exes);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in new[] { "ChestCounter", "ChestTracker" })
        {
            var dir = Path.Combine(appData, name);
            if (Directory.Exists(dir)) found.DataFolders.Add(dir);
        }
        return found;
    }

    private static string? ExeFromCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        var space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    /// <summary>Cierra, saca del inicio y manda a la papelera. Devuelve lo que no se pudo hacer (vacío = todo bien).</summary>
    public static List<string> Uninstall(ChestCounterInstall found)
    {
        var problems = new List<string>();

        // 1. Que el proceso en segundo plano no lo vuelva a abrir, y cerrar lo que esté abierto.
        try
        {
            if (EventWaitHandle.TryOpenExisting(StandbyStopEvent, out var stop))
            {
                stop.Set();
                stop.Dispose();
            }
        }
        catch
        {
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            problems.Add("No pude sacarlo del inicio de Windows: " + ex.Message);
        }
        foreach (var p in Process.GetProcessesByName("ChestCounter"))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                problems.Add("No pude cerrar Chest Counter: " + ex.Message);
            }
            p.Dispose();
        }

        // 2. El .exe (y los .old que deja su actualizador) y la carpeta de datos, a la papelera.
        foreach (var exe in found.Exes)
        {
            var dir = Path.GetDirectoryName(exe);
            var files = new List<string> { exe };
            if (dir != null && Directory.Exists(dir))
            {
                files.AddRange(Directory.EnumerateFiles(dir, Path.GetFileName(exe) + "*.old"));
                files.AddRange(Directory.EnumerateFiles(dir, Path.GetFileName(exe) + ".download"));
            }
            foreach (var f in files.Where(File.Exists))
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex)
                {
                    problems.Add($"No pude borrar {f}: {ex.Message}");
                }
            }
        }
        foreach (var d in found.DataFolders.Where(Directory.Exists))
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(d, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                problems.Add($"No pude borrar {d}: {ex.Message}");
            }
        }
        return problems;
    }
}
