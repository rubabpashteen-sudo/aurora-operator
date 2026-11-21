using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AuroraOperator.App.Services;

internal static class WindowBackdrop
{
    internal static void Apply(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast ||
            PresentationSource.FromVisual(window) is not HwndSource source || source.CompositionTarget is null) return;
        const int useImmersiveDarkMode = 20;
        const int captionColor = 35;
        const int textColor = 36;
        const int systemBackdropType = 38;
        int immersiveDark = dark ? 1 : 0;
        int caption = dark ? 0x0030211B : 0x00F8EEE7;
        int text = dark ? 0x00FFF5F1 : 0x00402619;
        int desktopAcrylic = 3;
        _ = DwmSetWindowAttribute(source.Handle, useImmersiveDarkMode, ref immersiveDark, sizeof(int));
        _ = DwmSetWindowAttribute(source.Handle, captionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(source.Handle, textColor, ref text, sizeof(int));
        if (DwmSetWindowAttribute(source.Handle, systemBackdropType, ref desktopAcrylic, sizeof(int)) != 0) return;

        var margins = new GlassMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(source.Handle, ref margins) != 0) return;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassMargins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref GlassMargins margins);
}
