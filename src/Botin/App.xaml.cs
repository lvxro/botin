using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Botin.UI;
using WinForms = System.Windows.Forms;

namespace Botin;

/// <summary>
/// Un solo proceso hace todo: vigila los replays, muestra el overlay, la ventana con las estadísticas y el ícono de
/// la bandeja. Nada que abrir aparte.
/// </summary>
public partial class App : Application
{
    private const int HotkeyOverlay = 1, HotkeyLock = 2;
    private static readonly string[] FortniteProcesses = { "FortniteClient-Win64-Shipping", "FortniteClient-Win64-Shipping_EAC", "FortniteClient-Win64-Shipping_BE" };

    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private WinForms.NotifyIcon? _tray;
    private HwndSource? _hotkeySource;
    private readonly DispatcherTimer _fortniteTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshQueued;
    private bool _overlayHiddenByHotkey;

    public Store Store { get; private set; } = null!;
    public Config Config { get; private set; } = null!;
    public Tracker Tracker { get; private set; } = null!;
    public OverlayWindow Overlay { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public bool FortniteRunning { get; private set; }
    public bool Quitting { get; private set; }
    /// <summary>Lo que quedó de Fortnite Chest Counter en esta PC (se revisa al abrir la ventana).</summary>
    public ChestCounterInstall ChestCounter { get; private set; } = new();
    /// <summary>El usuario dijo "ahora no" al aviso de desinstalar Chest Counter (hasta cerrar Botín).</summary>
    public bool ChestCounterDismissed { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--watch"))
        {
            RunWatcher();
            return;
        }

        // Una sola instancia: si ya está abierta, la segunda le pide que se muestre y se cierra.
        _single = new Mutex(true, @"Local\Botin.Instance", out var first);
        var updated = Array.IndexOf(e.Args, "--updated");
        if (!first && updated >= 0 && updated + 1 < e.Args.Length && int.TryParse(e.Args[updated + 1], out var oldPid))
        {
            // Recién actualizado: la versión vieja todavía se está cerrando. Se espera a que suelte la instancia.
            _single.Dispose();
            try { Process.GetProcessById(oldPid).WaitForExit(20000); } catch { }
            _single = new Mutex(true, @"Local\Botin.Instance", out first);
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Botin.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                Dispatcher.BeginInvoke(ShowMain);
            }
        }) { IsBackground = true }.Start();

        // Con aceleración por GPU, a escalas como 125 % y con algunos drivers (sobre todo AMD), WPF deja líneas
        // finas en los bordes de cada zona que repinta. Esta interfaz es liviana: se dibuja por software, sin marcas.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        L.Set("es");
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try { EcoQos.Enable(); } catch { }

        Store = new Store(Store.DefaultFolder());
        ErrorLog.FilePath = Store.ErrorLogPath;
        DispatcherUnhandledException += (_, ex) =>
        {
            ErrorLog.Write("Error de interfaz", ex.Exception);
            ex.Handled = true;
        };

        Config = Store.LoadConfig();
        if (string.IsNullOrWhiteSpace(Config.DemosFolder)) Config.DemosFolder = Store.DefaultDemos();
        if (Config.OverlayScale < 0.85) Config.OverlayScale = 0.85;
        if (Config.OverlayLookVersion < 3)
        {
            // Versiones anteriores guardaban sin querer el mínimo de los sliders al abrir la ventana: se vuelve a lo normal.
            if (Config.OverlayOpacity < 0.6) Config.OverlayOpacity = 0.9;
            if (Config.OverlayScale <= 0.85) Config.OverlayScale = 1.0;
            Config.OverlayLookVersion = 3;
        }

        MapService.Init(Store.Folder);
        MapService.Updated += QueueRefresh;
        if (Config.DownloadMap) MapService.RefreshInBackground();

        var data = Store.LoadData();
        Tracker = new Tracker(Store, Config, data);

        // Si el jugador usaba Fortnite Chest Counter, su historial se trae solo la primera vez.
        if (!Config.Imported && data.Matches.Count == 0 && File.Exists(Store.ChestCounterData))
        {
            ImportChestCounter();
        }
        Config.Imported = true;
        SaveConfig();

        Overlay = new OverlayWindow();
        Overlay.Moved += (l, t) =>
        {
            Config.OverlayLeft = l;
            Config.OverlayTop = t;
            SaveConfig();
        };
        Overlay.MoveModeChanged += () =>
        {
            Window.RenderOverlayState();
            ApplyOverlay();
        };
        Overlay.ApplyLook(Config);
        Overlay.PlaceInitially(Config);

