using Microsoft.Win32;

namespace MDViewer.Services;

public class ThemeService : IDisposable
{
    public event EventHandler<bool>? ThemeChanged;

    public bool IsDarkTheme { get; private set; }

    public ThemeService()
    {
        IsDarkTheme = DetectDarkTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static bool DetectDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;

        var wasDark = IsDarkTheme;
        IsDarkTheme = DetectDarkTheme();
        if (wasDark != IsDarkTheme)
        {
            ThemeChanged?.Invoke(this, IsDarkTheme);
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        GC.SuppressFinalize(this);
    }
}
