using System.Windows.Media;

namespace Botin.UI;

/// <summary>
/// Todos los colores de Botín en un solo lugar. App.xaml los publica como recursos con el mismo nombre.
/// Se armaron en OKLCH: neutros con tinte azul noche, superficies que se aclaran con la elevación, una escala de oro
/// con un paso para texto (400) y otro para barras y anillos (600), y colores de datos (oro, turquesa, violeta)
/// validados para daltonismo y contraste sobre las tarjetas. Verde y rojo quedan solo para mejor y peor.
/// </summary>
public static class Palette
{
    // Superficies, de atrás hacia adelante.
    public static readonly Color BgColor = Rgb(0x090E18);
    public static readonly Brush Bg = B(BgColor);
    public static readonly Brush Surface = B(Rgb(0x131926));
    public static readonly Brush Surface2 = B(Rgb(0x1B2330));
    public static readonly Brush SurfaceHover = B(Rgb(0x262E3D));
    public static readonly Brush Line = B(Rgb(0x323B4A));

    // Texto: principal, secundario y tenue (los tres superan 4,5:1 sobre cualquier superficie).
    public static readonly Brush Text = B(Rgb(0xEDF0F6));
    public static readonly Brush Muted = B(Rgb(0xA9B2C1));
    public static readonly Brush Dim = B(Rgb(0x919CAF));

    // Oro: 300 brillo · 400 acento · 500 etiquetas · 600 datos · 700 sombra · 900 texto sobre oro.
    public static readonly Color Gold300 = Rgb(0xFFDA7C), Gold400 = Rgb(0xFCBB39), Gold500 = Rgb(0xEE9E10),
        Gold600 = Rgb(0xC48800), Gold700 = Rgb(0xA0600B), Gold900 = Rgb(0x412805), GoldTintColor = Rgb(0x2A1C07);
    public static readonly Brush GoldLight = B(Gold300);
    public static readonly Brush Gold = B(Gold400);
    public static readonly Brush GoldLabel = B(Gold500);
    public static readonly Brush GoldData = B(Gold600);
    public static readonly Brush GoldInk = B(Gold900);
    public static readonly Brush GoldTint = B(GoldTintColor);
    public static readonly Brush GoldSoft = B(Alpha(Gold400, 0x2E));

    // Tesoros y ritmo: un paso para texto y otro para marcas.
    public static readonly Brush Purple = B(Rgb(0xBDA6FF));
    public static readonly Brush PurpleData = B(Rgb(0x8F6CE0));
    public static readonly Brush PurpleSoft = B(Alpha(Rgb(0xBDA6FF), 0x26));
    public static readonly Color TealColor = Rgb(0x60DBD4), TealDataColor = Rgb(0x04A19B);
    public static readonly Brush Teal = B(TealColor);
    public static readonly Brush TealData = B(TealDataColor);

    // Racha (fuego): el mismo naranja que el aviso de la paleta.
    public static readonly Color FireColor = Rgb(0xF99549);
    public static readonly Brush Fire = B(FireColor);

    // Estados (reservados): mejor y peor que tu promedio, en partida, errores.
    public static readonly Color GreenColor = Rgb(0x59D483), RedColor = Rgb(0xFA6863);
    public static readonly Brush Green = B(GreenColor);
    public static readonly Brush Red = B(RedColor);
    public static readonly Brush UpBg = B(Alpha(GreenColor, 0x24));
    public static readonly Brush DownBg = B(Alpha(RedColor, 0x24));

    /// <summary>Números grandes: degradé de oro 300 → 400 → 600.</summary>
    public static readonly Brush GoldText = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Gold300, 0), new(Gold400, 0.5), new(Gold600, 1),
    }, 90));

    /// <summary>Fondo de la tarjeta del total: un tinte de oro que se apaga hacia la superficie.</summary>
    public static readonly Brush HeroBg = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(GoldTintColor, 0), new(Mix(GoldTintColor, Rgb(0x131926), 0.55), 0.6), new(Rgb(0x131926), 1),
    }, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)));

    public static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static Brush B(Color c) => Freeze(new SolidColorBrush(c));

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Freeze<T>(T f) where T : System.Windows.Freezable
    {
        f.Freeze();
        return f;
    }
}