        Window = new MainWindow(this);
        var helper = new WindowInteropHelper(Window);
        helper.EnsureHandle();
        RegisterHotkeys(helper.Handle);

        CreateTray();

        Tracker.Changed += QueueRefresh;
        Tracker.Start();

        _fortniteTimer.Tick += (_, _) => CheckFortnite();
        _fortniteTimer.Start();
        CheckFortnite();

        Refresh();
        Window.RenderOverlayState();
        // Uso interno/soporte: desinstala Chest Counter igual que el botón, sin mostrar la ventana, y deja el resultado en un archivo.
        var cc = Array.IndexOf(e.Args, "--uninstall-chestcounter");
        if (cc >= 0 && cc + 1 < e.Args.Length)
        {
            var before = ChestCounterCleaner.Detect();
            var problems = UninstallChestCounter();
            var nl = Environment.NewLine;
            File.WriteAllText(e.Args[cc + 1],
                $"Encontrado:{nl}{before.Describe()}{nl}{nl}Problemas:{nl}{(problems.Count == 0 ? "ninguno" : string.Join(nl, problems))}{nl}{nl}Queda:{nl}{(ChestCounter.Found ? ChestCounter.Describe() : "nada")}");
            Quit();
            return;
        }

        if (!e.Args.Contains("--tray")) ShowMain();
        if (!e.Args.Contains("--shot"))
        {
            EnsureWatcher();
            Updater.CleanUp();
            Updater.Found += () => Dispatcher.BeginInvoke(OnUpdateFound);
            _updateTimer.Tick += (_, _) => CheckForUpdates();
            _updateTimer.Start();
            // Uso interno/soporte: --self-update busca la versión nueva y la instala sin esperar el clic.
            if (e.Args.Contains("--self-update")) Updater.Found += () => Dispatcher.BeginInvoke(InstallUpdate);
            _ = Task.Delay(TimeSpan.FromSeconds(e.Args.Contains("--self-update") ? 1 : 8)).ContinueWith(_ => Dispatcher.BeginInvoke(CheckForUpdates));
        }

