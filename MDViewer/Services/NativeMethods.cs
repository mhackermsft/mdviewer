namespace MDViewer.Services;

internal static partial class NativeMethods
{
    // Tells Explorer that file associations changed (SHCNE_ASSOCCHANGED).
    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // Natural ("file2" < "file10") ordering, as used by Windows Explorer.
    [System.Runtime.InteropServices.LibraryImport("shlwapi.dll",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    internal static partial int StrCmpLogicalW(string psz1, string psz2);

    // Window attributes such as the dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE).
    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
