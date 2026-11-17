using System.Windows;
using System.Windows.Interop;

namespace MDViewer.Services;

/// <summary>
/// Applies the effective theme to the WPF interface: swaps the theme
/// dictionary (Themes/Light.xaml or Themes/Dark.xaml) in the application
/// resources and sets the Windows title bar of every window to match.
/// </summary>
public static class ThemeApplier
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 and later)
    private const int DwmUseImmersiveDarkMode = 20;

    public static bool IsDark { get; private set; }

    /// <summary>Must run on the UI thread.</summary>
    public static void Apply(bool isDark)
    {
        var app = Application.Current;
        if (app == null) return;

        var merged = app.Resources.MergedDictionaries;
        var current = merged.FirstOrDefault(IsThemeDictionary);
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(isDark ? "Dark" : "Light")}.xaml")
        };

        if (current != null)
            merged[merged.IndexOf(current)] = next;
        else
            merged.Insert(0, next);

        IsDark = isDark;
        foreach (Window window in app.Windows)
            ApplyTitleBar(window);
    }

    /// <summary>Dark or light title bar for <paramref name="window"/>, following the current theme.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var value = IsDark ? 1 : 0;
        try
        {
            NativeMethods.DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows without the attribute: keep the default title bar.
        }
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? "";
        return source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase);
    }
}
