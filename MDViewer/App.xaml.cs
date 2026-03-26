using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MDViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

            Shutdown(1);
        }
    }
}

