using System.IO;
using System.Windows;
using System.Windows.Input;
using MDViewer.Services;
using Microsoft.Web.WebView2.Core;

namespace MDViewer;

public partial class MainWindow : Window
{
    private readonly MarkdownRenderService _renderService;
    private readonly ThemeService _themeService;
    private FileSystemWatcher? _fileWatcher;
    private string? _currentFilePath;
    private string? _assetsFolder;
    private bool _webViewReady;
    private double _zoomFactor = 1.0;
    private System.Timers.Timer? _debounceTimer;

    private const double MinZoom = 0.25;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.1;

    public MainWindow()
    {
        InitializeComponent();
        _renderService = new MarkdownRenderService();
        _themeService = new ThemeService();
        _themeService.ThemeChanged += OnThemeChanged;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebView();

        // Check for file association registration on first run
        if (!FileAssociationService.HasPromptedBefore())
        {
            PromptFileAssociation();
        }

        // Open file from command line args if provided
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            await OpenMarkdownFile(args[1]);
        }
        else
        {
            ShowOpenFileDialog();
        }
    }

    private async Task InitializeWebView()
    {
        try
        {
            StatusLabel.Text = "Initializing viewer...";

            // Prepare assets folder with mermaid.js
            _assetsFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MDViewer_Assets");
            Directory.CreateDirectory(_assetsFolder);

            var mermaidPath = System.IO.Path.Combine(_assetsFolder, "mermaid.min.js");
            if (!File.Exists(mermaidPath))
            {
                await ExtractEmbeddedResource("MDViewer.Assets.mermaid.min.js", mermaidPath);
            }

            await WebView.EnsureCoreWebView2Async();

            // Map virtual host to assets folder so template can reference mermaid.js
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "assets.local", _assetsFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            _webViewReady = true;
            EnableControls(true);
            StatusLabel.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "WebView2 initialization failed";
            MessageBox.Show(
                $"Failed to initialize WebView2. Please ensure the WebView2 Runtime is installed.\n\n{ex.Message}",
                "MDViewer - Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task ExtractEmbeddedResource(string resourceName, string outputPath)
    {
        var assembly = typeof(MainWindow).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");
        await using var fileStream = File.Create(outputPath);
        await stream.CopyToAsync(fileStream);
    }

    private void EnableControls(bool enabled)
    {
        PrintButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        ZoomOutButton.IsEnabled = enabled;
        ZoomResetButton.IsEnabled = enabled;
    }

    public async Task OpenMarkdownFile(string filePath)
    {
        if (!_webViewReady) return;

        try
        {
            _currentFilePath = filePath;
            var fileName = System.IO.Path.GetFileName(filePath);
            Title = $"{fileName} — MDViewer";
            FileNameLabel.Text = filePath;
            StatusLabel.Text = $"Loading {fileName}...";

            var markdown = await File.ReadAllTextAsync(filePath);
            var html = _renderService.RenderToHtml(markdown, _themeService.IsDarkTheme);

            WebView.NavigateToString(html);
            StatusLabel.Text = $"Loaded — {fileName}";

            SetupFileWatcher(filePath);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error loading file";
            MessageBox.Show($"Failed to load file:\n{ex.Message}",
                "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetupFileWatcher(string filePath)
    {
        _fileWatcher?.Dispose();
        _debounceTimer?.Dispose();

        var directory = System.IO.Path.GetDirectoryName(filePath);
        var fileName = System.IO.Path.GetFileName(filePath);

        if (directory == null) return;

        _debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        _debounceTimer.Elapsed += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (_currentFilePath != null)
                {
                    StatusLabel.Text = "Reloading...";
                    await OpenMarkdownFile(_currentFilePath);
                }
            });
        };

        _fileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _fileWatcher.Changed += (_, _) =>
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        };
    }

    #region File Operations

    private void ShowOpenFileDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Markdown files (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*",
            Title = "Open Markdown File",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            _ = OpenMarkdownFile(dialog.FileName);
        }
        else if (_currentFilePath == null)
        {
            StatusLabel.Text = "No file selected";
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        ShowOpenFileDialog();
    }

    #endregion

    #region Print

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;

        try
        {
            StatusLabel.Text = "Printing...";
            WebView.CoreWebView2.ShowPrintUI();
            StatusLabel.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Print failed";
            MessageBox.Show($"Print failed:\n{ex.Message}",
                "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Zoom

    private void UpdateZoom()
    {
        if (!_webViewReady) return;
        _zoomFactor = Math.Clamp(_zoomFactor, MinZoom, MaxZoom);
        WebView.ZoomFactor = _zoomFactor;
        ZoomLabel.Text = $"{(int)(_zoomFactor * 100)}%";
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _zoomFactor += ZoomStep;
        UpdateZoom();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _zoomFactor -= ZoomStep;
        UpdateZoom();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _zoomFactor = 1.0;
        UpdateZoom();
    }

    #endregion

    #region Theme

    private async void OnThemeChanged(object? sender, bool isDark)
    {
        if (!_webViewReady) return;

        // Re-render if we have a file loaded, or just toggle CSS
        if (_currentFilePath != null)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await OpenMarkdownFile(_currentFilePath);
            });
        }
    }

    #endregion

    #region Keyboard Shortcuts

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.O:
                ShowOpenFileDialog();
                e.Handled = true;
                break;
            case Key.P:
                Print_Click(sender, e);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                _zoomFactor += ZoomStep;
                UpdateZoom();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                _zoomFactor -= ZoomStep;
                UpdateZoom();
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                _zoomFactor = 1.0;
                UpdateZoom();
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region File Association

    private void PromptFileAssociation()
    {
        FileAssociationService.SetPrompted();

        if (FileAssociationService.IsRegistered()) return;

        var result = MessageBox.Show(
            "Would you like to set MDViewer as the default application for .md files?\n\n" +
            "This will allow you to open Markdown files by double-clicking them.",
            "MDViewer — File Association",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                FileAssociationService.Register();
                StatusLabel.Text = "Registered as .md file handler";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to register file association:\n{ex.Message}",
                    "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    #endregion

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _fileWatcher?.Dispose();
        _debounceTimer?.Dispose();
        _themeService.Dispose();
    }
}
