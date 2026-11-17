using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Botin.UI;

public partial class OverlayWindow : Window
{
    // Tiempos del momento (ms desde que aparece). La animación y el sonido usan los mismos.
    private const double CountStart = 120, CountDuration = 850, Stamp = 1050, ChipsStart = 1350, ChipGap = 140, Hold = 8000;

    private IntPtr _hwnd;
    private bool _first = true;
    private string? _lastSessionId;
    private int _lastGoalChests = -1, _lastGoal, _lastTotal = -1, _lastStreak;
    private DateTime _holdUntil;
    private Snapshot? _pending;
    private Storyboard? _summaryAnim;

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

    private string Mode => _look?.Celebrations ?? "full";

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
        SummaryCard.Background.Opacity = Math.Max(bg, 0.75);
        // Sin fondo, los textos llevan sombra propia, como el HUD de Fortnite.
        Content.Effect = bg < 0.6
            ? new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.95 - bg, Color = Colors.Black }
            : null;

        // En partida, la pastilla queda mínima: anillo y total. La sesión, los tesoros y el avance del día vuelven en el lobby.
        var showAll = (!c.OverlayCompact || _expanded || MoveMode) && (!_live || MoveMode);
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
        _live = s.InMatch;
        if (_look != null) ApplyLook(_look);

        // Mientras corre un momento, la pastilla espera su turno con los datos más nuevos.
        if (DateTime.UtcNow < _holdUntil)
        {
            _pending = s;
            return;
        }

        var total = s.TotalChests + (s.LiveChests ?? 0);
        var lastId = s.Last?.SessionId;
        // Partida terminada: hay una "última partida" nueva y es reciente (no un lote de replays viejos).
        var finished = !_first && lastId != null && lastId != _lastSessionId && s.Progress == null && s.Last is { } m
            && DateTime.UtcNow - m.When.AddSeconds(m.DurationSeconds ?? 0) < TimeSpan.FromMinutes(15);
        var goalReached = !_first && _lastGoalChests >= 0 && _lastGoalChests < _lastGoal && s.GoalChests >= s.Goal && s.Goal == _lastGoal;
        var milestone = !_first && _lastTotal >= 0 && total / 1000 > _lastTotal / 1000 ? total / 1000 * 1000 : 0;

        if (_first)
        {
            ApplyPill(s, animate: false, goalReached: false);
        }
        else if (finished && Mode != "off")
        {
            var pillAt = ShowSummary(s.Last!, s, goalReached, milestone, still: !Animate);
            _holdUntil = DateTime.UtcNow + pillAt;
            _pending = s;
            After(pillAt, () =>
            {
                _holdUntil = DateTime.MinValue;
                var latest = _pending ?? s;
                _pending = null;
                ApplyPill(latest, animate: true, goalReached);
            });
        }
        else
        {
            ApplyPill(s, animate: true, goalReached);
        }

        _lastSessionId = lastId;
        _lastGoalChests = s.GoalChests;
        _lastGoal = s.Goal;
        _lastTotal = total;
        _first = false;
        FitToContent();
    }

    /// <summary>Actualiza la pastilla: total, anillo, racha, sesión y tesoros.</summary>
    private void ApplyPill(Snapshot s, bool animate, bool goalReached)
    {
        var total = s.TotalChests + (s.LiveChests ?? 0);
        Session.Text = Pretty.N(s.SessionChests + (s.LiveChests ?? 0));
        Treasures.Text = Pretty.N(s.Treasures);
        SetGoalLine(s);
        SetStreak(s.Streak, animate);
        var progress = s.Goal > 0 ? (double)s.GoalChests / s.Goal : 0;
        if (!animate)
        {
            DisplayTotal = total;
            RingProgress = progress;
        }
        else
        {
            if (Math.Abs(total - DisplayTotal) >= 1) CountTo(total);
            if (Math.Abs(progress - RingProgress) > 0.0005) AnimateRing(progress, goalReached);
        }
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
    }

    /// <summary>Llama con "x3": partidas seguidas arriba de tu promedio. Salta cuando crece.</summary>
    private void SetStreak(int n, bool animate)
    {
        StreakChip.Visibility = n >= 2 ? Visibility.Visible : Visibility.Collapsed;
        StreakText.Text = $"x{n}";
        if (animate && n > _lastStreak && n >= 2 && Animate)
        {
            var pop = new DoubleAnimation(1.45, 1, TimeSpan.FromSeconds(0.5)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
            StreakScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            StreakScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        _lastStreak = n;
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
        BeginAnimation(DisplayTotalProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(up ? 1.0 : 0.3))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (!up) return;

        var pulse = new DoubleAnimation(1.1, 1, TimeSpan.FromSeconds(0.6)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        TotalScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        TotalScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        Glow.BeginAnimation(OpacityProperty, Flash(0, 0.2, 1.6));
    }

    private bool _ringMoving;

    private void AnimateRing(double target, bool goalReached)
    {
        if (!Animate)
        {
            RingProgress = target;
            return;
        }
        _ringMoving = true;
        RingTip.Opacity = 1;
        var a = new DoubleAnimation(target, TimeSpan.FromSeconds(0.85)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        a.Completed += (_, _) =>
        {
            _ringMoving = false;
            RingTip.Opacity = 0;
            if (goalReached) RingCelebrate();
        };
        BeginAnimation(RingProgressProperty, a);
    }

    /// <summary>Meta cumplida: el anillo estalla y se enciende dos veces.</summary>
    private void RingCelebrate()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.0) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.55))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.85))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.0)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        RingGlow.BeginAnimation(OpacityProperty, a);
        if (Mode != "full") return;
        var grow = new DoubleAnimation(0.85, 1.75, TimeSpan.FromSeconds(0.9)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RingBurstScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RingBurstScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        RingBurst.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.9)));
    }

    /// <summary>Dibuja el arco del anillo desde arriba, en sentido horario, con el porcentaje al centro y la chispa en la punta.</summary>
    private void DrawRing(double p)
    {
        const double c = 22, r = 20;
        var done = p >= 0.9995;
        RingPctValue.Text = done ? "✓" : Pretty.N(Math.Floor(p * 100));
        RingPctValue.Foreground = done ? Palette.Gold : Palette.Text;
        RingPctValue.FontSize = done ? 17 : 14;
        RingPctUnit.Text = done ? "" : "%";

        p = Math.Clamp(p, 0, 1);
        var a = (-90 + 360 * p) * Math.PI / 180;
        var end = new Point(c + r * Math.Cos(a), c + r * Math.Sin(a));
        Canvas.SetLeft(RingTip, end.X - 3);
        Canvas.SetTop(RingTip, end.Y - 3);
        if (!_ringMoving) RingTip.Opacity = 0;

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
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        RingArc.Data = g;
    }

    // =============================== Momento ===============================

    /// <summary>
    /// Tarjeta debajo de la pastilla al terminar una partida, en capas: aparece, los cofres suben con un tic (0,85 s),
    /// cae el sello de la nota (con chispas y destello si es S o récord), saltan los logros uno por uno y después se
    /// actualizan el total y el anillo. Devuelve cuándo le toca a la pastilla. Se va sola a los 8 s.
    /// </summary>
    public TimeSpan ShowSummary(MatchRecord m, Snapshot s, bool goalReached, int milestone, bool still)
    {
        var full = Mode == "full";
        var grade = ReferenceEquals(s.Last, m) ? s.LastGrade : null;

        var parts = new List<string>();
        var victory = m.Placement == 1;
        if (victory) parts.Add("Victoria magistral");
        else if (m.Placement is int p && p > 0) parts.Add($"Puesto #{p}");
        else parts.Add("Partida terminada");
        if (m.DurationSeconds is int d && d >= 60) parts.Add($"{d / 60} min");
        SummaryTitle.Text = string.Join(" · ", parts);
        SummaryTitle.Foreground = victory ? Palette.Gold : Palette.Muted;
        SummaryChestsLabel.Text = m.ChestsOpened == 1 ? "cofre" : "cofres";

        // Sello de la nota
        GradeBlock.Visibility = grade != null ? Visibility.Visible : Visibility.Collapsed;
        var big = grade?.Grade is "S" or "R";
        if (grade != null)
        {
            var color = grade.Grade switch { "B" => Palette.Teal, "A" => Palette.Purple, "S" or "R" => Palette.Gold, _ => Palette.Dim };
            GradeBox.BorderBrush = color;
            GradeLetter.Foreground = color;
            GradeName.Foreground = color;
            GradeLetter.Text = grade.Grade == "R" ? "RÉCORD" : grade.Grade;
            GradeLetter.FontSize = grade.Grade == "R" ? 14 : 34;
            GradeName.Text = grade.Name.ToUpperInvariant();
            GradeBox.Background = big ? Palette.B(Palette.Alpha(Palette.Gold400, 0x2E)) : Palette.B(Color.FromArgb(0x40, 0, 0, 0));
            GradeBox.Effect = big ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Color = Palette.Gold400, Opacity = 0.75 } : null;
        }
        SummaryBorder.Color = Palette.Alpha(Palette.Gold400, big ? (byte)0x8C : (byte)0x40);

        // Logros: los de la partida, más meta del día e hito si tocan.
        var accolades = new List<Accolade>(grade?.Accolades ?? Array.Empty<Accolade>());
        if (goalReached) accolades.Add(new Accolade("gold", "check", "¡Meta del día cumplida!"));
        if (milestone > 0) accolades.Add(new Accolade("gold", "chest", $"¡{Pretty.N(milestone)} cofres!"));
        Chips.Children.Clear();
        foreach (var a in accolades) Chips.Children.Add(MakeChip(a));
        Chips.Visibility = accolades.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var pillAt = ChipsStart + accolades.Count * ChipGap + 150;
        _summaryAnim?.Stop(this);
        Summary.Visibility = Visibility.Visible;
        Expand(TimeSpan.FromMilliseconds(Hold + 600));
        FitToContent();

        if (_look?.CelebrationSound ?? true)
        {
            MomentSound.Play(new MomentSound.Plan(still ? 0 : m.ChestsOpened, grade?.Grade, accolades.Count, CountStart, CountDuration,
                still ? 0 : Stamp, still ? 0 : ChipsStart, ChipGap,
                goalReached ? pillAt + 850 : null, milestone > 0 ? pillAt + 250 : null));
        }

        if (still)
        {
            ShowFinal(m);
            After(TimeSpan.FromMilliseconds(Hold), HideSummary);
            return TimeSpan.Zero;
        }

        // 1. Aparece (0,28 s), con un leve rebote.
        SummaryValue = 0;
        GradeBlock.Opacity = 0;
        Shine.Opacity = 0;
        var sb = new Storyboard();
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Hold))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Hold + 500))));
        Storyboard.SetTarget(fade, Summary);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        var slide = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, Summary);
        Storyboard.SetTargetProperty(slide, new PropertyPath("RenderTransform.Children[1].Y"));
        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Completed += (_, _) => HideSummary();
        _summaryAnim = sb;
        sb.Begin(this, true);
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        SummaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        SummaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // 2. Los cofres suben de 0 al total, frenando al final.
        BeginAnimation(SummaryValueProperty, new DoubleAnimation(0, m.ChestsOpened, TimeSpan.FromMilliseconds(CountDuration))
        {
            BeginTime = TimeSpan.FromMilliseconds(CountStart),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        // 3. Pausa y cae el sello de la nota; el número rebota.
        After(TimeSpan.FromMilliseconds(Stamp), () =>
        {
            var pop = new DoubleAnimation(1.12, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
            SummaryChestsScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            SummaryChestsScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            if (grade == null) return;
            GradeBlock.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            var stamp = new DoubleAnimation(2.2, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
            GradeScale.BeginAnimation(ScaleTransform.ScaleXProperty, stamp);
            GradeScale.BeginAnimation(ScaleTransform.ScaleYProperty, stamp);
            GradeRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (!full) return;
            if (big)
            {
                SummaryGlow.BeginAnimation(OpacityProperty, Flash(0, 0.18, 1.9));
                Shine.Opacity = 1;
                ShineShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-120, Math.Max(300, SummaryInner.ActualWidth) + 40, TimeSpan.FromMilliseconds(800))
                {
                    BeginTime = TimeSpan.FromMilliseconds(80),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
                Burst(GradeBlock, grade.Grade == "R" ? 80 : 50, [Palette.Gold300, Palette.Gold400, Color.FromRgb(0xFF, 0xF4, 0xD6), Palette.Gold500], grade.Grade == "R" ? 1.35 : 1);
            }
            else if (grade.Grade == "A")
            {
                Burst(GradeBlock, 22, [Color.FromRgb(0xBD, 0xA6, 0xFF), Color.FromRgb(0x8F, 0x6C, 0xE0), Color.FromRgb(0xFF, 0xF4, 0xD6)], 0.8);
            }
        });

        // 4. Los logros saltan uno por uno.
        for (var i = 0; i < Chips.Children.Count; i++)
        {
            var chip = (FrameworkElement)Chips.Children[i];
            var at = TimeSpan.FromMilliseconds(ChipsStart + i * ChipGap);
            chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = at });
            var g = (TransformGroup)chip.RenderTransform;
            var sc = (ScaleTransform)g.Children[0];
            var tr = (TranslateTransform)g.Children[1];
            var popIn = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = at, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 } };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, popIn);
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = at, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        return TimeSpan.FromMilliseconds(pillAt);
    }

    /// <summary>El momento con todo en su lugar, sin animar.</summary>
    private void ShowFinal(MatchRecord m)
    {
        _summaryAnim?.Stop(this);
        BeginAnimation(SummaryValueProperty, null);
        SummaryValue = m.ChestsOpened;
        GradeBlock.BeginAnimation(OpacityProperty, null);
        GradeBlock.Opacity = 1;
        GradeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        GradeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        GradeScale.ScaleX = GradeScale.ScaleY = 1;
        GradeRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        GradeRotate.Angle = 0;
        Shine.Opacity = 0;
        foreach (FrameworkElement chip in Chips.Children)
        {
            chip.BeginAnimation(OpacityProperty, null);
            chip.Opacity = 1;
            var g = (TransformGroup)chip.RenderTransform;
            ((ScaleTransform)g.Children[0]).ScaleX = ((ScaleTransform)g.Children[0]).ScaleY = 1;
            ((TranslateTransform)g.Children[1]).Y = 0;
        }
        Summary.Visibility = Visibility.Visible;
        Summary.BeginAnimation(OpacityProperty, null);
        Summary.Opacity = 1;
    }

    private void HideSummary()
    {
        Summary.Visibility = Visibility.Collapsed;
        FitToContent();
    }

    private static readonly Dictionary<string, Geometry> Icons = new()
    {
        ["bolt"] = Geometry.Parse("M7,0 L1.5,7 H6 L5,12 L10.5,5 H6 Z"),
        ["star"] = Geometry.Parse("M6,0 L7.8,3.9 L12,4.3 L8.8,7.1 L9.8,11.2 L6,9.1 L2.2,11.2 L3.2,7.1 L0,4.3 L4.2,3.9 Z"),
        ["fire"] = Geometry.Parse("M5.5,0 C6,3 9,4 9,8 A3.5,3.5 0 0 1 2,8 C2,6.4 2.8,5.5 3.6,4.7 C3.8,6 4.5,6.5 5,6.3 C4.6,4.2 4.8,2 5.5,0 Z"),
        ["crown"] = Geometry.Parse("M1,3.5 L3.5,6 L6,2 L8.5,6 L11,3.5 L10,10 H2 Z"),
        ["check"] = Geometry.Parse("F0 M6,0 A6,6 0 1 1 5.99,0 Z M5,8.6 L2.6,6.2 L3.7,5.1 L5,6.4 L8.3,3.1 L9.4,4.2 Z"),
        ["chest"] = Geometry.Parse("M1,5 H11 V10.5 H1 Z M1.5,4.5 C1.5,2.5 3,1.5 6,1.5 C9,1.5 10.5,2.5 10.5,4.5 Z"),
    };

    /// <summary>Un logro: ícono + texto, con el color de su tono.</summary>
    private static Border MakeChip(Accolade a)
    {
        var (fg, accent) = a.Tone switch
        {
            "gold" => (Palette.GoldLight, Palette.Gold400),
            "teal" => (Palette.Teal, Palette.TealColor),
            "violet" => (Palette.Purple, Color.FromRgb(0xBD, 0xA6, 0xFF)),
            "fire" => (Palette.Fire, Palette.FireColor),
            _ => (Palette.Muted, Color.FromRgb(0xA9, 0xB2, 0xC1)),
        };
        var icon = new System.Windows.Shapes.Path
        {
            Data = Icons.GetValueOrDefault(a.Icon) ?? Icons["star"],
            Fill = fg,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var text = new TextBlock { Text = a.Text, Foreground = fg, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(text);
        var gold = a.Tone == "gold";
        return new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Background = Palette.B(Palette.Alpha(accent, gold ? (byte)0x1F : (byte)0x14)),
            BorderBrush = Palette.B(Palette.Alpha(accent, gold ? (byte)0x5C : (byte)0x2E)),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } },
        };
    }

    private void SummaryInner_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // El destello y las chispas no se salen de las esquinas redondeadas de la tarjeta.
        SummaryInner.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17);
    }

    // =============================== Chispas ===============================

    private sealed class Spark
    {
        public required Ellipse Dot;
        public double X, Y, Vx, Vy, Life = 1;
    }

    private readonly List<Spark> _sparks = new();
    private TimeSpan _lastFrame;

    /// <summary>Estallido de chispas desde el centro de <paramref name="from"/>, dentro de la tarjeta.</summary>
    private void Burst(FrameworkElement from, int n, Color[] colors, double spread)
    {
        if (!Animate) return;
        var c = from.TranslatePoint(new Point(from.ActualWidth / 2, from.ActualHeight / 2), Fx);
        var rnd = Random.Shared;
        for (var i = 0; i < n; i++)
        {
            var a = rnd.NextDouble() * Math.PI * 2;
            var v = (1.5 + rnd.NextDouble() * 3.5) * spread;
            var size = 2.4 + rnd.NextDouble() * 4.2;
            var dot = new Ellipse { Width = size, Height = size, Fill = Palette.B(colors[i % colors.Length]), IsHitTestVisible = false };
            Fx.Children.Add(dot);
            _sparks.Add(new Spark { Dot = dot, X = c.X, Y = c.Y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v - 1.2 });
        }
        if (_sparks.Count == n)
        {
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var k = _lastFrame == TimeSpan.Zero ? 1 : Math.Clamp((now - _lastFrame).TotalMilliseconds / 16.7, 0.2, 3);
        _lastFrame = now;
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var p = _sparks[i];
            p.X += p.Vx * k;
            p.Y += p.Vy * k;
            p.Vy += 0.08 * k;
            p.Vx *= Math.Pow(0.985, k);
            p.Life -= 0.016 * k;
            if (p.Life <= 0)
            {
                Fx.Children.Remove(p.Dot);
                _sparks.RemoveAt(i);
                continue;
            }
            Canvas.SetLeft(p.Dot, p.X - p.Dot.Width / 2);
            Canvas.SetTop(p.Dot, p.Y - p.Dot.Height / 2);
            p.Dot.Opacity = p.Life;
        }
        if (_sparks.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }

    // =============================== Capturas de prueba ===============================

    /// <summary>Deja el momento fijo en pantalla, con los valores finales y sin sonido.</summary>
    public void PinSummaryForShot(MatchRecord m, Snapshot s)
    {
        var sound = _look?.CelebrationSound ?? true;
        if (_look != null) _look.CelebrationSound = false;
        ShowSummary(m, s, goalReached: true, milestone: (s.TotalChests / 1000) * 1000, still: true);
        if (_look != null) _look.CelebrationSound = sound;
        ShowFinal(m);
        Expand(TimeSpan.FromMinutes(1));
    }

    /// <summary>Muestra el estado "en partida" sin que haya una.</summary>
    public void PreviewLive(bool on)
    {
        _live = on;
        if (_look != null) ApplyLook(_look);
        else ApplyLive();
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
