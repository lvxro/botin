using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Botin.UI;

public partial class OverlayWindow : Window
{
    private IntPtr _hwnd;
    private bool _first = true;
    private string? _lastSessionId;
    private Storyboard? _summaryAnim;
    private int _lastGoalChests = -1;
    private int _lastGoal;

    public bool MoveMode { get; private set; }

    public event Action? MoveModeChanged;
    public event Action<double, double>? Moved;

    /// <summary>Si el usuario apagó las animaciones de Windows, todo aparece directo.</summary>
    private static bool Animate => SystemParameters.ClientAreaAnimation;

    /// <summary>Total que se muestra; se anima contando hacia arriba cuando cambia.</summary>
    public static readonly DependencyProperty DisplayTotalProperty = DependencyProperty.Register(
        nameof(DisplayTotal), typeof(double), typeof(OverlayWindow),
        new PropertyMetadata(0.0, (d, e) =>
        {
            var w = (OverlayWindow)d;
            w.Total.Text = Pretty.N(Math.Round((double)e.NewValue));
            w.FitToContent();
        }));

    public double DisplayTotal
    {
        get => (double)GetValue(DisplayTotalProperty);
        set => SetValue(DisplayTotalProperty, value);
    }

    /// <summary>Avance del anillo (0 = vacío, 1 = meta cumplida).</summary>
    public static readonly DependencyProperty RingProgressProperty = DependencyProperty.Register(
        nameof(RingProgress), typeof(double), typeof(OverlayWindow),
        new PropertyMetadata(0.0, (d, e) => ((OverlayWindow)d).DrawRing((double)e.NewValue)));

    public double RingProgress
    {
        get => (double)GetValue(RingProgressProperty);
        set => SetValue(RingProgressProperty, value);
    }

    /// <summary>Cofres de la partida en el momento; sube de 0 al número final.</summary>
    public static readonly DependencyProperty SummaryValueProperty = DependencyProperty.Register(
        nameof(SummaryValue), typeof(double), typeof(OverlayWindow),
        new PropertyMetadata(0.0, (d, e) => ((OverlayWindow)d).SummaryChests.Text = "+" + Pretty.N(Math.Round((double)e.NewValue))));

