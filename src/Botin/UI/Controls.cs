using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Botin.UI;

/// <summary>Gráfico de barras propio: barras redondeadas, línea de promedio, estrella en partidas con tesoro y tooltip al pasar el mouse.</summary>
public sealed class BarChart : FrameworkElement
{
    private IReadOnlyList<ChartPoint> _points = Array.Empty<ChartPoint>();
    private int _hover = -1;

    private static readonly Brush BarBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0xD3, 0x6E), Color.FromRgb(0xE0, 0x90, 0x1C), 90));
    private static readonly Brush BarDim = Frozen(new LinearGradientBrush(Color.FromArgb(0xB0, 0xF7, 0xB9, 0x3E), Color.FromArgb(0x70, 0xE0, 0x90, 0x1C), 90));
    private static readonly Brush LiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6C)));
    private static readonly Brush Purple = Frozen(new SolidColorBrush(Color.FromRgb(0xB9, 0x94, 0xFF)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x77)));
    private static readonly Brush HoverCol = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GoldText = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xB9, 0x3E)));
    private static readonly Brush MutedLight = Frozen(new SolidColorBrush(Color.FromRgb(0x9B, 0xA4, 0xB6)));
    private static readonly Pen RecordPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xB8)), 1.5));
    private static readonly Brush TipBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x27, 0x39)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF5)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x33)), 1));
    private static readonly Pen AvgPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0x8C, 0x95, 0xA8)), 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NumFace = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public BarChart()
    {
        Cursor = Cursors.Arrow;
    }

    /// <summary>Los valores son minutos jugados (ejes y textos en horas) en lugar de cofres.</summary>
    private bool _time;

    public void SetData(IReadOnlyList<ChartPoint> points, bool time = false)
    {
        _points = points;
        _time = time;
        InvalidateVisual();
    }

    private string Fmt(double v) => _time ? Pretty.Minutes(v) : Pretty.N(v);

    private string Axis(int v) => !_time ? Pretty.N(v) : v % 60 == 0 ? $"{v / 60} h" : $"{v / 60}:{v % 60:00}";

    /// <summary>Paso del eje para minutos: 15 o 30 min, o horas enteras.</summary>
    private static int TimeStep(int max) => new[] { 15, 30, 60, 120, 180, 240, 360 }.FirstOrDefault(s => max / s <= 5, 480);

    private const double AxisW = 38;
    private double BottomH => _points.Any(p => p.Axis != null) ? 24 : 6;

    private Rect Plot => new(AxisW, 22, Math.Max(0, ActualWidth - AxisW), Math.Max(0, ActualHeight - 22 - BottomH));

    /// <summary>Paso "redondo" (1, 2 o 5 × 10^n) para unas 4 líneas de guía.</summary>
    private static int NiceStep(int max)
    {
        var raw = max / 4.0;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1))));
        var n = raw / pow;
        var mult = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return Math.Max(1, (int)(mult * pow));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var plot = Plot;
        var i = -1;
        if (_points.Count > 0 && p.X >= plot.Left)
        {
            var slot = plot.Width / Math.Max(_points.Count, 12);
            i = (int)((p.X - plot.Left) / slot);
            if (i >= _points.Count) i = -1;
        }
        if (i != _hover)
        {
            _hover = i;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    private FormattedText Text(string s, double size, Brush b, Typeface? face = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = Plot;
        if (plot.Width <= 0 || plot.Height <= 0) return;

        if (_points.Count == 0)
        {
            var t = Text("Las barras aparecen después de tu primera partida", 13, Muted);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
            return;
        }

        var max = Math.Max(_time ? 30 : 5, _points.Max(p => p.Value));
        var step = _time ? TimeStep(max) : NiceStep(max);
        var top = (int)Math.Ceiling(max / (double)step) * step;

        for (var v = 0; v <= top; v += step)
        {
            var y = Math.Round(plot.Bottom - v / (double)top * plot.Height) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var lt = Text(Axis(v), 10.5, Muted, NumFace);
            dc.DrawText(lt, new Point(AxisW - 8 - lt.Width, y - lt.Height / 2));
        }

        var count = Math.Max(_points.Count, 12);
        var slot = plot.Width / count;
        var barW = Math.Clamp(slot * 0.62, 4, 28);
        var finished = _points.Where(p => !p.Live).ToList();

        if (_hover >= 0)
        {
            dc.DrawRoundedRectangle(HoverCol, null, new Rect(plot.Left + _hover * slot + 1, plot.Top, slot - 2, plot.Height), 6, 6);
        }

        var lastFinished = -1;
        for (var i = _points.Count - 1; i >= 0; i--)
        {
            if (!_points[i].Live) { lastFinished = i; break; }
        }

        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var h = Math.Max(p.Value / (double)top * plot.Height, 3);
            var x = plot.Left + i * slot + (slot - barW) / 2;
            var r = new Rect(x, plot.Bottom - h, barW, h);
            var brush = p.Live ? LiveBrush : (_hover == -1 || _hover == i ? BarBrush : BarDim);
            dc.DrawRoundedRectangle(brush, p.Record ? RecordPen : null, r, Math.Min(5, barW / 2), Math.Min(5, barW / 2));
            if (p.Axis != null)
            {
                var ax = Text(p.Axis, 10.5, _hover == i ? TextBrush : Muted, NumFace);
                if (ax.Width < slot + 6) dc.DrawText(ax, new Point(plot.Left + i * slot + (slot - ax.Width) / 2, plot.Bottom + 6));
            }
            var labelTop = r.Top;
            if (p.Treasures > 0)
            {
                var star = Text("★", 12, Purple);
                labelTop -= star.Height + 1;
                dc.DrawText(star, new Point(x + (barW - star.Width) / 2, labelTop));
            }
            // Valor escrito solo donde importa: tu récord y la última partida.
            if ((p.Record || i == lastFinished || p.Live) && _hover == -1)
            {
                var v = Text(_time ? (p.Value < 60 ? $"{p.Value} min" : $"{p.Value / 60} h {p.Value % 60:00}") : Pretty.N(p.Value), 11.5, p.Live ? LiveBrush : p.Record ? GoldText : MutedLight, NumFace);
                labelTop -= v.Height;
                dc.DrawText(v, new Point(x + (barW - v.Width) / 2, labelTop));
            }
        }

        if (finished.Count > 1)
        {
            var avg = finished.Average(p => p.Value);
            var y = Math.Round(plot.Bottom - avg / top * plot.Height) + 0.5;
            dc.DrawLine(AvgPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var al = Text($"prom. {(_time ? Pretty.Minutes(avg) : Pretty.N(avg, 1))}", 10.5, MutedLight, NumFace);
            var bg = new Rect(plot.Right - al.Width - 10, y - al.Height / 2 - 1, al.Width + 8, al.Height + 2);
            dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x22))), null, bg, 4, 4);
            dc.DrawText(al, new Point(bg.Left + 4, bg.Top + 1));
        }

        if (_hover >= 0 && _hover < _points.Count)
        {
            var p = _points[_hover];
            var title = Text(_time
                ? $"{Fmt(p.Value)} jugando" + (p.Live ? "  · en curso" : p.Record ? "  · tu día más largo" : "")
                : $"{p.Value} {(p.Value == 1 ? "cofre" : "cofres")}" + (p.Treasures > 0 ? $"  ★ {p.Treasures}" : "") + (p.Record ? "  · récord" : ""), 13, TextBrush);
            var sub = Text(p.Label, 11, Muted);
            var w = Math.Max(title.Width, sub.Width) + 20;
            var hgt = title.Height + sub.Height + 14;
            var cx = plot.Left + _hover * slot + slot / 2;
            var tx = Math.Clamp(cx - w / 2, 0, ActualWidth - w);
            var tip = new Rect(tx, 0, w, hgt);
            dc.DrawRoundedRectangle(TipBg, null, tip, 8, 8);
            dc.DrawText(title, new Point(tip.Left + 10, tip.Top + 6));
            dc.DrawText(sub, new Point(tip.Left + 10, tip.Top + 8 + title.Height));
        }
    }
}

