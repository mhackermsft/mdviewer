using Microsoft.Win32;

namespace MDViewer.Services;

/// <summary>
/// MDViewer no longer registers itself as the Markdown file handler. Older
/// versions could, so on every start this removes what they left in
/// HKCU\Software\Classes. Only MDViewer's own data goes: an extension is released
/// only while it still points to MDViewer, values and subkeys written by other
/// apps (e.g. OpenWithProgids) stay, and the extension key is deleted only when
/// nothing else is left in it.
/// </summary>
public static class LegacyFileAssociation
{
    private const string ProgId = "MDViewer.md";
    private static readonly string[] Extensions = { ".md", ".markdown" };

    /// <summary>Best-effort and idempotent: failures are ignored, the next start tries again.</summary>
    public static void Remove()
    {
        var removed = false;

        foreach (var extension in Extensions)
        {
            try { removed |= ReleaseExtension(extension); }
            catch { /* best-effort */ }
        }

        try
        {
            var progIdPath = $@"Software\Classes\{ProgId}";
            bool exists;
            using (var progKey = Registry.CurrentUser.OpenSubKey(progIdPath))
                exists = progKey != null;

            if (exists)
            {
                Registry.CurrentUser.DeleteSubKeyTree(progIdPath, throwOnMissingSubKey: false);
                removed = true;
            }
        }
        catch { /* best-effort */ }

        // Tell Explorer only when something actually changed (SHCNE_ASSOCCHANGED).
        if (removed)
        {
            try { NativeMethods.SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
            catch { /* best-effort */ }
        }
    }

    /// <returns>True when the extension pointed to MDViewer and was released.</returns>
    private static bool ReleaseExtension(string extension)
    {
        var path = $@"Software\Classes\{extension}";
        using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true))
        {
            if (key == null || key.GetValue(null)?.ToString() != ProgId) return false;

            key.DeleteValue("", throwOnMissingValue: false);
            if (key.ValueCount > 0 || key.SubKeyCount > 0) return true;
        }

        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        return true;
    }
}