    public double SummaryValue
    {
        get => (double)GetValue(SummaryValueProperty);
        set => SetValue(SummaryValueProperty, value);
    }

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetOverlayStyle(_hwnd, clickThrough: true);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!MoveMode) return;
            if (e.ClickCount == 2)
            {
                SetMoveMode(false);
                return;
            }
            try { DragMove(); } catch { }
            Moved?.Invoke(Left, Top);
        };
        MouseRightButtonUp += (_, _) => { if (MoveMode) SetMoveMode(false); };

        // Punto rojo que late durante la partida.
        var halo = new DoubleAnimation(1, 2.7, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        LiveHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, halo);
        LiveHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, halo);
        LiveHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private Config? _look;
    private bool _expanded;
    private bool _live;

    public void ApplyLook(Config c)
    {
        _look = c;
        RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(c.OverlayScale, 0.85, 1.7);

        // La opacidad es solo del fondo: los números siempre al 100 % para que se lean (guías de HUD).
        var bg = Math.Clamp(c.OverlayOpacity, 0.4, 1);
        Card.Background.Opacity = bg;
        Card.BorderBrush.Opacity = bg;
        Sheen.Opacity = bg;
        CardShadow.Opacity = 0.55 * bg;
        SummaryCard.Background.Opacity = Math.Max(bg, 0.7);
        // Sin fondo, los textos llevan sombra propia, como el HUD de Fortnite.
        Content.Effect = bg < 0.6
            ? new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.95 - bg, Color = Colors.Black }
            : null;

        var showAll = !c.OverlayCompact || _expanded || MoveMode;
        RingBlock.Visibility = c.OverlayShowGoal ? Visibility.Visible : Visibility.Collapsed;
        GoalLine.Visibility = c.OverlayShowGoal && showAll ? Visibility.Visible : Visibility.Collapsed;
        SessionBlock.Visibility = c.OverlayShowSession && showAll ? Visibility.Visible : Visibility.Collapsed;
        TreasureBlock.Visibility = c.OverlayShowTreasures && showAll ? Visibility.Visible : Visibility.Collapsed;
        ApplyLive();
        FitToContent();
    }

    /// <summary>En modo compacto, muestra todos los datos un rato (al terminar una partida).</summary>
    private void Expand(TimeSpan forHowLong)
    {
        _expanded = true;
        if (_look != null) ApplyLook(_look);
        After(forHowLong, () =>
        {
            _expanded = false;
            if (_look != null) ApplyLook(_look);
        });
    }

    public void PlaceInitially(Config c)
    {
        var area = SystemParameters.WorkArea;
        if (c.OverlayLeft is double l && c.OverlayTop is double t && l > SystemParameters.VirtualScreenLeft - 50
            && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Por defecto: arriba a la derecha, debajo de donde suele ir el minimapa.
            Left = area.Right - 380;
            Top = area.Top + 330;
        }
    }

    private bool _fitQueued;

    /// <summary>
    /// Ajusta la ventana exactamente al contenido. SizeToContent de WPF a veces se queda con la medida vieja
    /// (escala, bloques que aparecen) y el overlay quedaba cortado: se le pide que vuelva a medir.
    /// </summary>
    private void FitToContent()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _fitQueued = false;
            SizeToContent = SizeToContent.Manual;
            Width = double.NaN;
            Height = double.NaN;
            SizeToContent = SizeToContent.WidthAndHeight;
        });
    }

    public void SetMoveMode(bool on)
    {
        if (MoveMode == on) return;
        MoveMode = on;
        MoveFrame.Visibility = MoveHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FitToContent();
        Cursor = on ? Cursors.SizeAll : null;
        if (_hwnd != IntPtr.Zero) Native.SetOverlayStyle(_hwnd, clickThrough: !on);
        if (!on) Moved?.Invoke(Left, Top);
        MoveModeChanged?.Invoke();
    }

    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero && IsVisible) Native.BringToTop(_hwnd);
    }

    // =============================== Datos ===============================

    public void Update(Snapshot s)
    {
        var total = s.TotalChests + (s.LiveChests ?? 0);
        Session.Text = Pretty.N(s.SessionChests + (s.LiveChests ?? 0));
        Treasures.Text = Pretty.N(s.Treasures);
        _live = s.InMatch;

        // Partida terminada: hay una "última partida" nueva y es reciente (no un lote de replays viejos).
        var lastId = s.Last?.SessionId;
        var finished = !_first && lastId != null && lastId != _lastSessionId && s.Progress == null && s.Last is { } m
            && DateTime.UtcNow - m.When.AddSeconds(m.DurationSeconds ?? 0) < TimeSpan.FromMinutes(15);
        var goalReached = !_first && _lastGoalChests >= 0 && _lastGoalChests < _lastGoal && s.GoalChests >= s.Goal && s.Goal == _lastGoal;

        if (_look != null) ApplyLook(_look);
        SetGoalLine(s);

        // Con momento, primero suben los cofres de la partida y después el total y el anillo (~0,9 s).
        var delay = finished && Animate ? TimeSpan.FromSeconds(0.9) : TimeSpan.Zero;
        var progress = s.Goal > 0 ? (double)s.GoalChests / s.Goal : 0;
        if (_first)
        {
            DisplayTotal = total;
            RingProgress = progress;
        }
        else
        {
            if (Math.Abs(total - DisplayTotal) >= 1) After(delay, () => CountTo(total));
            if (Math.Abs(progress - RingProgress) > 0.0005) After(delay, () => AnimateRing(progress, goalReached));
        }

        if (finished) ShowSummary(s.Last!, isRecord: s.Best != null && ReferenceEquals(s.Best, s.Last) && s.Matches > 1, s, goalReached);

        _lastSessionId = lastId;
        _lastGoalChests = s.GoalChests;
        _lastGoal = s.Goal;
        _first = false;
        FitToContent();
    }

    /// <summary>"1.564 / 2.000 hoy", o "2.120 ✓ meta del día" cuando ya la cumpliste.</summary>
    private void SetGoalLine(Snapshot s)
    {
        var done = s.GoalChests >= s.Goal && s.Goal > 0;
        GoalNow.Text = Pretty.N(s.GoalChests);
        GoalNow.Foreground = done ? Palette.Gold : Palette.Muted;
        GoalSep.Text = done ? " ✓ " : " / ";
        GoalSep.Foreground = done ? Palette.Gold : Palette.Dim;
        GoalTarget.Text = done ? "meta del día" : Pretty.N(s.Goal);
        GoalSuffix.Text = done ? "" : " hoy";
        GoalLine.ToolTip = null;
    }

    /// <summary>Durante la partida, una insignia roja que late en la esquina del anillo (o, sin anillo, un punto al lado del total).</summary>
    private void ApplyLive()
    {
        var ring = _look?.OverlayShowGoal ?? true;
        LiveDot.Visibility = _live && ring ? Visibility.Visible : Visibility.Collapsed;
        LiveDotInline.Visibility = _live && !ring ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CountTo(double target)
    {
        var up = target > DisplayTotal;
        if (!Animate)
        {
            DisplayTotal = target;
            return;
        }
        BeginAnimation(DisplayTotalProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(up ? 1.1 : 0.3))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (!up) return;

        var pulse = new DoubleAnimation(1.08, 1, TimeSpan.FromSeconds(0.6)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        TotalScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        TotalScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        Glow.BeginAnimation(OpacityProperty, Flash(0, 0.2, 1.6));
    }

    private void AnimateRing(double target, bool goalReached)
    {
        if (!Animate)
        {
            RingProgress = target;
            return;
        }
        BeginAnimation(RingProgressProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(0.7))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (goalReached)
        {
            // Meta cumplida: el anillo se enciende dos veces.
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.2) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.1))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2)), new CubicEase { EasingMode = EasingMode.EaseIn }));
            RingGlow.BeginAnimation(OpacityProperty, a);
        }
    }

    /// <summary>Dibuja el arco del anillo desde arriba, en sentido horario.</summary>
    private void DrawRing(double p)
    {
        const double c = 22, r = 20;
        p = Math.Clamp(p, 0, 1);
        if (p < 0.004)
        {
            RingArc.Data = null;
            return;
        }
        if (p > 0.996)
        {
            RingArc.Data = new EllipseGeometry(new Point(c, c), r, r);
            return;
        }
        var a = (-90 + 360 * p) * Math.PI / 180;
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Cos(a), c + r * Math.Sin(a)), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        RingArc.Data = g;
    }

    // =============================== Momento ===============================

    /// <summary>
    /// Tarjeta debajo de la pastilla al terminar una partida. Secuencia: aparece (0,2 s), los cofres suben de 0 al total
    /// (0,7 s), si es récord salta la etiqueta, brilla el borde y cruza un destello; después se actualizan el total y el anillo.
    /// Se va sola a los ~8 s.
    /// </summary>
    public void ShowSummary(MatchRecord m, bool isRecord, Snapshot s, bool goalReached = false)
    {
        var victory = m.Placement == 1;
        var parts = new List<string>();
        if (victory) parts.Add("Victoria magistral");
        else if (m.Placement is int p && p > 0) parts.Add($"Puesto #{p}");
        else parts.Add("Partida terminada");
        if (m.DurationSeconds is int d && d >= 60) parts.Add($"{d / 60} min");
        SummaryTitle.Text = string.Join(" · ", parts);
        SummaryTitle.Foreground = victory ? Palette.Gold : Palette.Muted;
        SummaryChestsLabel.Text = m.ChestsOpened == 1 ? "cofre" : "cofres";
        RecordBadge.Visibility = isRecord ? Visibility.Visible : Visibility.Collapsed;
        SummaryBorder.Color = isRecord ? Palette.Alpha(Palette.Gold400, 0x8C) : Palette.Alpha(Palette.Gold400, 0x3D);
        SetFacts(m, s, goalReached);

        _summaryAnim?.Stop(this);
        Summary.Visibility = Visibility.Visible;
        Expand(TimeSpan.FromSeconds(8.6));
        FitToContent();

        if (!Animate)
        {
            SummaryValue = m.ChestsOpened;
            Summary.Opacity = 1;
            BadgeScale.ScaleX = BadgeScale.ScaleY = 1;
            After(TimeSpan.FromSeconds(8), HideSummary);
            return;
        }

        // Los cofres de la partida suben de 0 al total.
        SummaryValue = 0;
        BeginAnimation(SummaryValueProperty, new DoubleAnimation(0, m.ChestsOpened, TimeSpan.FromSeconds(0.7))
        {
            BeginTime = TimeSpan.FromSeconds(0.12),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        if (isRecord)
        {
            BadgeScale.ScaleX = BadgeScale.ScaleY = 0;
            var pop = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.32))
            {
                BeginTime = TimeSpan.FromSeconds(0.82),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
            };
            BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            SummaryGlow.BeginAnimation(OpacityProperty, Flash(0.88, 0.18, 1.9));

            Shine.Opacity = 1;
            ShineShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-120, Math.Max(260, SummaryInner.ActualWidth) + 40, TimeSpan.FromSeconds(0.75))
            {
                BeginTime = TimeSpan.FromSeconds(0.95),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            BadgeScale.ScaleX = BadgeScale.ScaleY = 1;
            Shine.Opacity = 0;
        }

        var sb = new Storyboard();
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8.6))));
        Storyboard.SetTarget(fade, Summary);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(-8, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.35)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        Storyboard.SetTarget(slide, Summary);
        Storyboard.SetTargetProperty(slide, new PropertyPath("RenderTransform.Y"));

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Completed += (_, _) => HideSummary();
        _summaryAnim = sb;
        sb.Begin(this, true);
    }

    private void HideSummary()
    {
        Summary.Visibility = Visibility.Collapsed;
        FitToContent();
    }

    /// <summary>Renglón de abajo: tesoros, ritmo y meta, cada uno con su color.</summary>
    private void SetFacts(MatchRecord m, Snapshot s, bool goalReached)
    {
        SummaryFacts.Inlines.Clear();
        void Add(string text, Brush color)
        {
            if (SummaryFacts.Inlines.Count > 0) SummaryFacts.Inlines.Add(new Run("   ·   ") { Foreground = Palette.Line });
            SummaryFacts.Inlines.Add(new Run(text) { Foreground = color });
        }

        var t = m.TreasureFinds.Count;
        if (t > 0) Add($"★ +{t} {(t == 1 ? "tesoro" : "tesoros")}", Palette.Purple);
        if (s.RateSession is double rate) Add($"{Pretty.N(rate)} cofres/h", Palette.Teal);
        if (s.Goal > 0)
        {
            if (goalReached || s.GoalChests >= s.Goal) Add("¡meta del día cumplida!", Palette.Gold);
            else Add($"meta {Pretty.N(100.0 * s.GoalChests / s.Goal)} %", Palette.Muted);
        }
        SummaryFacts.Visibility = SummaryFacts.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SummaryInner_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // El destello no se sale de las esquinas redondeadas de la tarjeta.
        SummaryInner.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17);
    }

    /// <summary>Para las capturas de prueba: deja el momento fijo en pantalla, con los valores finales.</summary>
    public void PinSummaryForShot(MatchRecord m, bool isRecord, Snapshot s)
    {
        ShowSummary(m, isRecord, s);
        _summaryAnim?.Stop(this);
        BeginAnimation(SummaryValueProperty, null);
        SummaryValue = m.ChestsOpened;
        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BadgeScale.ScaleX = BadgeScale.ScaleY = 1;
        Shine.Opacity = 0;
        Summary.Visibility = Visibility.Visible;
        Summary.Opacity = 1;
        Expand(TimeSpan.FromMinutes(1));
    }

    /// <summary>Para las capturas de prueba: muestra el estado "en partida" sin que haya una.</summary>
    public void PreviewLive(bool on)
    {
        _live = on;
        ApplyLive();
    }

    public void HideSummaryForShot()
    {
        _summaryAnim?.Stop(this);
        HideSummary();
    }

    // =============================== Utilidades ===============================

    private static DoubleAnimationUsingKeyFrames Flash(double start, double rise, double end)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(start + end) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + rise))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + end)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        return a;
    }

    private void After(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = delay };
        t.Tick += (_, _) =>
        {
            t.Stop();
            action();
        };
        t.Start();
    }
}
