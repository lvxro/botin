using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Botin.UI;

internal static class Native
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;

    /// <summary>Barra de título oscura del mismo color que la ventana (Windows 11; en Windows 10 queda oscura nomás).</summary>
    public static void DarkTitleBar(IntPtr hwnd, uint rgb)
    {
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        var bgr = (int)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
        DwmSetWindowAttribute(hwnd, 35, ref bgr, 4); // color del título
        DwmSetWindowAttribute(hwnd, 34, ref bgr, 4); // color del borde
    }

    /// <summary>El overlay no aparece en Alt+Tab, no roba el foco y, fijado, deja pasar el mouse.</summary>
    public static void SetOverlayStyle(IntPtr hwnd, bool clickThrough)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        ex = clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Vuelve a poner el overlay arriba de todo (algunos juegos se suben por encima).</summary>
    public static void BringToTop(IntPtr hwnd) => SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Con "cerrar con Fortnite", al iniciar Windows arranca solo el vigía (--watch), que abre Botín cuando abrís Fortnite.</summary>
    public static void SetStartWithWindows(bool on, bool watchOnly = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (on && Environment.ProcessPath is { } exe) key.SetValue("Botin", $"\"{exe}\" {(watchOnly ? "--watch" : "--tray")}");
            else key.DeleteValue("Botin", throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
