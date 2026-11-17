using MDViewer.Models;
using Microsoft.Win32;

namespace MDViewer.Services;

/// <summary>
/// Persists user preferences under HKCU\Software\MDViewer.
/// </summary>
public static class SettingsService
{
    private const string KeyPath = @"Software\MDViewer";
    private const double DefaultTreeWidth = 260;

    public static string? LastFolder
    {
        get => GetValue("LastFolder") as string;
        set => SetValue("LastFolder", value, RegistryValueKind.String);
    }

    public static string? LastFile
    {
        get => GetValue("LastFile") as string;
        set => SetValue("LastFile", value, RegistryValueKind.String);
    }

    public static bool TreeOnLeft
    {
        get => GetValue("TreeOnLeft") is int i && i == 1;
        set => SetValue("TreeOnLeft", value ? 1 : 0, RegistryValueKind.DWord);
    }

    public static double TreeWidth
    {
        get => GetValue("TreeWidth") is int i && i >= 100 ? i : DefaultTreeWidth;
        set => SetValue("TreeWidth", (int)Math.Round(value), RegistryValueKind.DWord);
    }

    /// <summary>
    /// Interface theme chosen in the app (REG_SZ "Light", "Dark" or "System").
    /// Missing or invalid values mean <see cref="AppThemeMode.System"/>.
    /// </summary>
    public static AppThemeMode Theme
    {
        get => GetValue("Theme") is string s
               && Enum.TryParse<AppThemeMode>(s, ignoreCase: true, out var mode)
               && Enum.IsDefined(mode)
               && !int.TryParse(s, out _)
            ? mode
            : AppThemeMode.System;
        set => SetValue("Theme", value.ToString(), RegistryValueKind.String);
    }

    // Values written by older versions that nothing reads any more.
    private static readonly string[] ObsoleteValues = { "PageSize", "FileAssocRegistered" };

    /// <summary>
    /// Deletes preferences left by older versions (the page size, removed with
    /// printing and export; the "already asked" flag of the removed file
    /// association). Best-effort: a failure is ignored and the next start tries again.
    /// </summary>
    public static void RemoveObsoleteValues()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key == null) return;
            foreach (var name in ObsoleteValues)
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // Preferences are best-effort; ignore registry failures.
        }
    }

    private static object? GetValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name);
        }
        catch
        {
            return null;
        }
    }

    private static void SetValue(string name, object? value, RegistryValueKind kind)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value is null)
                key.DeleteValue(name, throwOnMissingValue: false);
            else
                key.SetValue(name, value, kind);
        }
        catch
        {
            // Preferences are best-effort; ignore registry failures.
        }
    }
}