        // Modo de prueba: guarda capturas de la ventana y del overlay en una carpeta y se cierra.
        var shot = Array.IndexOf(e.Args, "--shot");
        if (shot >= 0 && shot + 1 < e.Args.Length) _ = TakeShots(e.Args[shot + 1]);
    }

    private async Task TakeShots(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(1500);
        while (Tracker.Progress != null) await Task.Delay(500);
        for (var i = 0; i < 40 && (MapService.Pois.Count == 0 || !MapService.HasImage); i++) await Task.Delay(500);
        await Task.Delay(1500);
        Refresh();
        Overlay.Show();
        var snap = Snapshot.Build(Tracker, Config, FortniteRunning);
        if (snap.Last != null) Overlay.PinSummaryForShot(snap.Last, isRecord: true);
        await Task.Delay(800);
        Save((FrameworkElement)Window.Content, Path.Combine(dir, "main.png"));
        if (Window.FindName("Scroller") is System.Windows.Controls.ScrollViewer sv && sv.Content is FrameworkElement inner)
        {
            Save(inner, Path.Combine(dir, "full.png"));
        }
        // Fotos reales de la pantalla (con escala y DPI reales), además del render interno.
        ScreenShot(Overlay, Path.Combine(dir, "overlay-screen.png"), 20);
        Window.Activate();
        await Task.Delay(500);
        ScreenShot(Window, Path.Combine(dir, "main-screen.png"), 0);
        Window.PrepareShot(byDay: false);
        await Task.Delay(800);
        if (Window.FindName("Scroller") is System.Windows.Controls.ScrollViewer sv2 && sv2.Content is FrameworkElement inner2)
        {
            Save(inner2, Path.Combine(dir, "detail.png"));
        }
        Window.PrepareShot(byDay: false, time: true);
        await Task.Delay(800);
        if (Window.FindName("Scroller") is System.Windows.Controls.ScrollViewer sv3 && sv3.Content is FrameworkElement inner3)
        {
            Save(inner3, Path.Combine(dir, "time.png"));
        }
        Window.FindName("SettingsLayer");
        Quit();

        static void ScreenShot(System.Windows.Window w, string path, int pad)
        {
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                var origin = w.PointToScreen(new Point(0, 0));
                var pw = (int)Math.Ceiling(w.ActualWidth * dpi.DpiScaleX);
                var ph = (int)Math.Ceiling(w.ActualHeight * dpi.DpiScaleY);
                using var bmp = new System.Drawing.Bitmap(pw + 2 * pad, ph + 2 * pad);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)origin.X - pad, (int)origin.Y - pad, 0, 0, bmp.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                }
                bmp.Save(path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(path + ".error.txt", ex.ToString());
            }
        }

        static void Save(FrameworkElement el, string path, double k = 1)
        {
            var w = (int)Math.Ceiling(el.ActualWidth);
            var h = (int)Math.Ceiling(el.ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * k), (int)(h * k), 96 * k, 96 * k, System.Windows.Media.PixelFormats.Pbgra32);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0E, 0x14)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(el) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }

    // =============================== Estado ===============================

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    public void Refresh()
    {
        var s = Snapshot.Build(Tracker, Config, FortniteRunning);
        Overlay.Update(s);
        if (Window.IsVisible) Window.Render(s);
        if (_tray != null)
        {
            var tip = $"Botín · {Pretty.N(s.TotalChests + (s.LiveChests ?? 0))} cofres";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        }
        ApplyOverlay();
    }

    private void CheckFortnite()
    {
        var running = false;
        foreach (var name in FortniteProcesses)
        {
            var procs = Process.GetProcessesByName(name);
            running |= procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running) break;
        }
        if (running != FortniteRunning)
        {
            var wasRunning = FortniteRunning;
            FortniteRunning = running;
            if (running)
            {
                Tracker.Poke();
                CancelAutoClose();
            }
            else if (wasRunning && Config.CloseWithFortnite)
            {
                StartAutoClose();
            }
            Refresh();
        }
        Overlay.KeepOnTop();
    }

    public static bool IsFortniteRunning()
    {
        foreach (var name in FortniteProcesses)
        {
            var procs = Process.GetProcessesByName(name);
            var any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    // =============================== Cerrar con Fortnite ===============================

    private DispatcherTimer? _closeTimer;
    private DateTime? _closeAt;

    /// <summary>Fortnite se cerró: se espera a contar la última partida y 30 s después Botín se cierra.</summary>
    private void StartAutoClose()
    {
        _closeAt = null;
        _closeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeTimer.Tick -= AutoCloseTick;
        _closeTimer.Tick += AutoCloseTick;
        _closeTimer.Start();
        Tracker.Poke();
    }

    public void CancelAutoClose()
    {
        _closeTimer?.Stop();
        _closeAt = null;
        Window?.ShowClosingBanner(null);
    }

    private void AutoCloseTick(object? sender, EventArgs e)
    {
        if (Tracker.InMatch || Tracker.Progress != null || Tracker.Pending)
        {
            _closeAt = null; // todavía falta contar la última partida
            Window.ShowClosingBanner(-1);
            return;
        }
        _closeAt ??= DateTime.UtcNow.AddSeconds(30);
        var left = (int)Math.Ceiling((_closeAt.Value - DateTime.UtcNow).TotalSeconds);
        if (left <= 0)
        {
            Quit();
            return;
        }
        Window.ShowClosingBanner(left);
    }

    /// <summary>
    /// Vigía: proceso mínimo, sin ventana ni overlay. Cada 5 s mira si se abrió Fortnite y, si Botín no está abierto,
    /// lo abre en la bandeja. Solo existe con la opción "abrir y cerrar junto con Fortnite".
    /// </summary>
    private void RunWatcher()
    {
        _single = new Mutex(true, @"Local\Botin.Watch", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
        try { EcoQos.Enable(); } catch { }
        var stop = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Botin.WatchStop");
        new Thread(() =>
        {
            var was = IsFortniteRunning();
            while (!stop.WaitOne(TimeSpan.FromSeconds(5)))
            {
                var now = IsFortniteRunning();
                if (now && !was && !Mutex.TryOpenExisting(@"Local\Botin.Instance", out _) && Environment.ProcessPath is { } exe)
                {
                    try { Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = false }); } catch { }
                }
                was = now;
                GC.Collect();
            }
            Dispatcher.BeginInvoke(Shutdown);
        }) { IsBackground = true, Priority = ThreadPriority.Lowest }.Start();
    }

    /// <summary>Arranca o frena el vigía según la opción, y deja el inicio con Windows acorde.</summary>
    public void EnsureWatcher()
    {
        Native.SetStartWithWindows(Config.StartWithWindows, watchOnly: Config.CloseWithFortnite);
        if (Config.CloseWithFortnite)
        {
            if (Mutex.TryOpenExisting(@"Local\Botin.Watch", out var m))
            {
                m.Dispose();
            }
            else if (Environment.ProcessPath is { } exe && Path.GetFileName(exe).Equals("Botin.exe", StringComparison.OrdinalIgnoreCase))
            {
                try { Process.Start(new ProcessStartInfo(exe, "--watch") { UseShellExecute = false }); } catch { }
            }
        }
        else if (EventWaitHandle.TryOpenExisting(@"Local\Botin.WatchStop", out var stop))
        {
            stop.Set();
            stop.Dispose();
        }
    }

    // =============================== Actualizaciones ===============================

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private Version? _notified;

    /// <summary>El usuario dijo "ahora no" al aviso de versión nueva (hasta cerrar Botín).</summary>
    public bool UpdateDismissed { get; set; }
    /// <summary>null = sin actualizar; 0..1 = bajando; NaN = reemplazando y reiniciando.</summary>
    public double? UpdateProgress { get; private set; }
    public string? UpdateError { get; private set; }

    public void CheckForUpdates()
    {
        if (Config.CheckUpdates && Updater.Available == null) _ = Updater.CheckAsync();
    }

    private void OnUpdateFound()
    {
        Refresh();
        // Si Botín está solo en la bandeja, se avisa una vez con una notificación (nunca con Fortnite abierto).
        if (Updater.Available is { } u && _notified != u.Version && !Window.IsVisible && !FortniteRunning && _tray != null)
        {
            _notified = u.Version;
            _tray.ShowBalloonTip(10000, $"Botín {u.Version.ToString(3)} ya está disponible", "Abrí Botín y tocá Actualizar. Tus datos quedan como están.", WinForms.ToolTipIcon.Info);
        }
    }

    public async void InstallUpdate()
    {
        if (Updater.Available is not { } info || UpdateProgress != null) return;
        UpdateError = null;
        UpdateProgress = 0;
        Window.RenderUpdate();
        try
        {
            var exe = await Updater.DownloadAndSwapAsync(info, new Progress<double>(p =>
            {
                UpdateProgress = p;
                Window.RenderUpdate();
            }));
            UpdateProgress = double.NaN;
            Window.RenderUpdate();

            // El vigía corre con el exe viejo: se lo frena y la versión nueva lo vuelve a lanzar.
            if (EventWaitHandle.TryOpenExisting(@"Local\Botin.WatchStop", out var stop))
            {
                stop.Set();
                stop.Dispose();
                for (var i = 0; i < 25 && Mutex.TryOpenExisting(@"Local\Botin.Watch", out var w); i++)
                {
                    w.Dispose();
                    await Task.Delay(200);
                }
            }
            Updater.Restart(exe);
            Quit();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo actualizar", ex);
            UpdateProgress = null;
            UpdateError = ex is InvalidOperationException ? ex.Message : "No se pudo actualizar. Probá de nuevo.";
            Window.RenderUpdate();
        }
    }

    // =============================== Overlay ===============================

    public void ApplyOverlay(bool preview = false)
    {
        Overlay.ApplyLook(Config);
        var visible = Overlay.MoveMode
            || preview
            || (Config.OverlayEnabled && !_overlayHiddenByHotkey && (Config.OverlayMode == "always" || FortniteRunning));
        if (visible && !Overlay.IsVisible)
        {
            Overlay.Show();
            Overlay.KeepOnTop();
        }
        else if (!visible && Overlay.IsVisible)
        {
            Overlay.Hide();
        }
        if (preview) _previewUntil = DateTime.UtcNow.AddSeconds(3);
        SchedulePreviewEnd();
    }

    private DateTime _previewUntil;
    private DispatcherTimer? _previewTimer;

    /// <summary>Al mover un slider se muestra el overlay unos segundos para ver el cambio, aunque Fortnite esté cerrado.</summary>
    private void SchedulePreviewEnd()
    {
        if (_previewUntil <= DateTime.UtcNow) return;
        _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _previewTimer.Tick -= PreviewTick;
        _previewTimer.Tick += PreviewTick;
        _previewTimer.Start();
    }

    private void PreviewTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow < _previewUntil) return;
        _previewTimer!.Stop();
        ApplyOverlay();
    }

    public void SetOverlayEnabled(bool on)
    {
        Config.OverlayEnabled = on;
        _overlayHiddenByHotkey = false;
        SaveConfig();
        ApplyOverlay();
        Window.RenderOverlayState();
        if (on && Config.OverlayMode == "auto" && !FortniteRunning)
        {
            // Que se vea un momento para que el usuario sepa dónde está.
            ApplyOverlay(preview: true);
        }
    }

    public void ToggleMoveMode()
    {
        if (!Overlay.IsVisible) Overlay.Show();
        Overlay.SetMoveMode(!Overlay.MoveMode);
        Window.RenderOverlayState();
        ApplyOverlay();
    }

    private void RegisterHotkeys(IntPtr hwnd)
    {
        _hotkeySource = HwndSource.FromHwnd(hwnd);
        _hotkeySource.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY)
            {
                if (wParam.ToInt32() == HotkeyOverlay)
                {
                    if (!Config.OverlayEnabled) SetOverlayEnabled(true);
                    else
                    {
                        _overlayHiddenByHotkey = !_overlayHiddenByHotkey;
                        ApplyOverlay();
                    }
                }
                else if (wParam.ToInt32() == HotkeyLock)
                {
                    _overlayHiddenByHotkey = false;
                    ToggleMoveMode();
                }
                handled = true;
            }
            return IntPtr.Zero;
        });
        const uint vkO = 0x4F, vkL = 0x4C;
        Native.RegisterHotKey(hwnd, HotkeyOverlay, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vkO);
        Native.RegisterHotKey(hwnd, HotkeyLock, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vkL);
    }

    // =============================== Ventana y bandeja ===============================

    public void ShowMain()
    {
        try { ChestCounter = ChestCounterCleaner.Detect(); } catch { }
        if (!Window.IsVisible) Window.Show();
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
        Refresh();
    }

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        try
        {
            using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/Botin.ico"))!.Stream;
            icon = new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Abrir Botín", null, (_, _) => ShowMain());
        menu.Items.Add("Mostrar / ocultar overlay", null, (_, _) => SetOverlayEnabled(!Config.OverlayEnabled));
        menu.Items.Add("Ubicar overlay", null, (_, _) => ToggleMoveMode());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Quit());

        _tray = new WinForms.NotifyIcon { Icon = icon, Text = "Botín", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => ShowMain();
    }

    public void Quit()
    {
        Quitting = true;
        Tracker.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        base.OnExit(e);
    }

    // =============================== Acciones ===============================

    public void SaveConfig()
    {
        try { Store.SaveConfig(Config); } catch (Exception ex) { ErrorLog.Write("No se pudo guardar la configuración", ex); }
    }

    public void SelectProfile(string key)
    {
        Config.Profile = key;
        SaveConfig();
        Refresh();
    }

    public void SetDemosFolder(string folder)
    {
        lock (Tracker.Sync) Config.DemosFolder = folder;
        SaveConfig();
        Tracker.Poke();
        Refresh();
    }

    public void ExportCsv(string path)
    {
        string csv;
        lock (Tracker.Sync) csv = Snapshot.Csv(Profiles.View(Tracker.Data, Config));
        File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true));
    }

    /// <summary>
    /// Desinstala Fortnite Chest Counter: antes trae a Botín cualquier partida suya que falte, después lo cierra,
    /// lo saca del inicio de Windows y manda el .exe y sus datos a la papelera.
    /// </summary>
    public List<string> UninstallChestCounter()
    {
        var found = ChestCounterCleaner.Detect();
        try { ImportChestCounter(); } catch (Exception ex) { ErrorLog.Write("Importación antes de desinstalar", ex); }
        var problems = ChestCounterCleaner.Uninstall(found);
        ChestCounter = ChestCounterCleaner.Detect();
        Tracker.Log(problems.Count == 0 ? "Se desinstaló Fortnite Chest Counter (quedó en la papelera)." : "Chest Counter se desinstaló en parte.", problems.Count == 0 ? "import" : "warn");
        QueueRefresh();
        return problems;
    }

    /// <summary>Trae las partidas del historial de Fortnite Chest Counter (mismo formato). Devuelve cuántas sumó.</summary>
    public int ImportChestCounter()
    {
        var other = Store.LoadDataFrom(Store.ChestCounterData);
        if (other == null) return 0;
        var added = 0;
        lock (Tracker.Sync)
        {
            var data = Tracker.Data;
            var known = data.Matches.Select(m => m.SessionId).ToHashSet();
            foreach (var m in other.Matches.Where(m => !known.Contains(m.SessionId)))
            {
                data.Matches.Add(m);
                added++;
            }
            foreach (var kv in other.ProcessedFiles.Where(kv => !data.ProcessedFiles.ContainsKey(kv.Key) && (kv.Value == "skipped" || other.Matches.Any(m => m.SessionId == kv.Value))))
            {
                data.ProcessedFiles[kv.Key] = kv.Value;
            }
            Store.SaveData(data);
        }
        if (added > 0)
        {
            Tracker.Log($"Se importaron {added} partidas de Fortnite Chest Counter.", "import");
            Tracker.Poke();
            QueueRefresh();
        }
        return added;
    }
}
