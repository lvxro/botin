using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Botin.UI;

public partial class MainWindow : Window
{
    private readonly App _app;
    private bool _loadingSettings;
    private bool _showAll;
    private readonly HashSet<string> _expanded = new();
    private int? _shownTotal;
    private Snapshot? _last;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => Native.DarkTitleBar(new WindowInteropHelper(this).Handle, 0x0B0E14);
        Closing += (_, e) =>
        {
            if (_app.Config.CloseToTray && !_app.Quitting)
            {
                e.Cancel = true;
                Hide();
            }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && SettingsLayer.Visibility == Visibility.Visible) CloseSettings(); };

        var halo = new DoubleAnimation(1, 2.4, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, halo);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, halo);
        StatusHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // =============================== Render ===============================

    public void Render(Snapshot s)
    {
        _last = s;
        RenderStatus(s);
        RenderBanner(s);

        var total = s.TotalChests + (s.LiveChests ?? 0);
        TotalText.Text = Pretty.N(total);
        if (_shownTotal is int before && total > before) Pulse();
        _shownTotal = total;
        TotalSub.Text = s.Matches == 0
            ? "Todavía no hay partidas contadas"
            : $"en {Pretty.N(s.Matches)} {(s.Matches == 1 ? "partida" : "partidas")} · {Pretty.N(s.Average, 1)} por partida";
        LiveChip.Visibility = s.InMatch ? Visibility.Visible : Visibility.Collapsed;
        LiveChipText.Text = s.LiveChests is int lc ? $"● +{lc} en esta partida" : "● partida en curso";

        SessionText.Text = Pretty.N(s.SessionChests + (s.LiveChests ?? 0));
        SetTrend(SessionTrend, SessionTrendText, s.SessionMatches > 0 && s.Matches > s.SessionMatches ? (double)s.SessionChests / s.SessionMatches - s.Average : null, "prom.");
        HeroTrendRow.Visibility = s.RecentAverage.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (s.RecentAverage is double ra) SetTrend(HeroTrend, HeroTrendText, ra - s.Average, "/ partida");
        SessionSub.Text = s.SessionMatches == 0
            ? $"desde las {Snapshot.SessionStartUtc.ToLocalTime():HH:mm}"
            : $"{s.SessionMatches} {(s.SessionMatches == 1 ? "partida" : "partidas")} · prom. {Pretty.N((double)s.SessionChests / s.SessionMatches, 1)}"
              + (s.PlaySessionNow >= 1 ? $" · {Pretty.Minutes(s.PlaySessionNow)}" : "");

        if (s.Last is { } last)
        {
            LastText.Text = Pretty.N(last.ChestsOpened);
            SetTrend(LastTrend, LastTrendText, s.Matches > 1 ? last.ChestsOpened - s.Average : null, "");
            LastSub.Text = $"{Pretty.Relative(last.When)} · {Pretty.Placement(last)} · {Pretty.Playlist(last.Playlist)}";
        }
        else
        {
            LastText.Text = "–";
            LastTrend.Visibility = Visibility.Collapsed;
            LastSub.Text = "sin partidas todavía";
        }

        TreasureText.Text = Pretty.N(s.Treasures);
        TreasureSub.Text = s.ChestsPerTreasure is int cpt ? $"1 cada {Pretty.N(cpt)} cofres" : "de la búsqueda del tesoro";

        var empty = s.Matches == 0 && !s.InMatch;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DataArea.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) return;

        var timeMode = ByTime.IsChecked == true;
        TimeSummary.Visibility = timeMode ? Visibility.Visible : Visibility.Collapsed;
        ChartLegend.Visibility = timeMode ? Visibility.Collapsed : Visibility.Visible;
        if (timeMode)
        {
            Chart.SetData(s.PlayDaily, time: true);
            ChartTitle.Text = "Tiempo de juego";
            ChartSub.Text = "Por día, de la primera partida a la última de cada sesión (con el lobby entre partidas)";
            PlayTodayText.Text = s.PlayToday > 0 ? Pretty.Minutes(s.PlayToday) : "nada todavía";
            PlayWeekText.Text = Pretty.Minutes(s.PlayWeek);
            PlayLongestText.Text = s.LongestSession is { } ls
                ? $"{Pretty.Minutes(ls.Minutes)} ({ls.StartUtc.ToLocalTime().ToString("d MMM", L.Culture)}, {ls.Matches} partidas)"
                : "–";
        }
        else if (ByDay.IsChecked == true)
        {
            Chart.SetData(s.Daily);
            ChartTitle.Text = "Cofres por día";
            ChartSub.Text = s.Daily.Count == 1 ? "Tu primer día" : $"Últimos {s.Daily.Count} días con partidas";
        }
        else
        {
            Chart.SetData(s.Chart);
            ChartTitle.Text = "Cofres por partida";
            ChartSub.Text = s.Chart.Count(p => !p.Live) switch
            {
                0 => "Partida en curso",
                1 => "Tu primera partida",
                var n => $"Últimas {n} partidas",
            };
        }

        RenderCurve(s.Curve);
        RenderPlaces(s.Places);

        if (s.Best is { } best)
        {
            BestText.Text = Pretty.N(best.ChestsOpened);
            BestSub.Text = $"{Pretty.Relative(best.When)} · {Pretty.Playlist(best.Playlist)}";
        }
        AvgText.Text = Pretty.N(s.Average, 1);
        TodayText.Text = Pretty.N(s.Today);
        WinsText.Text = Pretty.N(s.Wins);

        var maxType = Math.Max(1, s.Types.Count > 0 ? s.Types.Max(t => t.Count) : 1);
        TypesList.ItemsSource = s.Types.Select(t => new
        {
            t.Name,
            CountText = Pretty.N(t.Count),
            Ratio = (double)t.Count / maxType,
            Brush = TypeBrush(t.ColorKey),
        }).ToList();

        var rows = _showAll ? s.Rows : s.Rows.Take(12).ToList();
        Rows.ItemsSource = rows.Select(r => new
        {
            r.When, r.Mode, r.Place, r.Kills, r.Chests,
            Ratio = Math.Max(0.02, r.ChestsRatio),
            PlaceBrush = r.Win ? Res("Gold") : Res("Text"),
            PlaceWeight = r.Win ? FontWeights.Bold : FontWeights.Normal,
            ChestBrush = r.IsRecord ? Res("Gold") : (Brush)new SolidColorBrush(Color.FromRgb(0xC9, 0x9A, 0x3A)),
            TreasureLabel = r.Treasures > 0 ? $"★ {r.Treasures}" : "",
            TreasureTip = r.TreasureText,
            HasTreasure = r.Treasures > 0,
            RowBg = r.Win ? (Brush)new SolidColorBrush(Color.FromArgb(0x10, 0xF7, 0xB9, 0x3E)) : Brushes.Transparent,
            Id = r.Match.SessionId,
            Chevron = _expanded.Contains(r.Match.SessionId) ? "\uE70E" : "\uE70D",
            DetailVisibility = _expanded.Contains(r.Match.SessionId) ? Visibility.Visible : Visibility.Collapsed,
            Match = _expanded.Contains(r.Match.SessionId) ? r.Match : null,
            DetailMeta = DetailMeta(r.Match),
            KindChips = r.Match.Chests.GroupBy(c => ChestKinds.Canonical(c.Kind)).OrderByDescending(g => g.Count())
                .Select(g => new { Name = L.Name(g.Key), Count = g.Count(), Brush = MatchTimeline.KindBrush(g.Key) }).ToList(),
            OthersText = OthersText(r.Match),
            NoTreasureVisibility = r.Match.TreasureFinds.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            TreasureItems = r.Match.TreasureFinds.Select(f => new { Name = f.DisplayName, Where = TreasureWhere(f) }).ToList(),
            RouteVisibility = r.Match.Chests.Any(c => c.X.HasValue) || r.Match.Path is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        MoreBtn.Visibility = s.Rows.Count > 12 ? Visibility.Visible : Visibility.Collapsed;
        MoreBtn.Content = _showAll ? "Ver menos" : $"Ver las {s.Rows.Count} partidas";
        HistorySub.Text = $"{Pretty.N(s.Matches)} partidas · {Pretty.N(s.Kills)} eliminaciones";

        ActivityEmpty.Visibility = s.Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityList.ItemsSource = s.Activity.Take(8).Select(a => new
        {
            a.Text,
            Time = Pretty.Relative(a.At.ToUniversalTime()),
            Brush = a.Kind switch
            {
                "match" or "batch" => Res("Gold"),
                "live" => Res("Red"),
                "error" => Res("Red"),
                "import" => Res("Purple"),
                _ => Res("Dim"),
            },
        }).ToList();

        ProfileBtn.Visibility = s.Profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ProfileText.Text = s.ProfileName ?? "Perfil";
    }

    /// <summary>Chip "▲ +4,2" verde o "▼ −3" rojo comparado con el promedio (oculto si no hay con qué comparar).</summary>
    private static void SetTrend(Border chip, TextBlock text, double? delta, string suffix)
    {
        if (delta is not double d)
        {
            chip.Visibility = Visibility.Collapsed;
            return;
        }
        chip.Visibility = Visibility.Visible;
        var flat = Math.Abs(d) < 0.5;
        var up = d > 0;
        text.Text = flat ? "= promedio" : $"{(up ? "▲ +" : "▼ −")}{Pretty.N(Math.Abs(d), Math.Abs(d) < 10 ? 1 : 0)}{(suffix.Length > 0 ? " " + suffix : "")}";
        text.Foreground = flat ? Res("Muted") : up ? Res("Green") : Res("Red");
        chip.Background = flat ? Res("Surface2") : up ? Res("UpBg") : Res("DownBg");
    }

    private void RenderPlaces(LootPlaces p)
    {
        if (MapLand.IsChecked == true)
        {
            LootMap.SetLandings(p.Landings.Select(l => (l.X, l.Y, l.Times)).ToList());
        }
        else
        {
            LootMap.SetHeat(p.Points);
        }

        MapSub.Text = p.MatchesWithPosition == 0
            ? "Se arma con tus próximas partidas"
            : $"{Pretty.N(p.Points.Count)} cofres en {p.MatchesWithPosition} {(p.MatchesWithPosition == 1 ? "partida" : "partidas")}";
        var missing = p.MatchesTotal - p.MatchesWithPosition;
        MapNote.Text = (missing > 0 ? $"{missing} partidas más viejas no tienen ubicación (su replay ya no está en la PC). " : "")
            + (MapService.HasImage ? "Mapa: fortnite-api.com" : _app.Config.DownloadMap ? "Bajando el mapa de la temporada…" : "Activá \"Bajar el mapa de la temporada\" en Ajustes para ver el mapa.");

        var named = p.Places.Where(x => x.Name != LootPlaces.Outskirts).ToList();
        var maxChests = Math.Max(1, named.Count > 0 ? named.Max(x => x.Chests) : 1);
        PlacesList.ItemsSource = p.Places.Take(7).Select(x => new
        {
            x.Name,
            Chests = Pretty.N(x.Chests),
            Detail = $" en {x.Matches} {(x.Matches == 1 ? "partida" : "partidas")}",
            Ratio = x.Name == LootPlaces.Outskirts ? 0.0 : (double)x.Chests / maxChests,
            NameBrush = x.Name == LootPlaces.Outskirts ? Res("Muted") : Res("Text"),
        }).ToList();

        // Mejor lugar para caer: el de mayor promedio entre los que probaste al menos 2 veces.
        var candidates = p.Landings.Where(l => l.Times >= 2 && l.Name != LootPlaces.Outskirts).ToList();
        var best = candidates.Count > 0 ? candidates.MaxBy(l => l.AvgChests) : null;
        LandingsList.ItemsSource = p.Landings.Take(6).Select(l => new
        {
            l.Name,
            Detail = $"caíste {l.Times} {(l.Times == 1 ? "vez" : "veces")} · récord {l.Best}" + (ReferenceEquals(l, best) ? " · tu mejor lugar" : ""),
            Avg = Pretty.N(l.AvgChests, 0),
            AvgBrush = ReferenceEquals(l, best) ? Res("Gold") : Res("Text"),
            Bg = ReferenceEquals(l, best) ? (Brush)new SolidColorBrush(Color.FromArgb(0x1C, 0xF7, 0xB9, 0x3E)) : Res("Surface2"),
        }).ToList();
    }

    private void MapMode_Click(object sender, RoutedEventArgs e)
    {
        if (_last != null) RenderPlaces(_last.Places);
    }

    private void RenderCurve(LootCurve c)
    {
        Curve.SetData(c.Minutes);
        if (c.Minutes.Count == 0)
        {
            CurveEarly.Text = CurvePeak.Text = CurveHalf.Text = "–";
            CurvePeakSub.Text = CurveHalfSub.Text = "";
            CurveTipBox.Visibility = Visibility.Collapsed;
            return;
        }
        CurveSub.Text = $"Cofres por minuto desde tu primer cofre (cuando aterrizás) · {c.MatchesUsed} partidas";
        CurveEarly.Text = Pretty.N(c.EarlyAverage, 1);
        CurveEarlySub.Text = $"cofres por partida · {Pretty.N(c.EarlyPct, 0)} % de todos tus cofres";
        CurvePeak.Text = c.PeakMinute is int pm ? $"Minuto {pm}–{pm + 1}" : "–";
        CurvePeakSub.Text = $"{Pretty.N(c.PeakAverage, 1)} cofres por partida";
        CurveHalf.Text = c.HalfMinute is int h ? $"Antes del minuto {h + 1}" : "–";
        CurveHalfSub.Text = "ya abriste la mitad de tus cofres";
        CurveTip.Text = c.Tip ?? "";
        CurveTipBox.Visibility = c.Tip != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DetailMeta(MatchRecord m)
    {
        var parts = new List<string>();
        if (m.DurationSeconds is int d) parts.Add($"{d / 60} min");
        if (m.Eliminations is int k) parts.Add($"{k} {(k == 1 ? "eliminación" : "eliminaciones")}");
        var destroyed = m.Chests.Count(c => c.Destroyed);
        if (destroyed > 0) parts.Add($"{destroyed} {(destroyed == 1 ? "cofre destruido" : "cofres destruidos")}");
        if (m.DurationSeconds is int dd && dd > 60 && m.ChestsOpened > 0) parts.Add($"{Pretty.N(m.ChestsOpened / (dd / 60.0), 1)} cofres/min");
        return string.Join("  ·  ", parts);
    }

    private static string OthersText(MatchRecord m)
    {
        var others = m.OtherInteractions.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
            .Select(kv => $"{L.Name(kv.Key)} {kv.Value}").ToList();
        return others.Count == 0 ? "" : "No cuentan como cofres: " + string.Join(" · ", others);
    }

    private static string TreasureWhere(TreasureFind f)
    {
        var when = f.ObtainedAtSeconds is float t ? $"{(f.TimeExact ? "" : "~")}{Pretty.Clock(t)}" : "momento desconocido";
        var src = string.IsNullOrEmpty(f.SourceKind) ? "origen desconocido" : f.SourceLabel;
        var sure = f.FromGoldenChest ? "cofre dorado" : f.SourceCertain ? "seguro" : "probable";
        var flag = f.SourceIsChest ? "" : "  ·  ⚑ no salió de un cofre, puede ser un error";
        return $"min {when}  ·  de {src} ({sure}){flag}";
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            if (!_expanded.Remove(id)) _expanded.Add(id);
            if (_last != null) Render(_last);
        }
    }

    /// <summary>Modo de captura: abre el detalle de la partida más reciente con tesoros y elige el gráfico.</summary>
    public void PrepareShot(bool byDay, bool time = false)
    {
        if (_last?.Rows.FirstOrDefault(r => r.Match.Path != null) is { } r) _expanded.Add(r.Match.SessionId);
        ByTime.IsChecked = time;
        ByDay.IsChecked = byDay && !time;
        ByMatch.IsChecked = !byDay && !time;
        if (_last != null) Render(_last);
    }

    private void ChartMode_Click(object sender, RoutedEventArgs e)
    {
        if (_last != null) Render(_last);
    }

    private void RenderStatus(Snapshot s)
    {
        string text;
        Brush color;
        ProgressTrack.Visibility = Visibility.Collapsed;
        switch (s.State)
        {
            case TrackerState.NoFolder:
                text = "No encuentro la carpeta de replays";
                color = Res("Red");
                break;
            case TrackerState.Analyzing when s.Progress is { } p:
                text = $"Analizando replays {p.Done + 1} de {p.Total}";
                color = Res("Gold");
                ProgressTrack.Visibility = Visibility.Visible;
                ProgressFill.Width = 110.0 * p.Done / Math.Max(1, p.Total);
                break;
            case TrackerState.InMatch:
                text = s.LiveChests is int lc ? $"En partida · {lc} cofres" : "En partida";
                color = Res("Red");
                break;
            case TrackerState.Empty:
                text = s.FortniteRunning ? "Fortnite abierto · esperando replays" : "Esperando tu primera partida";
                color = Res("Muted");
                break;
            default:
                text = s.FortniteRunning ? "Fortnite abierto · listo" : "Listo · esperando partida";
                color = Res("Green");
                break;
        }
        StatusText.Text = text;
        StatusDot.Fill = color;
        StatusHalo.Fill = color;
    }

    private int? _closing;

    /// <summary>Aviso "Fortnite se cerró: Botín se cierra en N s" (null = ocultar, -1 = esperando la última partida).</summary>
    public void ShowClosingBanner(int? seconds)
    {
        _closing = seconds;
        if (_last != null) RenderBanner(_last);
    }

    private void RenderBanner(Snapshot s)
    {
        if (_closing is int secs)
        {
            Banner.Visibility = Visibility.Visible;
            BannerTitle.Text = secs < 0 ? "Fortnite se cerró · contando tu última partida…" : $"Fortnite se cerró · Botín se cierra en {secs} s";
            BannerText.Text = "Se vuelve a abrir solo la próxima vez que abras Fortnite.";
            BannerBtn.Content = "Mantener abierto";
            BannerBtn.Visibility = Visibility.Visible;
            return;
        }
        BannerBtn.Content = "Elegir carpeta";
        BannerBtn2.Visibility = Visibility.Collapsed;
        _ccBanner = false;
        _updateBanner = false;
        if (Updater.Available is { } u && (!_app.UpdateDismissed || _app.UpdateProgress != null))
        {
            _updateBanner = true;
            Banner.Visibility = Visibility.Visible;
            var v = u.Version.ToString(3);
            BannerBtn.Visibility = Visibility.Visible;
            BannerBtn2.Visibility = Visibility.Visible;
            BannerBtn.IsEnabled = true;
            if (_app.UpdateProgress is double p)
            {
                BannerTitle.Text = double.IsNaN(p) ? $"Instalando Botín {v}…" : $"Bajando Botín {v}… {p * 100:0} %";
                BannerText.Text = "Botín se vuelve a abrir solo en unos segundos. Tus datos quedan como están.";
                BannerBtn.Content = "Actualizando…";
                BannerBtn.IsEnabled = false;
                BannerBtn2.Visibility = Visibility.Collapsed;
            }
            else if (_app.UpdateError is { } err)
            {
                BannerTitle.Text = $"No se pudo actualizar a Botín {v}";
                BannerText.Text = err;
                BannerBtn.Content = "Reintentar";
            }
            else
            {
                BannerTitle.Text = $"Hay una versión nueva de Botín ({v})";
                BannerText.Text = $"Tenés la {Updater.CurrentText}. Se actualiza en unos segundos y tus datos quedan como están.";
                BannerBtn.Content = "Actualizar";
            }
            return;
        }
        BannerBtn.IsEnabled = true;
        if (s.State != TrackerState.NoFolder && _app.ChestCounter.Found && !_app.ChestCounterDismissed)
        {
            _ccBanner = true;
            Banner.Visibility = Visibility.Visible;
            BannerTitle.Text = "Tenés instalado Fortnite Chest Counter";
            BannerText.Text = "Botín ya hace todo lo que hacía y tu historial ya está acá. Si querés, lo desinstalo: va a la papelera, así que se puede recuperar.";
            BannerBtn.Content = "Desinstalarlo";
            BannerBtn.Visibility = Visibility.Visible;
            BannerBtn2.Visibility = Visibility.Visible;
            return;
        }
        if (s.State == TrackerState.NoFolder)
        {
            Banner.Visibility = Visibility.Visible;
            BannerTitle.Text = "No encuentro la carpeta de replays de Fortnite";
            BannerText.Text = "Activá Configuración → Juego → Repeticiones → Grabar repeticiones y jugá una partida. Si moviste la carpeta, elegila acá.";
            BannerBtn.Visibility = Visibility.Visible;
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
        }
    }

    private void Pulse()
    {
        var a = new DoubleAnimation(1.08, 1, TimeSpan.FromSeconds(0.7)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
        TotalScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        TotalScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static Brush TypeBrush(string key) => key switch
    {
        "Rare" => Res("Blue"),
        "Buried" => new SolidColorBrush(Color.FromRgb(0xC0, 0x8A, 0x5B)),
        "Sprite" => Res("Purple"),
        "Normal" => Res("Gold"),
        _ => Res("Muted"),
    };

    public void RenderOverlayState()
    {
        OverlaySwitch.IsChecked = _app.Config.OverlayEnabled;
        MoveText.Text = _app.Overlay.MoveMode ? "Fijar" : "Ubicar";
    }

    // =============================== Encabezado ===============================

    private void OverlaySwitch_Click(object sender, RoutedEventArgs e)
    {
        _app.SetOverlayEnabled(OverlaySwitch.IsChecked == true);
        if (SettingsLayer.Visibility == Visibility.Visible) LoadSettings();
    }

    private void Move_Click(object sender, RoutedEventArgs e) => _app.ToggleMoveMode();

    /// <summary>El cofre de fondo de la tarjeta dorada se recorta con las esquinas redondeadas de la tarjeta (no en rectángulo).</summary>
    private void HeroGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // La grilla está dentro del padding (24,20): el recorte se extiende hasta el borde interno de la tarjeta.
        HeroGrid.Clip = new RectangleGeometry(new Rect(-24, -20, e.NewSize.Width + 48, e.NewSize.Height + 40), 15, 15);
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        var menu = new ContextMenu { PlacementTarget = ProfileBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var p in _last.Profiles)
        {
            var item = new MenuItem
            {
                Header = $"{p.Name}  ·  {Pretty.N(p.Chests)} cofres",
                IsChecked = p.Name == _last.ProfileName,
            };
            var key = p.Key;
            item.Click += (_, _) => _app.SelectProfile(key);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _showAll = !_showAll;
        if (_last != null) Render(_last);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"botin-partidas-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV (Excel, Google Sheets)|*.csv",
        };
        if (dlg.ShowDialog(this) == true) _app.ExportCsv(dlg.FileName);
    }

    // =============================== Ajustes ===============================

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SettingsLayer.Visibility = Visibility.Visible;
        SettingsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(420, 0, TimeSpan.FromSeconds(0.22)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseSettings()
    {
        var a = new DoubleAnimation(0, 420, TimeSpan.FromSeconds(0.18)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += (_, _) => SettingsLayer.Visibility = Visibility.Collapsed;
        SettingsShift.BeginAnimation(TranslateTransform.XProperty, a);
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseSettings();

    private void LoadSettings()
    {
        _loadingSettings = true;
        var c = _app.Config;
        SetOverlay.IsChecked = c.OverlayEnabled;
        ModeAuto.IsChecked = c.OverlayMode != "always";
        ModeAlways.IsChecked = c.OverlayMode == "always";
        ScaleSlider.Value = Math.Round(c.OverlayScale * 100);
        OpacitySlider.Value = Math.Round(c.OverlayOpacity * 100);
        ScaleLabel.Text = $"{ScaleSlider.Value:0} %";
        OpacityLabel.Text = $"{OpacitySlider.Value:0} %";
        ShowSession.IsChecked = c.OverlayShowSession;
        ShowLast.IsChecked = c.OverlayShowLast;
        ShowTreasures.IsChecked = c.OverlayShowTreasures;
        SetCompact.IsChecked = c.OverlayCompact;
        SetStartup.IsChecked = c.StartWithWindows;
        SetTray.IsChecked = c.CloseToTray;
        SetCloseWithFortnite.IsChecked = c.CloseWithFortnite;
        SetMap.IsChecked = c.DownloadMap;
        SetUpdates.IsChecked = c.CheckUpdates;
        SetLive.IsChecked = c.LiveUpdate;
        AboutText.Text = $"Botín {Updater.CurrentText} · Solo lee los archivos .replay que guarda Fortnite: no modifica el juego, no lee su memoria y no manda datos a ningún lado. Lector de replays basado en Fortnite Chest Counter (LED-N) y FortniteReplayDecompressor (Shiqan), ambos con licencia MIT.";
        FolderText.Text = c.DemosFolder;
        FolderText.ToolTip = c.DemosFolder;
        ImportBtn.Visibility = File.Exists(Store.ChestCounterData) ? Visibility.Visible : Visibility.Collapsed;
        UninstallCcBtn.Visibility = _app.ChestCounter.Found ? Visibility.Visible : Visibility.Collapsed;
        _loadingSettings = false;
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var c = _app.Config;
        c.OverlayMode = ModeAlways.IsChecked == true ? "always" : "auto";
        c.OverlayShowSession = ShowSession.IsChecked == true;
        c.OverlayShowLast = ShowLast.IsChecked == true;
        c.OverlayShowTreasures = ShowTreasures.IsChecked == true;
        c.OverlayCompact = SetCompact.IsChecked == true;
        c.CloseToTray = SetTray.IsChecked == true;
        c.LiveUpdate = SetLive.IsChecked == true;
        if (c.DownloadMap != (SetMap.IsChecked == true))
        {
            c.DownloadMap = SetMap.IsChecked == true;
            if (c.DownloadMap) MapService.RefreshInBackground(force: true);
        }
        if (c.CheckUpdates != (SetUpdates.IsChecked == true))
        {
            c.CheckUpdates = SetUpdates.IsChecked == true;
            _app.SaveConfig();
            _app.CheckForUpdates();
        }
        if (c.StartWithWindows != (SetStartup.IsChecked == true) || c.CloseWithFortnite != (SetCloseWithFortnite.IsChecked == true))
        {
            c.StartWithWindows = SetStartup.IsChecked == true;
            c.CloseWithFortnite = SetCloseWithFortnite.IsChecked == true;
            _app.EnsureWatcher();
        }
        if (c.OverlayEnabled != (SetOverlay.IsChecked == true))
        {
            _app.SetOverlayEnabled(SetOverlay.IsChecked == true);
        }
        _app.SaveConfig();
        _app.ApplyOverlay();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleLabel == null || OpacityLabel == null) return;
        ScaleLabel.Text = $"{ScaleSlider.Value:0} %";
        OpacityLabel.Text = $"{OpacitySlider.Value:0} %";
        // Al crear la ventana, WPF lleva los sliders a su mínimo y dispara este evento: eso no es el usuario.
        if (_loadingSettings || SettingsLayer.Visibility != Visibility.Visible) return;
        _app.Config.OverlayScale = Math.Round(ScaleSlider.Value / 5) * 5 / 100;
        _app.Config.OverlayOpacity = Math.Round(OpacitySlider.Value / 5) * 5 / 100;
        _app.SaveConfig();
        _app.ApplyOverlay(preview: true);
    }

    private bool _ccBanner, _updateBanner;

    /// <summary>Repinta el aviso de actualización (progreso, error).</summary>
    public void RenderUpdate()
    {
        if (_last != null) RenderBanner(_last);
    }

    private void Banner_Click(object sender, RoutedEventArgs e)
    {
        if (_closing != null) _app.CancelAutoClose();
        else if (_updateBanner) _app.InstallUpdate();
        else if (_ccBanner) UninstallCc_Click(sender, e);
        else ChooseFolder_Click(sender, e);
    }

    private void Banner2_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBanner) _app.UpdateDismissed = true;
        else _app.ChestCounterDismissed = true;
        if (_last != null) RenderBanner(_last);
    }

    private void UninstallCc_Click(object sender, RoutedEventArgs e)
    {
        var found = ChestCounterCleaner.Detect();
        if (!found.Found)
        {
            MessageBox.Show(this, "No encontré Fortnite Chest Counter en esta PC.", "Botín", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(this,
            "Antes de desinstalar, traigo a Botín cualquier partida de Chest Counter que falte.\n\nDespués voy a:\n" + found.Describe() +
            "\n\nLos archivos van a la papelera de reciclaje (se pueden recuperar). ¿Sigo?",
            "Desinstalar Fortnite Chest Counter", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        var problems = _app.UninstallChestCounter();
        var noExe = found.Exes.Count == 0
            ? "\n\nNo encontré el archivo ChestCounter.exe: si lo tenés guardado en otra carpeta, borralo a mano."
            : "";
        if (problems.Count == 0)
        {
            MessageBox.Show(this, "Listo: Fortnite Chest Counter quedó desinstalado. Todo está en la papelera." + noExe, "Botín", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(this, "Lo desinstalé en parte. Esto no se pudo:\n\n" + string.Join("\n", problems) + noExe, "Botín", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (SettingsLayer.Visibility == Visibility.Visible) LoadSettings();
        if (_last != null) RenderBanner(_last);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Carpeta de replays de Fortnite",
            InitialDirectory = Directory.Exists(_app.Config.DemosFolder) ? _app.Config.DemosFolder : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (dlg.ShowDialog(this) == true)
        {
            _app.SetDemosFolder(dlg.FolderName);
            FolderText.Text = dlg.FolderName;
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Store.Folder}\"") { UseShellExecute = true });

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var n = _app.ImportChestCounter();
        ImportBtn.Content = n > 0 ? $"Importadas {n} partidas" : "No había partidas nuevas";
        ImportBtn.IsEnabled = false;
    }
}
