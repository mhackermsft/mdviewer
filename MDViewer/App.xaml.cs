using System.Diagnostics;
using System.Windows;
using MDViewer.Services;
using Microsoft.Web.WebView2.Core;

namespace MDViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Interface theme before any window appears; each window then gets a
        // title bar that matches it as soon as it loads.
        ThemeApplier.Apply(ThemeService.ResolveIsDark(SettingsService.Theme));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ThemeApplier.ApplyTitleBar((Window)sender)));

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var result = MessageBox.Show(
                "This app requires the Microsoft Edge WebView2 Runtime.\n\n" +
                "Click OK to open the download page.",
                "Missing Component",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.OK)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    UseShellExecute = true
                });
            }

            // Leave without ever creating the main window.
            Shutdown(1);
            return;
        }

        SettingsService.RemoveObsoleteValues();
        LegacyFileAssociation.Remove();

        // No StartupUri: the main window exists only when the runtime is present.
        new MainWindow().Show();
    }
}

