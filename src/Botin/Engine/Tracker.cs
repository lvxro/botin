namespace Botin;

public enum TrackerState { NoFolder, Empty, Idle, InMatch, Analyzing }

/// <summary>Un evento para el registro de actividad de la app.</summary>
public sealed record ActivityItem(DateTime At, string Text, string Kind);

/// <summary>
/// Vigila la carpeta de replays de Fortnite en un hilo propio, analiza cada partida terminada una sola vez y avisa
/// a la interfaz con <see cref="Changed"/>. Toda la lectura de <see cref="Data"/> desde afuera va con <see cref="Sync"/>.
/// La lógica de cuándo un replay está listo viene de Fortnite Chest Counter (LED-N, MIT).
/// </summary>
public sealed class Tracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AbandonedLiveAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Dictionary<string, long> _lastSeenSize = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<ActivityItem> _activity = new();
    private Thread? _thread;
    private volatile bool _stop;

    public object Sync { get; } = new();
    public TrackerData Data => _data;

    public TrackerState State { get; private set; } = TrackerState.Idle;
    public (int Done, int Total)? Progress { get; private set; }
    public MatchRecord? LiveMatch { get; private set; }
    public MatchRecord? JustCounted { get; private set; }
    /// <summary>Hay replays esperando a que Fortnite los termine de escribir.</summary>
    public bool Pending { get; private set; }

    /// <summary>Se dispara en el hilo del tracker: la interfaz tiene que pasar al Dispatcher.</summary>
    public event Action? Changed;

    public Tracker(Store store, Config config, TrackerData data)
    {
        _store = store;
        _config = config;
        _data = data;
        if (_data.AnalyzerVersion < ReplayAnalyzer.CurrentVersion)
        {
            // La lógica de análisis cambió: se vuelven a leer los replays que siguen en disco.
            foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value != "skipped").Select(kv => kv.Key).ToList())
            {
                _data.ProcessedFiles.Remove(key);
            }
            _data.AnalyzerVersion = ReplayAnalyzer.CurrentVersion;
            _store.SaveData(_data);
        }
    }

    public IReadOnlyList<ActivityItem> Activity
    {
        get { lock (Sync) return _activity.ToList(); }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Tracker", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    /// <summary>Revisar ya (por ejemplo, después de cambiar la carpeta).</summary>
    public void Poke()
    {
        lock (Sync) _lastSeenSize.Clear();
        _wake.Set();
    }

    private void Loop()
    {
        while (!_stop)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                var pending = ScanOnce(out var finishing);
                Pending = pending;
                var liveChanged = UpdateLive();
                UpdateState();
                if (liveChanged) Raise();
                if (finishing) delay = TimeSpan.FromSeconds(2);
                else if (pending || LiveMatch != null || _liveFile != null) delay = TimeSpan.FromSeconds(4);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Error en el ciclo del tracker", ex);
            }
            _wake.WaitOne(delay);
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }

    public void Log(string text, string kind)
    {
        lock (Sync)
        {
            _activity.Insert(0, new ActivityItem(DateTime.Now, text, kind));
            if (_activity.Count > 50) _activity.RemoveAt(_activity.Count - 1);
        }
    }

    private void UpdateState()
    {
        TrackerState s;
        if (!Directory.Exists(_config.DemosFolder)) s = TrackerState.NoFolder;
        else if (Progress != null) s = TrackerState.Analyzing;
        else if (_liveFile != null) s = TrackerState.InMatch;
        else if (!ReplayFiles().Any()) s = TrackerState.Empty;
        else s = TrackerState.Idle;
        if (s != State)
        {
            State = s;
            Raise();
        }
    }

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    private IEnumerable<FileInfo> ReplayFiles()
    {
        var dir = new DirectoryInfo(_config.DemosFolder);
        return dir.Exists ? dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc) : Enumerable.Empty<FileInfo>();
    }

    private List<FileInfo> UnprocessedFiles()
    {
        lock (Sync) return ReplayFiles().Where(f => !_data.ProcessedFiles.ContainsKey(FileKey(f))).ToList();
    }

    /// <summary>Una pasada por la carpeta. Devuelve true si quedan replays esperando.</summary>
    private bool ScanOnce(out bool finishing)
    {
        finishing = false;
        var pending = false;
        var ready = new List<FileInfo>();
        foreach (var file in UnprocessedFiles())
        {
            file.Refresh();
            if (!file.Exists) continue;
            if (!IsReady(file, ref finishing))
            {
                pending = true;
                continue;
            }
            ready.Add(file);
        }
        if (ready.Count == 0) return pending;

        var batch = ready.Count >= 2;
        var added = 0;
        var chests = 0;
        for (var i = 0; i < ready.Count && !_stop; i++)
        {
            if (batch)
            {
                Progress = (i, ready.Count);
                UpdateState();
                Raise();
            }
            var m = Process(ready[i], logEach: !batch);
            if (m != null)
            {
                added++;
                chests += m.ChestsOpened;
                if (!batch)
                {
                    JustCounted = m;
                }
            }
        }
        if (batch)
        {
            Progress = null;
            Log($"Se analizaron {ready.Count} replays: {L.Count(added, "partida", "partida", "partida", "partidas", "partida", "partidas")} y {L.Count(chests, "cofre", "cofre", "cofre")} sumados.", "batch");
        }
        UpdateState();
        Raise();
        JustCounted = null;
        return pending;
    }

    private bool IsReady(FileInfo file, ref bool finishing)
    {
        var live = ReplayHeaderProbe.IsLive(file.FullName);
        var quietFor = DateTime.UtcNow - file.LastWriteTimeUtc;
        if (live == true) return quietFor > AbandonedLiveAge;
        if (live == false && quietFor > OldReplayAge) return true;

        bool stable;
        lock (Sync)
        {
            stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
            _lastSeenSize[file.FullName] = file.Length;
        }
        if (live == null) return stable && quietFor > TimeSpan.FromMinutes(2);
        if (!ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            finishing = true;
            return stable && quietFor >= FinalizedQuietPeriod;
        }
        return stable && quietFor > QuietPeriod;
    }

    private void Mark(string key, string value)
    {
        lock (Sync)
        {
            _data.ProcessedFiles[key] = value;
            _store.SaveData(_data);
        }
    }

    private MatchRecord? Process(FileInfo file, bool logEach)
    {
        var key = FileKey(file);
        if (ReplayHeaderProbe.IsLive(file.FullName) == true && ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            Mark(key, "incomplete");
            if (logEach) Log("Replay incompleto (Fortnite se cerró en medio de la partida): no se puede contar.", "warn");
            return null;
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, ParseTimeout);
        }
        catch (Exception ex)
        {
            var message = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
            ErrorLog.Write($"Replay ilegible: {file.FullName} ({file.Length} bytes)", ex);
            Mark(key, "error: " + message);
            if (logEach) Log($"No se pudo leer {file.Name}: {message}", "error");
            return null;
        }

        record.ReplayFile = file.Name;
        if (!record.IsBattleRoyale)
        {
            lock (Sync) _data.Matches.RemoveAll(m => m.SessionId == record.SessionId);
            Mark(key, "otro modo");
            if (logEach) Log("Partida ignorada: no es Battle Royale (Creativo u otro modo).", "skip");
            return null;
        }

        lock (Sync)
        {
            var existing = _data.Matches.FirstOrDefault(m => m.SessionId == record.SessionId);
            if (existing != null && existing.AnalyzerVersion >= record.AnalyzerVersion)
            {
                _data.ProcessedFiles[key] = record.SessionId;
                _store.SaveData(_data);
                return null;
            }
            if (existing != null)
            {
                record.AnalyzedAtUtc = existing.AnalyzedAtUtc;
                _data.Matches.Remove(existing);
            }
            _data.Matches.Add(record);
            _data.ProcessedFiles[key] = record.SessionId;
            _store.SaveData(_data);
            if (string.IsNullOrEmpty(_config.Profile))
            {
                _config.Profile = Profiles.Key(record);
                try { _store.SaveConfig(_config); } catch { }
            }
        }
        if (_liveFile == file.FullName) ClearLive();

        if (logEach)
        {
            var t = record.TreasureFinds.Count > 0 ? $" · ★ {L.Count(record.TreasureFinds.Count, "tesoro", "tesoro", "tesoro")}" : "";
            Log($"Partida contada: {L.Count(record.ChestsOpened, "cofre", "cofre", "cofre")} ({Pretty.Playlist(record.Playlist)}, {Pretty.Placement(record)}){t}", "match");
        }
        return record;
    }

    /// <summary>Lee el archivo (aunque Fortnite lo esté escribiendo) y lo analiza en un hilo de prioridad mínima.</summary>
    private static MatchRecord Analyze(FileInfo file, TimeSpan timeout)
    {
        byte[] bytes;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        MatchRecord? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                result = new ReplayAnalyzer().Analyze(ms, file.Name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "ReplayAnalysis" };
        worker.Start();
        if (!worker.Join(timeout)) throw new TimeoutException("el análisis tardó demasiado");
        bytes = Array.Empty<byte>();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    // ---- Partida en curso ------------------------------------------------------------------------
    private string? _liveFile;
    private long _liveSize = -1;
    private DateTime _liveParsedUtc = DateTime.MinValue;

    public bool InMatch => _liveFile != null;

    private void ClearLive()
    {
        _liveFile = null;
        _liveSize = -1;
        LiveMatch = null;
    }

    private bool UpdateLive()
    {
        var file = UnprocessedFiles()
            .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => ReplayHeaderProbe.IsLive(f.FullName) == true);

        if (file == null)
        {
            if (_liveFile != null && (!File.Exists(_liveFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_liveFile) > TimeSpan.FromMinutes(5)))
            {
                ClearLive();
                return true;
            }
            return false;
        }

        var started = false;
        if (file.FullName != _liveFile)
        {
            ClearLive();
            _liveFile = file.FullName;
            started = true;
            Log("Empezó una partida. Sus cofres se suman cuando termine.", "live");
        }

        if (!_config.LiveUpdate || ReplayHeaderProbe.KeyMissing(file.FullName)) return started;
        if (file.Length == _liveSize || DateTime.UtcNow - _liveParsedUtc < TimeSpan.FromSeconds(30)) return started;

        try
        {
            var record = Analyze(file, TimeSpan.FromSeconds(60));
            _liveSize = file.Length;
            _liveParsedUtc = DateTime.UtcNow;
            var changed = LiveMatch?.ChestsOpened != record.ChestsOpened || LiveMatch?.TreasureFinds.Count != record.TreasureFinds.Count;
            LiveMatch = record;
            return changed || started;
        }
        catch
        {
            _liveParsedUtc = DateTime.UtcNow;
            return started;
        }
    }
}

/// <summary>Lee lo justo del encabezado del replay para saber si la partida se sigue grabando (de Fortnite Chest Counter).</summary>
public static class ReplayHeaderProbe
{
    private const uint FileMagic = 0x1CA2E27F;

    public static bool KeyMissing(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic) return true;
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64) return true;
                fs.Seek(count * 20L, SeekOrigin.Current);
            }
            fs.Seek(12, SeekOrigin.Current);
            var len = br.ReadInt32();
            fs.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            br.ReadUInt32();
            br.ReadInt64();
            br.ReadUInt32();
            if (br.ReadUInt32() == 0) return false;
            var keyLen = br.ReadInt32();
            if (keyLen <= 0 || keyLen > 256) return true;
            return br.ReadBytes(keyLen).All(b => b == 0);
        }
        catch
        {
            return true;
        }
    }

    public static bool? IsLive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic) return null;
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64) return null;
                fs.Seek(count * 20L, SeekOrigin.Current);
            }
            br.ReadUInt32();
            br.ReadUInt32();
            br.ReadUInt32();
            var len = br.ReadInt32();
            fs.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            return br.ReadUInt32() != 0;
        }
        catch
        {
            return null;
        }
    }
}
