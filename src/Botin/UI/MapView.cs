using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Botin.UI;

/// <summary>
/// Mapa de la temporada con tus cofres encima: mapa de calor de todos los cofres, lugares donde caés, o el recorrido de
/// una partida. Las coordenadas del mundo se pasan a la imagen con <see cref="MapService.ToImage"/>.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public enum Modes { Heat, Landings, Route }

    private static BitmapSource? _image;
    private static DateTime _imageStamp;

    private IReadOnlyList<(double X, double Y)> _chests = Array.Empty<(double, double)>();
    private IReadOnlyList<(double X, double Y, int Count)> _landings = Array.Empty<(double, double, int)>();
    private IReadOnlyList<int[]>? _path;
    private Modes _mode = Modes.Heat;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush Dim = Fz(Color.FromArgb(0x55, 0x05, 0x07, 0x0C));
    private static readonly Brush Empty = Fz(Color.FromRgb(0x0F, 0x13, 0x1C));
    private static readonly Brush Label = Fz(Color.FromRgb(0xF2, 0xF4, 0xF8));
    private static readonly Brush LabelShadow = Fz(Color.FromArgb(0xE6, 0x00, 0x00, 0x00));
    private static readonly Brush Gold = Fz(Color.FromRgb(0xF7, 0xB9, 0x3E));
    private static readonly Brush Muted = Fz(Color.FromRgb(0x9B, 0xA4, 0xB6));
    private static readonly Pen RoutePen = new(Fz(Color.FromArgb(0xE6, 0x55, 0xB8, 0xFF)), 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    private static readonly Pen DotRing = new(Fz(Color.FromRgb(0x1A, 0x12, 0x05)), 1);
    private static readonly Brush Heat = MakeHeat();

    private static Brush Fz(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush MakeHeat()
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0xFF, 0xD3, 0x6E), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0xF7, 0x9B, 0x2E), 0.45));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xE0, 0x60, 0x1C), 1));
        b.Freeze();
        return b;
    }

    public static readonly DependencyProperty RouteMatchProperty = DependencyProperty.Register(
        nameof(RouteMatch), typeof(MatchRecord), typeof(MapView),
        new PropertyMetadata(null, (d, e) =>
        {
            if (e.NewValue is MatchRecord m)
            {
                var chests = m.Chests.Where(c => c.X.HasValue).ToList();
                ((MapView)d).SetRoute(m.Path, chests.Select(c => ((double)c.X!.Value, (double)c.Y!.Value)).ToList(),
                    chests.Count > 0 ? chests.Min(c => c.TimeSeconds) : null);
            }
        }));

    /// <summary>Partida cuyo recorrido se dibuja (para usar desde XAML).</summary>
    public MatchRecord? RouteMatch
    {
        get => (MatchRecord?)GetValue(RouteMatchProperty);
        set => SetValue(RouteMatchProperty, value);
    }

    public MapView()
    {
        ClipToBounds = true;
    }

    private BitmapSource? _heat;

    public void SetHeat(IReadOnlyList<(double X, double Y)> chests)
    {
        _mode = Modes.Heat;
        if (!ReferenceEquals(chests, _chests) || _heat == null)
        {
            _chests = chests;
            _heat = BuildHeat(chests);
        }
        InvalidateVisual();
    }

    private const int HeatGrid = 512; // celdas sobre la imagen de 2048 px (4 px cada una)

    /// <summary>
    /// Mapa de calor de verdad: se suma la densidad de cofres en una grilla (con un núcleo gaussiano) y se pinta
    /// de transparente a naranja, amarillo y blanco. Con miles de cofres no se satura como los puntos sueltos.
    /// </summary>
    private static BitmapSource? BuildHeat(IReadOnlyList<(double X, double Y)> chests)
    {
        if (chests.Count == 0) return null;
        var grid = new float[HeatGrid * HeatGrid];
        const int r = 5;
        var kernel = new float[(2 * r + 1) * (2 * r + 1)];
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            kernel[(dy + r) * (2 * r + 1) + dx + r] = (float)Math.Exp(-(dx * dx + dy * dy) / (2.0 * 2.2 * 2.2));
        }
        foreach (var (x, y) in chests)
        {
            var (px, py) = MapService.ToImage(x, y);
            var cx = (int)(px / MapService.ImageSize * HeatGrid);
            var cy = (int)(py / MapService.ImageSize * HeatGrid);
            for (var dy = -r; dy <= r; dy++)
            {
                var yy = cy + dy;
                if (yy < 0 || yy >= HeatGrid) continue;
                for (var dx = -r; dx <= r; dx++)
                {
                    var xx = cx + dx;
                    if (xx < 0 || xx >= HeatGrid) continue;
                    grid[yy * HeatGrid + xx] += kernel[(dy + r) * (2 * r + 1) + dx + r];
                }
            }
        }
        // Se normaliza con el percentil 99 (no con el máximo) para que un solo lugar muy usado no apague el resto.
        var nonZero = grid.Where(v => v > 0.05f).OrderBy(v => v).ToArray();
        if (nonZero.Length == 0) return null;
        var top = Math.Max(0.5f, nonZero[(int)(nonZero.Length * 0.95)]);

        var pixels = new byte[HeatGrid * HeatGrid * 4];
        for (var i = 0; i < grid.Length; i++)
        {
            var t = Math.Clamp(grid[i] / top, 0, 1);
            if (t < 0.02) continue;
            t = MathF.Sqrt(t);
            // Escala: naranja oscuro → naranja → amarillo → blanco cálido.
            byte R, G, B;
            if (t < 0.5) { R = 0xE8; G = (byte)(0x55 + (0xA8 - 0x55) * (t / 0.5)); B = 0x1C; }
            else if (t < 0.85) { R = 0xFF; G = (byte)(0xA8 + (0xE0 - 0xA8) * ((t - 0.5) / 0.35)); B = (byte)(0x1C + 0x40 * ((t - 0.5) / 0.35)); }
            else { R = 0xFF; G = (byte)(0xE0 + 0x1C * ((t - 0.85) / 0.15)); B = (byte)(0x5C + 0x90 * ((t - 0.85) / 0.15)); }
            var a = (byte)(255 * Math.Min(0.95, 0.4 + 0.6 * t));
            // BGRA premultiplicado.
            pixels[i * 4 + 0] = (byte)(B * a / 255);
            pixels[i * 4 + 1] = (byte)(G * a / 255);
            pixels[i * 4 + 2] = (byte)(R * a / 255);
            pixels[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(HeatGrid, HeatGrid, 96, 96, PixelFormats.Pbgra32, null, pixels, HeatGrid * 4);
        bmp.Freeze();
        return bmp;
    }

    public void SetLandings(IReadOnlyList<(double X, double Y, int Count)> landings)
    {
        _mode = Modes.Landings;
        _landings = landings;
        InvalidateVisual();
    }

    /// <param name="from">Segundo desde el que se dibuja (el aterrizaje): así no aparece el vuelo del autobús.</param>
    public void SetRoute(IReadOnlyList<int[]>? path, IReadOnlyList<(double X, double Y)> chests, float? from)
    {
        _mode = Modes.Route;
        _path = path == null ? null : from is float f ? path.Where(p => p[0] >= f - 15).ToList() : path;
        _chests = chests;
        InvalidateVisual();
    }

    /// <summary>La imagen se carga una vez (achicada a 1024 px) y se recarga si se bajó una nueva.</summary>
    private static BitmapSource? Image()
    {
        try
        {
            if (!MapService.HasImage) return null;
            var stamp = File.GetLastWriteTimeUtc(MapService.ImagePath);
            if (_image != null && stamp == _imageStamp) return _image;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1024;
            bmp.UriSource = new Uri(MapService.ImagePath);
            bmp.EndInit();
            bmp.Freeze();
            _image = bmp;
            _imageStamp = stamp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Zona de la imagen (en px de 2048) a mostrar: la isla, más lo que haga falta para ver todos los puntos.</summary>
    private Rect Viewport()
    {
        var pts = new List<(double, double)>();
        pts.AddRange(MapService.Pois.Select(p => MapService.ToImage(p.X, p.Y)));
        if (_mode == Modes.Route && _path is { Count: > 0 })
        {
            // En una partida: centrado en el recorrido, con margen.
            var route = _path.Select(p => MapService.ToImage(p[1], p[2])).Concat(_chests.Select(c => MapService.ToImage(c.X, c.Y))).ToList();
            pts = route;
        }
        else
        {
            pts.AddRange(_chests.Select(c => MapService.ToImage(c.X, c.Y)));
        }
        if (pts.Count == 0) return new Rect(500, 500, 1050, 1050);

        var minX = pts.Min(p => p.Item1);
        var maxX = pts.Max(p => p.Item1);
        var minY = pts.Min(p => p.Item2);
        var maxY = pts.Max(p => p.Item2);
        var margin = _mode == Modes.Route ? 70 : 110;
        var r = new Rect(minX - margin, minY - margin, maxX - minX + 2 * margin, maxY - minY + 2 * margin);

        // Misma proporción que el control, sin deformar.
        var aspect = ActualWidth / Math.Max(1, ActualHeight);
        if (r.Width / r.Height < aspect)
        {
            var w = r.Height * aspect;
            r = new Rect(r.X - (w - r.Width) / 2, r.Y, w, r.Height);
        }
        else
        {
            var h = r.Width / aspect;
            r = new Rect(r.X, r.Y - (h - r.Height) / 2, r.Width, h);
        }
        if (_mode == Modes.Route && r.Width < 420)
        {
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            r = new Rect(c.X - 210, c.Y - 210 / aspect, 420, 420 / aspect);
        }
        // Que no se salga de la imagen (si no, queda una franja vacía en el borde).
        var size = MapService.ImageSize;
        if (r.Width > size) r = new Rect(r.X, r.Y, size, size / aspect);
        if (r.Height > size) r = new Rect(r.X, r.Y, size * aspect, size);
        r.X = Math.Clamp(r.X, 0, size - r.Width);
        r.Y = Math.Clamp(r.Y, 0, size - r.Height);
        return r;
    }

    private FormattedText Txt(string s, double size, Brush b) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 10 || bounds.Height < 10) return;
        dc.PushClip(new RectangleGeometry(bounds, 12, 12));
        dc.DrawRectangle(Empty, null, bounds);

        var view = Viewport();
        var k = bounds.Width / view.Width;
        Point P(double x, double y)
        {
            var (px, py) = MapService.ToImage(x, y);
            return new Point((px - view.X) * k, (py - view.Y) * k);
        }

        var img = Image();
        if (img != null)
        {
            dc.DrawImage(img, new Rect(-view.X * k, -view.Y * k, MapService.ImageSize * k, MapService.ImageSize * k));
            dc.DrawRectangle(Dim, null, bounds);
        }
        else
        {
            var t = Txt("Mapa sin descargar: se muestran solo los puntos", 11.5, Muted);
            dc.DrawText(t, new Point(10, bounds.Height - t.Height - 8));
        }

        switch (_mode)
        {
            case Modes.Heat:
                if (_heat != null)
                {
                    RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                    dc.DrawImage(_heat, new Rect(-view.X * k, -view.Y * k, MapService.ImageSize * k, MapService.ImageSize * k));
                }
                break;

            case Modes.Landings:
                var max = Math.Max(1, _landings.Count == 0 ? 1 : _landings.Max(l => l.Count));
                foreach (var l in _landings.OrderBy(l => l.Count))
                {
                    var rr = 5 + 13 * Math.Sqrt((double)l.Count / max);
                    var p = P(l.X, l.Y);
                    dc.DrawEllipse(Fz(Color.FromArgb(0x55, 0xF7, 0xB9, 0x3E)), null, p, rr + 4, rr + 4);
                    dc.DrawEllipse(Gold, DotRing, p, rr, rr);
                    var n = Txt(l.Count.ToString(), rr > 10 ? 11 : 9.5, Fz(Color.FromRgb(0x1A, 0x12, 0x05)));
                    if (rr >= 8) dc.DrawText(n, new Point(p.X - n.Width / 2, p.Y - n.Height / 2));
                }
                break;

            case Modes.Route:
                if (_path is { Count: > 1 })
                {
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(P(_path[0][1], _path[0][2]), false, false);
                        for (var i = 1; i < _path.Count; i++)
                        {
                            g.LineTo(P(_path[i][1], _path[i][2]), true, true);
                        }
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, RoutePen, geo);
                }
                foreach (var c in _chests)
                {
                    dc.DrawEllipse(Gold, DotRing, P(c.X, c.Y), 3.6, 3.6);
                }
                if (_path is { Count: > 0 })
                {
                    var end = P(_path[^1][1], _path[^1][2]);
                    dc.DrawEllipse(Fz(Color.FromRgb(0xFF, 0x5C, 0x6C)), DotRing, end, 5, 5);
                }
                break;
        }

        // Nombres de los lugares (la imagen de la API no los trae).
        foreach (var poi in MapService.Pois)
        {
            var p = P(poi.X, poi.Y);
            if (p.X < -40 || p.Y < -10 || p.X > bounds.Width + 40 || p.Y > bounds.Height + 10) continue;
            var size = _mode == Modes.Route ? 10.5 : 10;
            var t = Txt(poi.Name.ToUpper(CultureInfo.CurrentCulture), size, Label);
            var at = new Point(p.X - t.Width / 2, p.Y - t.Height / 2);
            var sh = Txt(poi.Name.ToUpper(CultureInfo.CurrentCulture), size, LabelShadow);
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (1, 1) })
            {
                dc.DrawText(sh, new Point(at.X + dx, at.Y + dy));
            }
            dc.DrawText(t, at);
        }
        dc.Pop();
    }
}
