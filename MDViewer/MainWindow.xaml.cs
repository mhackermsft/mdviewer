using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private bool _renderComplete;
    private double _zoomFactor = 1.0;
    private System.Timers.Timer? _debounceTimer;
    private readonly Stack<string> _navigationHistory = new();

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

            // Handle messages from the WebView (Mermaid completion and link navigation)
            WebView.CoreWebView2.WebMessageReceived += async (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                if (message == "render-complete")
                {
                    _renderComplete = true;
                    return;
                }

                try
                {
                    using var json = JsonDocument.Parse(message);
                    if (json.RootElement.TryGetProperty("type", out var type)
                        && type.GetString() == "navigate"
                        && json.RootElement.TryGetProperty("href", out var hrefProp))
                    {
                        var href = hrefProp.GetString();
                        if (href != null)
                            await HandleLinkNavigation(href);
                    }
                }
                catch { /* Not JSON — ignore */ }
            };

            WebView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;

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
        ExportButton.IsEnabled = enabled;
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

            _renderComplete = false;
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

    #region Navigation

    private async Task HandleLinkNavigation(string href)
    {
        // External URLs and mailto — open in default browser/app
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(href) { UseShellExecute = true }); }
            catch { /* silently ignore */ }
            return;
        }

        if (_currentFilePath == null) return;

        var currentDir = Path.GetDirectoryName(_currentFilePath);
        if (currentDir == null) return;

        // Separate any fragment (e.g. "file.md#section")
        string? anchor = null;
        var hashIndex = href.IndexOf('#');
        if (hashIndex >= 0)
        {
            anchor = href[(hashIndex + 1)..];
            href = href[..hashIndex];
        }

        // If only an anchor was provided, there's no file to navigate to
        if (string.IsNullOrEmpty(href)) return;

        var targetPath = Path.GetFullPath(Path.Combine(currentDir, href));
        var ext = Path.GetExtension(targetPath).ToLowerInvariant();

        if ((ext is ".md" or ".markdown") && File.Exists(targetPath))
        {
            _navigationHistory.Push(_currentFilePath);
            BackButton.IsEnabled = true;
            await OpenMarkdownFile(targetPath);

            if (!string.IsNullOrEmpty(anchor))
            {
                var escaped = anchor.Replace("'", "\\'");
                await WebView.CoreWebView2.ExecuteScriptAsync(
                    $"document.getElementById('{escaped}')?.scrollIntoView({{behavior:'smooth'}})");
            }
        }
        else if (File.Exists(targetPath))
        {
            // Non-markdown files — open with default handler
            try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); }
            catch { /* silently ignore */ }
        }
    }

    private async void NavigateBack_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationHistory.Count == 0) return;

        var previousFile = _navigationHistory.Pop();
        BackButton.IsEnabled = _navigationHistory.Count > 0;
        await OpenMarkdownFile(previousFile);
    }

    #endregion

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
            if (_currentFilePath != null)
            {
                _navigationHistory.Push(_currentFilePath);
                BackButton.IsEnabled = true;
            }
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

    #region Export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady || _currentFilePath == null) return;

        // Scan for linked markdown documents
        var collector = new Services.LinkedDocumentCollector();
        var linkedDocs = collector.CollectLinkedDocuments(_currentFilePath);
        var linkedDocCount = linkedDocs.Count - 1; // exclude the root document

        // Show export options dialog
        var exportDialog = new ExportDialog(linkedDocCount) { Owner = this };
        if (exportDialog.ShowDialog() != true) return;

        var includeLinkedDocs = linkedDocCount > 0 && exportDialog.IncludeLinkedDocuments;

        if (exportDialog.IsPdfFormat)
            await ExportAsPdf(linkedDocs, includeLinkedDocs);
        else
            await ExportAsHtml(linkedDocs, includeLinkedDocs);
    }

    private async Task ExportAsPdf(IReadOnlyList<Services.LinkedDocument> linkedDocs, bool includeLinkedDocs)
    {
        var defaultName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath!) + ".pdf";
        var defaultDir = System.IO.Path.GetDirectoryName(_currentFilePath!) ?? "";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            Title = "Export as PDF",
            FileName = defaultName,
            InitialDirectory = defaultDir
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var docCount = includeLinkedDocs ? linkedDocs.Count : 1;
            StatusLabel.Text = includeLinkedDocs
                ? $"Exporting PDF ({docCount} documents)..."
                : "Exporting PDF...";
            ExportButton.IsEnabled = false;

            // Build HTML for PDF (always light mode, with page breaks)
            string htmlForPdf;
            if (includeLinkedDocs)
            {
                htmlForPdf = _renderService.RenderMergedToHtml(linkedDocs, isDarkTheme: false, addPageBreaks: true);
            }
            else
            {
                var markdown = await File.ReadAllTextAsync(_currentFilePath!);
                htmlForPdf = _renderService.RenderToHtml(markdown, isDarkTheme: false);
            }

            _renderComplete = false;
            WebView.NavigateToString(htmlForPdf);

            // Wait for Mermaid diagrams (scale timeout with document count)
            var maxWaitIterations = docCount * 100;
            for (var i = 0; i < maxWaitIterations && !_renderComplete; i++)
                await Task.Delay(100);

            var settings = WebView.CoreWebView2.Environment.CreatePrintSettings();
            settings.PageWidth = 8.5;
            settings.PageHeight = 11.0;
            settings.MarginTop = 0;
            settings.MarginBottom = 0;
            settings.MarginLeft = 0;
            settings.MarginRight = 0;
            settings.ShouldPrintBackgrounds = true;
            settings.ScaleFactor = 1.0;

            var success = await WebView.CoreWebView2.PrintToPdfAsync(dialog.FileName, settings);

            if (success)
                StatusLabel.Text = $"Exported — {System.IO.Path.GetFileName(dialog.FileName)}";
            else
            {
                StatusLabel.Text = "PDF export failed";
                MessageBox.Show("PDF export failed. The file may be in use by another application.",
                    "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "PDF export failed";
            MessageBox.Show($"PDF export failed:\n{ex.Message}",
                "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            if (_currentFilePath != null)
                await OpenMarkdownFile(_currentFilePath);
        }
    }

    private async Task ExportAsHtml(IReadOnlyList<Services.LinkedDocument> linkedDocs, bool includeLinkedDocs)
    {
        var defaultName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath!) + ".html";
        var defaultDir = System.IO.Path.GetDirectoryName(_currentFilePath!) ?? "";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "HTML files (*.html)|*.html",
            Title = "Export as HTML",
            FileName = defaultName,
            InitialDirectory = defaultDir
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var docCount = includeLinkedDocs ? linkedDocs.Count : 1;
            StatusLabel.Text = includeLinkedDocs
                ? $"Exporting HTML ({docCount} documents)..."
                : "Exporting HTML...";
            ExportButton.IsEnabled = false;

            // Build HTML matching the current system theme, no page breaks
            string html;
            if (includeLinkedDocs)
            {
                html = _renderService.RenderMergedToHtml(
                    linkedDocs, _themeService.IsDarkTheme, addPageBreaks: false);
            }
            else
            {
                var markdown = await File.ReadAllTextAsync(_currentFilePath!);
                html = _renderService.RenderToHtml(markdown, _themeService.IsDarkTheme);
            }

            // Convert to self-contained standalone HTML
            html = _renderService.ConvertToStandaloneHtml(html);

            await File.WriteAllTextAsync(dialog.FileName, html);
            StatusLabel.Text = $"Exported — {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "HTML export failed";
            MessageBox.Show($"HTML export failed:\n{ex.Message}",
                "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
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
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            Export_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left)
        {
            NavigateBack_Click(sender, e);
            e.Handled = true;
            return;
        }

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
