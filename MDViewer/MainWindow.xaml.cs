using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MDViewer.Models;
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
    private double? _pendingScrollRatio;
    private System.Timers.Timer? _debounceTimer;
    private readonly Stack<string> _navigationHistory = new();

    // Folder mode state
    private string? _rootFolder;
    private FolderTree? _folderTree;
    private FileTreeNode? _selectedNode;
    private bool _treeOnLeft;
    private string? _placeholderMarkdown;

    private const double TreeMinWidth = 150;
    private const double ViewerMinWidth = 300;

    private const string WelcomeMarkdown =
        "# MDViewer\n\nNo folder open.\n\n" +
        "Click **📁 Open Folder** (Ctrl+O) to choose a folder containing Markdown files.";

    private const double MinZoom = 0.25;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.1;

    // Virtual host used to resolve relative resource paths (e.g. images)
    // referenced by the currently open document.
    private const string DocumentVirtualHost = "mddoc.local";

    public MainWindow()
    {
        InitializeComponent();
        _renderService = new MarkdownRenderService();
        _themeService = new ThemeService();
        _themeService.ThemeChanged += OnThemeChanged;

        ApplyTreeLayout(SettingsService.TreeOnLeft, SettingsService.TreeWidth);

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

        var (folder, file) = ResolveStartupTarget();
        if (folder != null)
            await OpenFolder(folder, file);
        else
            ShowPlaceholder(WelcomeMarkdown);
    }

    /// <summary>
    /// Decides which folder/file to open at startup: a folder or file passed on
    /// the command line (e.g. via file association) wins; otherwise the last
    /// opened folder and file are restored.
    /// </summary>
    private static (string? Folder, string? File) ResolveStartupTarget()
    {
        var lastFolder = SettingsService.LastFolder;
        if (lastFolder != null && !Directory.Exists(lastFolder))
            lastFolder = null;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            try
            {
                var arg = Path.GetFullPath(args[1]);
                if (Directory.Exists(arg))
                    return (arg, null);

                if (File.Exists(arg))
                {
                    // Keep the last folder if the file lives inside it,
                    // otherwise use the file's own folder.
                    var folder = lastFolder != null && IsUnderFolder(arg, lastFolder)
                        ? lastFolder
                        : Path.GetDirectoryName(arg);
                    return (folder, arg);
                }
            }
            catch { /* invalid path — fall through */ }
        }

        return (lastFolder, lastFolder != null ? SettingsService.LastFile : null);
    }

    private static bool IsUnderFolder(string filePath, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
                    if (_pendingScrollRatio is double ratio)
                    {
                        _pendingScrollRatio = null;
                        var ratioStr = ratio.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await WebView.ExecuteScriptAsync(
                            "(function(){var h=document.documentElement.scrollHeight-window.innerHeight;" +
                            "if(h>0){window.scrollTo(0,h*" + ratioStr + ");}})()");
                    }
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
            // If reloading the same file, capture the current scroll position
            // so we can restore it after re-render.
            if (_currentFilePath != null
                && string.Equals(_currentFilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && _renderComplete)
            {
                try
                {
                    var result = await WebView.ExecuteScriptAsync(
                        "(function(){var h=document.documentElement.scrollHeight-window.innerHeight;" +
                        "return h>0?(window.scrollY/h):0;})()");
                    if (double.TryParse(result, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var ratio))
                    {
                        _pendingScrollRatio = Math.Clamp(ratio, 0.0, 1.0);
                    }
                }
                catch { /* ignore — fall back to top of document */ }
            }
            else
            {
                _pendingScrollRatio = null;
            }

            _currentFilePath = filePath;
            _placeholderMarkdown = null;
            SyncTreeSelection(filePath);
            if (_rootFolder != null && IsUnderFolder(filePath, _rootFolder))
                SettingsService.LastFile = filePath;

            var fileName = System.IO.Path.GetFileName(filePath);
            Title = $"{fileName} — MDViewer";
            FileNameLabel.Text = filePath;
            StatusLabel.Text = $"Loading {fileName}...";

            var markdown = await File.ReadAllTextAsync(filePath);
            var baseHref = MapDocumentFolder(filePath);
            var html = _renderService.RenderToHtml(markdown, _themeService.IsDarkTheme, baseHref);

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

    /// <summary>
    /// Maps the folder containing <paramref name="filePath"/> to a WebView2 virtual
    /// host so that relative resource paths (such as images) resolve correctly when
    /// the rendered HTML is loaded via NavigateToString (which has no file base URL).
    /// Returns the base href to embed in the document, or null if unavailable.
    /// </summary>
    private string? MapDocumentFolder(string filePath)
    {
        var directory = System.IO.Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return null;

        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            DocumentVirtualHost, directory,
            CoreWebView2HostResourceAccessKind.Allow);

        return $"https://{DocumentVirtualHost}/";
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
            PushHistory();
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

    private void PushHistory()
    {
        if (_currentFilePath == null) return;
        _navigationHistory.Push(_currentFilePath);
        BackButton.IsEnabled = true;
    }

    #endregion

    #region Folder

    private async Task ShowOpenFolderDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open Folder" };
        if (_rootFolder != null)
            dialog.InitialDirectory = _rootFolder;

        if (dialog.ShowDialog(this) == true)
            await OpenFolder(dialog.FolderName);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        await ShowOpenFolderDialog();
    }

    /// <summary>
    /// Opens <paramref name="folder"/>: rebuilds the tree from scratch and shows
    /// <paramref name="preferredFile"/> if it exists, otherwise the first
    /// Markdown file of the tree.
    /// </summary>
    private async Task OpenFolder(string folder, string? preferredFile = null)
    {
        _rootFolder = Path.GetFullPath(folder);
        _folderTree = null;
        _selectedNode = null;
        SettingsService.LastFolder = _rootFolder;

        _navigationHistory.Clear();
        BackButton.IsEnabled = false;

        await LoadFolderTree();

        string? target = null;
        if (preferredFile != null && File.Exists(preferredFile))
            target = Path.GetFullPath(preferredFile);
        target ??= _folderTree?.FirstFile()?.FullPath;

        if (target != null)
        {
            await OpenMarkdownFile(target);
        }
        else
        {
            SettingsService.LastFile = null;
            ShowPlaceholder(
                $"# No Markdown files\n\nThe folder `{_rootFolder}` does not contain any Markdown (`.md`) files.");
        }
    }

    /// <summary>
    /// Scans the current root folder and (re)binds the tree, keeping folders
    /// that were expanded before expanded.
    /// </summary>
    private async Task LoadFolderTree()
    {
        var root = _rootFolder;
        if (root == null) return;

        var expanded = _folderTree?.AllNodes()
            .Where(n => n.IsDirectory && n.IsExpanded)
            .Select(n => n.FullPath)
            .ToList() ?? new List<string>();

        FolderNameLabel.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) is { Length: > 0 } name
            ? name
            : root;
        FolderNameLabel.ToolTip = root;
        StatusLabel.Text = "Scanning folder...";

        FolderTree tree;
        try
        {
            tree = await Task.Run(() => FolderTreeService.Build(root));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Failed to read folder";
            MessageBox.Show($"Failed to read folder:\n{ex.Message}",
                "MDViewer - Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Another folder was opened while scanning
        if (!string.Equals(root, _rootFolder, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var path in expanded)
        {
            if (tree.Find(path) is { } node)
                node.IsExpanded = true;
        }

        _folderTree = tree;
        _selectedNode = null;
        FolderTreeView.ItemsSource = tree.Nodes;
        StatusLabel.Text = "Ready";
    }

    private async Task RefreshFolderTree()
    {
        if (_rootFolder == null) return;

        if (!Directory.Exists(_rootFolder))
        {
            MessageBox.Show($"The folder no longer exists:\n{_rootFolder}",
                "MDViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await LoadFolderTree();
        SyncTreeSelection(_currentFilePath);
    }

    private async void RefreshTree_Click(object sender, RoutedEventArgs e)
    {
        await RefreshFolderTree();
    }

    /// <summary>
    /// Selects the tree node of <paramref name="filePath"/> (expanding its parent
    /// folders), or clears the selection when the file is not part of the tree.
    /// </summary>
    private void SyncTreeSelection(string? filePath)
    {
        var node = filePath != null ? _folderTree?.Find(filePath) : null;
        if (ReferenceEquals(node, _selectedNode)) return;

        if (_selectedNode != null)
            _selectedNode.IsSelected = false;

        _selectedNode = node;
        if (node == null) return;

        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            parent.IsExpanded = true;
        node.IsSelected = true;
    }

    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FileTreeNode node) return;
        _selectedNode = node;

        if (node.IsDirectory) return;
        if (string.Equals(node.FullPath, _currentFilePath, StringComparison.OrdinalIgnoreCase)) return;

        PushHistory();
        await OpenMarkdownFile(node.FullPath);
    }

    private void TreeViewItem_Selected(object sender, RoutedEventArgs e)
    {
        // Selected bubbles through parent items; only scroll the item itself.
        if (sender is TreeViewItem item && ReferenceEquals(item, e.OriginalSource))
            item.BringIntoView();
    }

    /// <summary>Shows a static message instead of a document.</summary>
    private void ShowPlaceholder(string markdown)
    {
        _placeholderMarkdown = markdown;
        _currentFilePath = null;
        _pendingScrollRatio = null;
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        SyncTreeSelection(null);

        Title = "MDViewer";
        FileNameLabel.Text = _rootFolder ?? "No folder open";
        StatusLabel.Text = "Ready";

        if (!_webViewReady) return;
        _renderComplete = false;
        WebView.NavigateToString(_renderService.RenderToHtml(markdown, _themeService.IsDarkTheme));
    }

    #endregion

    #region Tree Panel Layout

    private ColumnDefinition TreeColumn => _treeOnLeft ? LeftColumn : RightColumn;

    private void ApplyTreeLayout(bool treeOnLeft, double treeWidth)
    {
        _treeOnLeft = treeOnLeft;
        var treeColumn = treeOnLeft ? LeftColumn : RightColumn;
        var viewerColumn = treeOnLeft ? RightColumn : LeftColumn;

        treeColumn.Width = new GridLength(Math.Max(treeWidth, TreeMinWidth));
        treeColumn.MinWidth = TreeMinWidth;
        viewerColumn.Width = new GridLength(1, GridUnitType.Star);
        viewerColumn.MinWidth = ViewerMinWidth;

        Grid.SetColumn(TreePanel, treeOnLeft ? 0 : 2);
        Grid.SetColumn(WebView, treeOnLeft ? 2 : 0);

        MoveTreeButton.ToolTip = treeOnLeft ? "Move panel to the right" : "Move panel to the left";
    }

    private void MoveTree_Click(object sender, RoutedEventArgs e)
    {
        ApplyTreeLayout(!_treeOnLeft, TreeColumn.ActualWidth);
        SettingsService.TreeOnLeft = _treeOnLeft;
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
                var baseHref = MapDocumentFolder(_currentFilePath!);
                htmlForPdf = _renderService.RenderToHtml(markdown, isDarkTheme: false, baseHref);
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

        // Re-render the current file or placeholder with the new theme
        await Dispatcher.InvokeAsync(async () =>
        {
            if (_currentFilePath != null)
                await OpenMarkdownFile(_currentFilePath);
            else if (_placeholderMarkdown != null)
                ShowPlaceholder(_placeholderMarkdown);
        });
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

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F5)
        {
            _ = RefreshFolderTree();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.O:
                _ = ShowOpenFolderDialog();
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
        if (TreeColumn.ActualWidth > 0)
            SettingsService.TreeWidth = TreeColumn.ActualWidth;

        _fileWatcher?.Dispose();
        _debounceTimer?.Dispose();
        _themeService.Dispose();
    }
}
