using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Botin.UI;

public partial class OverlayWindow : Window
{
    private IntPtr _hwnd;
    private bool _first = true;
    private string? _lastSessionId;
    private Storyboard? _summaryAnim;

    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x8F, 0x98, 0xAB));
    private static readonly Brush LiveBrush = Frozen(Color.FromRgb(0xFF, 0x8E, 0x99));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public bool MoveMode { get; private set; }

    public event Action? MoveModeChanged;
    public event Action<double, double>? Moved;

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
        var halo = new DoubleAnimation(1, 2.6, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever };
        LiveHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, halo);
        LiveHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, halo);
        LiveHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 0, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private Config? _look;
    private bool _expanded;

    public void ApplyLook(Config c)
    {
        _look = c;
        RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(c.OverlayScale, 0.85, 1.7);

        // La opacidad es solo del fondo: los números siempre al 100 % para que se lean (guías de HUD).
        var bg = Math.Clamp(c.OverlayOpacity, 0.4, 1);
        Card.Background.Opacity = bg;
        Card.BorderBrush.Opacity = bg;
        CardShadow.Opacity = 0.6 * bg;
        Summary.Background.Opacity = Math.Max(bg, 0.6);
        // Sin fondo, los textos llevan sombra propia, como el HUD de Fortnite.
        Content.Effect = bg < 0.6
            ? new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.95 - bg, Color = Colors.Black }
            : null;

        var showAll = !c.OverlayCompact || _expanded || MoveMode;
        SessionBlock.Visibility = c.OverlayShowSession && showAll ? Visibility.Visible : Visibility.Collapsed;
        LastBlock.Visibility = c.OverlayShowLast && (showAll || LiveDot.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        TreasureBlock.Visibility = c.OverlayShowTreasures && showAll ? Visibility.Visible : Visibility.Collapsed;
        FitToContent();
    }

    /// <summary>En modo compacto, muestra todos los datos un rato (al terminar una partida).</summary>
    private void Expand(TimeSpan forHowLong)
    {
        _expanded = true;
        if (_look != null) ApplyLook(_look);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = forHowLong };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _expanded = false;
            if (_look != null) ApplyLook(_look);
        };
        t.Start();
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
            Left = area.Right - 360;
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
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
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

    public void Update(Snapshot s)
    {
        var total = s.TotalChests + (s.LiveChests ?? 0);
        Session.Text = Pretty.N(s.SessionChests + (s.LiveChests ?? 0));
        Treasures.Text = Pretty.N(s.Treasures);

        if (s.InMatch)
        {
            LiveDot.Visibility = Visibility.Visible;
            LastLabel.Text = "EN PARTIDA";
            LastLabel.Foreground = LiveBrush;
            // Si Fortnite no deja leer la partida en curso (lo normal), solo se ve "● EN PARTIDA", sin número.
            if (s.LiveChests is int lc)
            {
                Last.Text = Pretty.N(lc);
                Last.Visibility = Visibility.Visible;
                LastLabel.FontSize = 9;
            }
            else
            {
                Last.Visibility = Visibility.Collapsed;
                LastLabel.FontSize = 11;
            }
        }
        else
        {
            Last.Visibility = Visibility.Visible;
            LastLabel.FontSize = 9;
            LiveDot.Visibility = Visibility.Collapsed;
            Last.Text = s.Last != null ? Pretty.N(s.Last.ChestsOpened) : "–";
            LastLabel.Text = "ÚLTIMA";
            LastLabel.Foreground = LabelBrush;
        }

        if (_look != null) ApplyLook(_look);
        if (_first)
        {
            DisplayTotal = total;
        }
        else if (Math.Abs(total - DisplayTotal) >= 1)
        {
            CountTo(total);
        }

        // Partida terminada: hay una "última partida" nueva y es reciente (no un lote de replays viejos).
        var lastId = s.Last?.SessionId;
        if (!_first && lastId != null && lastId != _lastSessionId && s.Progress == null && s.Last is { } m
            && DateTime.UtcNow - m.When.AddSeconds(m.DurationSeconds ?? 0) < TimeSpan.FromMinutes(15))
        {
            ShowSummary(m, isRecord: s.Best != null && ReferenceEquals(s.Best, m) && s.Matches > 1);
        }
        _lastSessionId = lastId;
        _first = false;
        FitToContent();
    }

    private void CountTo(double target)
    {
        var up = target > DisplayTotal;
        BeginAnimation(DisplayTotalProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(up ? 1.4 : 0.3))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (!up) return;

        var pulse = new DoubleAnimation(1.12, 1, TimeSpan.FromSeconds(0.7)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        TotalScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        TotalScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

        var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.8) };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        Glow.BeginAnimation(OpacityProperty, glow);
    }

    /// <summary>Tarjeta "Partida terminada" debajo de la barra: cofres, tesoros, puesto y récord. Se va sola a los ~8 s.</summary>
    public void ShowSummary(MatchRecord m, bool isRecord)
    {
        var victory = m.Placement == 1;
        SummaryTitle.Text = victory ? "¡VICTORIA MAGISTRAL!" : "PARTIDA TERMINADA";
        SummaryPlace.Text = !victory && m.Placement is int p && p > 0 ? $"· PUESTO #{p}" : "";
        SummaryChests.Text = "+" + Pretty.N(m.ChestsOpened);
        SummaryChestsLabel.Text = m.ChestsOpened == 1 ? "cofre" : "cofres";

        var t = m.TreasureFinds.Count;
        SummaryTreasureBlock.Visibility = t > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryTreasures.Text = $"★ +{t}";
        SummaryTreasuresLabel.Text = t == 1 ? "tesoro" : "tesoros";
        SummaryRecordBlock.Visibility = isRecord ? Visibility.Visible : Visibility.Collapsed;

        _summaryAnim?.Stop(this);
        Summary.Visibility = Visibility.Visible;
        Expand(TimeSpan.FromSeconds(8.6));

        var sb = new Storyboard();
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.35))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8.6))));
        Storyboard.SetTarget(fade, Summary);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));
        Storyboard.SetTarget(slide, Summary);
        Storyboard.SetTargetProperty(slide, new PropertyPath("RenderTransform.Y"));

        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Completed += (_, _) =>
        {
            Summary.Visibility = Visibility.Collapsed;
            FitToContent();
        };
        FitToContent();
        _summaryAnim = sb;
        sb.Begin(this, true);
    }

    /// <summary>Para las capturas de prueba: deja el resumen fijo en pantalla.</summary>
    public void PinSummaryForShot(MatchRecord m, bool isRecord)
    {
        ShowSummary(m, isRecord);
        _summaryAnim?.Stop(this);
        Summary.Visibility = Visibility.Visible;
        Summary.Opacity = 1;
        Expand(TimeSpan.FromMinutes(1));
    }
}
