using Microsoft.Win32;

namespace MDViewer.Services;

public static class FileAssociationService
{
    private const string ProgId = "MDViewer.md";
    private const string FileExtension = ".md";
    private const string RegistrationKey = "FileAssocRegistered";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{FileExtension}");
            var value = key?.GetValue(null)?.ToString();
            return value == ProgId;
        }
        catch
        {
            return false;
        }
    }

    public static void Register()
    {
        var exePath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? "";

        using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FileExtension}"))
        {
            extKey.SetValue(null, ProgId);
        }

        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progKey.SetValue(null, "Markdown Document");

            using var iconKey = progKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, $"\"{exePath}\",0");

            using var commandKey = progKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(null, $"\"{exePath}\" \"%1\"");
        }

        using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.markdown"))
        {
            extKey.SetValue(null, ProgId);
        }

        NativeMethods.SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{FileExtension}", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.markdown", false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            NativeMethods.SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Silently ignore cleanup errors
        }
    }

    public static bool HasPromptedBefore()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\MDViewer");
            return key?.GetValue(RegistrationKey) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetPrompted()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\MDViewer");
        key.SetValue(RegistrationKey, 1, RegistryValueKind.DWord);
    }
}

internal static partial class NativeMethods
{
    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
