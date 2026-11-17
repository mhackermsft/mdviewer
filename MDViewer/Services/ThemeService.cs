using Microsoft.Win32;

namespace MDViewer.Services;

/// <summary>Theme chosen in the app. Named to avoid System.Windows.ThemeMode.</summary>
public enum AppThemeMode
{
    Light,
    Dark,
    System,
}

public class ThemeService : IDisposable
{
    /// <summary>Raised when the effective theme changes; the argument is "is dark".</summary>
    public event EventHandler<bool>? ThemeChanged;

    /// <summary>The user's choice (persisted).</summary>
    public AppThemeMode Mode { get; private set; }

    /// <summary>The effective theme: the fixed choice, or Windows' app theme in System mode.</summary>
    public bool IsDarkTheme { get; private set; }

    public ThemeService()
    {
        Mode = SettingsService.Theme;
        IsDarkTheme = ResolveIsDark(Mode);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Effective theme for <paramref name="mode"/>.</summary>
    public static bool ResolveIsDark(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => false,
        AppThemeMode.Dark => true,
        _ => DetectDarkTheme(),
    };

    /// <summary>
    /// Changes and persists the choice; raises <see cref="ThemeChanged"/> when
    /// the effective theme changes.
    /// </summary>
    public void SetMode(AppThemeMode mode)
    {
        if (mode == Mode) return;

        Mode = mode;
        SettingsService.Theme = mode;

        var wasDark = IsDarkTheme;
        IsDarkTheme = ResolveIsDark(mode);
        if (wasDark != IsDarkTheme)
            ThemeChanged?.Invoke(this, IsDarkTheme);
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
        // A fixed choice ignores the Windows setting.
        if (e.Category != UserPreferenceCategory.General || Mode != AppThemeMode.System) return;

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