/// <summary>Paso numerado del estado vacío.</summary>
public sealed class StepRow : ContentControl
{
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = (Brush)Application.Current.FindResource("GoldSoft"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = Number, FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = (Brush)Application.Current.FindResource("Gold"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { Margin = new Thickness(14, 2, 0, 0) };
        text.Children.Add(new TextBlock { Text = Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = Detail, Style = (Style)Application.Current.FindResource("Sub"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        Content = grid;
    }
}

/// <summary>Línea de tiempo de una partida: cada cofre en su minuto (color por tipo) y los tesoros como estrellas.</summary>
public sealed class MatchTimeline : FrameworkElement
{
    public static readonly DependencyProperty MatchProperty = DependencyProperty.Register(
        nameof(Match), typeof(MatchRecord), typeof(MatchTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MatchRecord? Match
    {
        get => (MatchRecord?)GetValue(MatchProperty);
        set => SetValue(MatchProperty, value);
    }

    private double _mouseX = -1;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NumFace = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Track = Fz(Color.FromRgb(0x1F, 0x26, 0x36));
    private static readonly Brush Tick = Fz(Color.FromRgb(0x73, 0x7D, 0x93));
    private static readonly Brush Purple = Fz(Color.FromRgb(0xB9, 0x94, 0xFF));
    private static readonly Brush TipBg = Fz(Color.FromRgb(0x26, 0x2F, 0x44));
    private static readonly Brush TextB = Fz(Color.FromRgb(0xEC, 0xEF, 0xF5));
    private static readonly Pen Cursor = new(Fz(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1);

    private static Brush Fz(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush KindBrush(string kind) => Snapshot.ColorKey(kind) switch
    {
        "Rare" => Fz(Color.FromRgb(0x55, 0xB8, 0xFF)),
        "Buried" => Fz(Color.FromRgb(0xC0, 0x8A, 0x5B)),
        "Sprite" => Fz(Color.FromRgb(0xB9, 0x94, 0xFF)),
        "Normal" => Fz(Color.FromRgb(0xF7, 0xB9, 0x3E)),
        _ => Fz(Color.FromRgb(0x9B, 0xA4, 0xB6)),
    };

    public MatchTimeline()
    {
        Height = 74;
        MouseMove += (_, e) => { _mouseX = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { _mouseX = -1; InvalidateVisual(); };
    }

    private FormattedText Txt(string s, double size, Brush b, Typeface f) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, f, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var m = Match;
        if (m == null || ActualWidth < 50) return;

        var times = m.Chests.Select(c => (double)c.TimeSeconds).Concat(m.TreasureFinds.Where(t => t.ObtainedAtSeconds.HasValue).Select(t => (double)t.ObtainedAtSeconds!.Value)).ToList();
        var end = Math.Max(m.DurationSeconds ?? 0, times.Count > 0 ? times.Max() + 30 : 0);
        if (end <= 0) end = 60;

        const double left = 4, trackY = 40;
        var width = ActualWidth - 8;
        double X(double t) => left + t / end * width;

        dc.DrawRoundedRectangle(Track, null, new Rect(left, trackY - 2, width, 4), 2, 2);

        var step = end > 1500 ? 300 : end > 600 ? 120 : 60;
        for (var t = 0; t <= end; t += step)
        {
            var x = X(t);
            dc.DrawRectangle(Tick, null, new Rect(x, trackY + 6, 1, 4));
            var lt = Txt(Pretty.Clock(t), 10, Tick, NumFace);
            if (x + lt.Width / 2 < ActualWidth) dc.DrawText(lt, new Point(Math.Max(0, x - lt.Width / 2), trackY + 12));
        }

        foreach (var c in m.Chests)
        {
            var x = X(c.TimeSeconds);
            var b = KindBrush(c.Kind);
            var r = new Rect(x - 3, trackY - 9, 6, 18);
            if (c.Destroyed) dc.DrawRoundedRectangle(null, new Pen(b, 1.5), r, 2, 2);
            else dc.DrawRoundedRectangle(b, null, r, 2, 2);
        }

        foreach (var t in m.TreasureFinds.Where(t => t.ObtainedAtSeconds.HasValue))
        {
            var star = Txt("★", 15, Purple, Face);
            dc.DrawText(star, new Point(X(t.ObtainedAtSeconds!.Value) - star.Width / 2, trackY - 30));
        }

        if (_mouseX >= 0)
        {
            var at = Math.Clamp((_mouseX - left) / width * end, 0, end);
            var near = m.Chests.Select(c => (T: (double)c.TimeSeconds, Text: L.Name(c.Kind) + (c.Destroyed ? " (destruido)" : "")))
                .Concat(m.TreasureFinds.Where(t => t.ObtainedAtSeconds.HasValue).Select(t => (T: (double)t.ObtainedAtSeconds!.Value, Text: "★ " + t.DisplayName)))
                .OrderBy(e => Math.Abs(e.T - at)).FirstOrDefault();
            if (near.Text != null && Math.Abs(X(near.T) - _mouseX) < 14)
            {
                var x = X(near.T);
                dc.DrawLine(Cursor, new Point(x, 0), new Point(x, trackY + 10));
                var tip = Txt($"{Pretty.Clock((float)near.T)}  ·  {near.Text}", 11.5, TextB, Face);
                var rx = Math.Clamp(x + 8, 0, ActualWidth - tip.Width - 14);
                dc.DrawRoundedRectangle(TipBg, null, new Rect(rx, 0, tip.Width + 14, tip.Height + 6), 6, 6);
                dc.DrawText(tip, new Point(rx + 7, 3));
            }
        }
    }
}

/// <summary>Cofres por minuto (barras con color de calor) + % acumulado del total (línea azul, eje derecho).</summary>
public sealed class MinuteChart : FrameworkElement
{
    private IReadOnlyList<MinuteStat> _data = Array.Empty<MinuteStat>();
    private int _hover = -1;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NumFace = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Color Cold = Color.FromRgb(0x4A, 0x3A, 0x1E), Hot = Color.FromRgb(0xFF, 0xC9, 0x4D);
    private static readonly Brush Muted = Fz(Color.FromRgb(0x73, 0x7D, 0x93));
    private static readonly Brush Blue = Fz(Color.FromRgb(0x55, 0xB8, 0xFF));
    private static readonly Brush TipBg = Fz(Color.FromRgb(0x1F, 0x27, 0x39));
    private static readonly Brush TextB = Fz(Color.FromRgb(0xEC, 0xEF, 0xF5));
    private static readonly Brush HoverCol = Fz(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush EarlyBand = Fz(Color.FromArgb(0x0C, 0xF7, 0xB9, 0x3E));
    private static readonly Pen GridPen = new(Fz(Color.FromRgb(0x1C, 0x23, 0x33)), 1);
    private static readonly Pen LinePen = new(Fz(Color.FromRgb(0x55, 0xB8, 0xFF)), 2) { LineJoin = PenLineJoin.Round };

    private static Brush Fz(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public MinuteChart()
    {
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            var plot = Plot;
            var i = _data.Count == 0 || p.X < plot.Left ? -1 : (int)((p.X - plot.Left) / (plot.Width / _data.Count));
            if (i >= _data.Count) i = -1;
            if (i != _hover) { _hover = i; InvalidateVisual(); }
        };
        MouseLeave += (_, _) => { _hover = -1; InvalidateVisual(); };
    }

    public void SetData(IReadOnlyList<MinuteStat> data)
    {
        _data = data;
        InvalidateVisual();
    }

    private Rect Plot => new(34, 28, Math.Max(0, ActualWidth - 34 - 40), Math.Max(0, ActualHeight - 28 - 24));

    private FormattedText Txt(string s, double size, Brush b, Typeface? f = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, f ?? Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var c = Color.FromRgb((byte)(Cold.R + (Hot.R - Cold.R) * t), (byte)(Cold.G + (Hot.G - Cold.G) * t), (byte)(Cold.B + (Hot.B - Cold.B) * t));
        return Fz(c);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var plot = Plot;
        if (plot.Width <= 0 || plot.Height <= 0) return;
        if (_data.Count == 0)
        {
            var t = Txt("Necesito al menos 3 partidas con cofres para armar el mapa", 13, Muted);
            dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
            return;
        }

        var max = Math.Max(0.5, _data.Max(d => d.Average));
        var top = Math.Ceiling(max); // entero, así la mitad es x,5 o entera
        var slot = plot.Width / _data.Count;
        var barW = Math.Clamp(slot * 0.7, 4, 34);

        // Franja de los primeros minutos (el aterrizaje).
        var early = Math.Min(LootCurve.EarlyMinutes, _data.Count);
        dc.DrawRoundedRectangle(EarlyBand, null, new Rect(plot.Left, plot.Top - 24, early * slot, plot.Height + 24), 6, 6);
        var el = Txt("ATERRIZAJE", 9.5, Muted);
        dc.DrawText(el, new Point(plot.Left + (early * slot - el.Width) / 2, plot.Top - 20));

        foreach (var v in new[] { 0, top / 2, top })
        {
            var y = Math.Round(plot.Bottom - v / top * plot.Height) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var lt = Txt(Pretty.N(v, v % 1 == 0 ? 0 : 1), 10.5, Muted, NumFace);
            dc.DrawText(lt, new Point(plot.Left - 8 - lt.Width, y - lt.Height / 2));
        }
        foreach (var pct in new[] { 0, 50, 100 })
        {
            var y = plot.Bottom - pct / 100.0 * plot.Height;
            var lt = Txt($"{pct} %", 10.5, Blue, NumFace);
            dc.DrawText(lt, new Point(plot.Right + 8, y - lt.Height / 2));
        }

        if (_hover >= 0) dc.DrawRoundedRectangle(HoverCol, null, new Rect(plot.Left + _hover * slot + 1, plot.Top, slot - 2, plot.Height), 6, 6);

        for (var i = 0; i < _data.Count; i++)
        {
            var d = _data[i];
            var h = Math.Max(d.Average / top * plot.Height, 2);
            var x = plot.Left + i * slot + (slot - barW) / 2;
            dc.DrawRoundedRectangle(Heat(d.Average / max), null, new Rect(x, plot.Bottom - h, barW, h), Math.Min(4, barW / 2), Math.Min(4, barW / 2));
            if (d.Minute % 2 == 0 || slot > 26)
            {
                var lt = Txt(d.Minute.ToString(), 10.5, _hover == i ? TextB : Muted, NumFace);
                dc.DrawText(lt, new Point(plot.Left + i * slot + (slot - lt.Width) / 2, plot.Bottom + 6));
            }
        }

        // % acumulado.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(plot.Left, plot.Bottom), false, false);
            for (var i = 0; i < _data.Count; i++)
            {
                g.LineTo(new Point(plot.Left + (i + 1) * slot, plot.Bottom - _data[i].CumulativePct / 100 * plot.Height), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, LinePen, geo);

        if (_hover >= 0)
        {
            var d = _data[_hover];
            var cx = plot.Left + (_hover + 1) * slot;
            dc.DrawEllipse(Blue, null, new Point(cx, plot.Bottom - d.CumulativePct / 100 * plot.Height), 4, 4);
            var title = Txt($"Minuto {d.Minute}–{d.Minute + 1}: {Pretty.N(d.Average, 1)} cofres por partida", 12.5, TextB);
            var sub = Txt($"Hasta acá: {Pretty.N(d.CumulativePct, 0)} % de tus cofres · {d.Matches} partidas", 11, Muted);
            var w = Math.Max(title.Width, sub.Width) + 20;
            var tx = Math.Clamp(plot.Left + _hover * slot + slot / 2 - w / 2, 0, ActualWidth - w);
            dc.DrawRoundedRectangle(TipBg, null, new Rect(tx, 0, w, title.Height + sub.Height + 14), 8, 8);
            dc.DrawText(title, new Point(tx + 10, 6));
            dc.DrawText(sub, new Point(tx + 10, 8 + title.Height));
        }
    }
}
