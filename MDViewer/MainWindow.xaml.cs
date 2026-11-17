using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MDViewer.Models;
using MDViewer.Services;
using MDViewer.ViewModels;
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
    private readonly ZoomLevel _zoom = new();
    private double? _pendingScrollRatio;
    private System.Timers.Timer? _debounceTimer;
    private readonly NavigationHistory _history = new();

    // Navigations the app itself started (NavigateHost) and that have not yet
    // reached NavigationStarting. Any other navigation is cancelled.
    private int _hostNavigationsPending;

    // Folder mode state
    private string? _rootFolder;
    private FolderTree? _folderTree;
    private FileTreeNode? _selectedNode;
    private bool _treeOnLeft;
    private string? _placeholderMarkdown;

    // "Open files" section: Markdown files opened outside the folder tree
    // (menu, drag and drop, links). Session only, never persisted.
    private readonly FileTreeNode _openFilesSection =
        new("Open files", string.Empty, isDirectory: true, parent: null) { IsSection = true, IsExpanded = true };
    private readonly ObservableCollection<FileTreeNode> _treeItems = new();

    private const double TreeMinWidth = 150;
    private const double ViewerMinWidth = 300;

    private const string WelcomeMarkdown =
        "# MDViewer\n\nNo folder open.\n\n" +
        "Click **📁 Open Folder** (Ctrl+O) to choose a folder containing Markdown files.";

    private const string NoFileSelectedMarkdown =
        "# No file selected\n\n" +
        "The document that was open has been deleted, renamed or moved.\n\n" +
        "Choose a file in the tree.";

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
        FolderTreeView.ItemsSource = _treeItems;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebView();

        var (folder, file) = ResolveStartupTarget();
        if (folder != null)
            await OpenFolder(folder, file);
        else
            ShowPlaceholder(WelcomeMarkdown);
    }

    /// <summary>
    /// Decides which folder/file to open at startup: a folder or file passed on
    /// the command line (e.g. Explorer's "Open with") wins; otherwise the last
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

            // Always refresh the copy, so an old or damaged mermaid.min.js left in
            // %TEMP% by another version never wins over the embedded one.
            var mermaidPath = System.IO.Path.Combine(_assetsFolder, "mermaid.min.js");
            var mermaidStale = !await TryRefreshMermaid(mermaidPath);

            await WebView.EnsureCoreWebView2Async();

            // The viewer only reads: no browser context menu (Print, Save as...)
            // and no browser-only shortcuts (Ctrl+P, Ctrl+S, Ctrl+F, F12...).
            // Text editing keys such as Ctrl+C and Ctrl+A keep working, and the
            // app's own shortcuts still reach Window_KeyDown.
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            ApplyWebViewColorScheme(_themeService.IsDarkTheme);

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

                // Only the parsing is guarded: a failure while navigating must
                // surface, not be mistaken for a malformed message.
                string? type = null, href = null, dir = null;
                try
                {
                    using var json = JsonDocument.Parse(message);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) return;
                    type = ReadString(json.RootElement, "type");
                    href = ReadString(json.RootElement, "href");
                    dir = ReadString(json.RootElement, "dir");
                }
                catch (JsonException) { return; /* Not JSON — ignore */ }

                if (type == "navigate" && href != null)
                    await HandleLinkNavigation(href);
                else if (type == "flow" && dir != null)
                    await HandleFlowMessage(dir);
            };

            WebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (TryGetDroppedPath(args.Uri) is { } droppedPath)
                    _ = Dispatcher.InvokeAsync(() => OpenDroppedPath(droppedPath));
            };

            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                // Files dropped on the document are handled by the browser, not
                // by the window: they surface as a navigation to a file:// URL,
                // which the viewer never starts itself. Cancel it and open the
                // dropped item like a drop on the window (unsupported types: nothing).
                if (TryGetDroppedPath(args.Uri) is { } droppedPath)
                {
                    args.Cancel = true;
                    _ = Dispatcher.InvokeAsync(() => OpenDroppedPath(droppedPath));
                    return;
                }

                // Only the app's own NavigateToString calls may change the page;
                // navigations started by the content (location.href, forms,
                // <meta refresh>, reload/back) are cancelled.
                if (_hostNavigationsPending > 0 && IsHostDocumentUri(args.Uri))
                {
                    _hostNavigationsPending--;
                    return;
                }

                args.Cancel = true;
                StatusLabel.Text = "Navigation blocked";
            };

            EnableControls(true);
            UpdateZoom(); // a level chosen by keyboard before the viewer was ready
            StatusLabel.Text = mermaidStale
                ? "Ready — could not update mermaid.min.js, using the existing copy"
                : "Ready";
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

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Shows HTML generated by the app. The only way the viewer changes the
    /// page: NavigationStarting lets through just the navigations started here.
    /// </summary>
    private void NavigateHost(string html)
    {
        _hostNavigationsPending++;
        WebView.NavigateToString(html);
    }

    /// <summary>URI that NavigateToString produces (a data: URL or about:blank).</summary>
    private static bool IsHostDocumentUri(string? uri) =>
        uri != null
        && (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the embedded Mermaid over the copy in the assets folder (via a temp
    /// file, so a failure never leaves it half written). Returns false when the
    /// copy could not be replaced (e.g. in use by another instance) but an older
    /// one exists; throws when there is no copy at all.
    /// </summary>
    private static async Task<bool> TryRefreshMermaid(string mermaidPath)
    {
        var tempPath = $"{mermaidPath}.{Environment.ProcessId}.tmp";
        try
        {
            await ExtractEmbeddedResource("MDViewer.Assets.mermaid.min.js", tempPath);
            File.Move(tempPath, mermaidPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(mermaidPath))
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            return false;
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
                _pendingScrollRatio = await GetScrollRatioAsync();
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
            (string? PrevLabel, string? NextLabel)? footer = IsFlowFooterActive ? BuildFlowFooter(filePath) : null;
            var html = _renderService.RenderToHtml(markdown, _themeService.IsDarkTheme, baseHref, footer);

            _renderComplete = false;
            NavigateHost(html);
            UpdateFlowNavigation();
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
    /// Relative reading position of the page (0 = top, 1 = bottom), or null
    /// when it cannot be read.
    /// </summary>
    private async Task<double?> GetScrollRatioAsync()
    {
        try
        {
            var result = await WebView.ExecuteScriptAsync(
                "(function(){var h=document.documentElement.scrollHeight-window.innerHeight;" +
                "return h>0?(window.scrollY/h):0;})()");
            if (double.TryParse(result, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ratio))
                return Math.Clamp(ratio, 0.0, 1.0);
        }
        catch { /* ignore — fall back to top of document */ }
        return null;
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

        var debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        debounceTimer.Elapsed += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                // Ignore a tick from a timer that was replaced or disposed meanwhile.
                if (!ReferenceEquals(debounceTimer, _debounceTimer) || _currentFilePath == null)
                    return;

                // Decide by existence at the end of the debounce window: an atomic
                // save (temp file + rename, or delete + create) leaves the file in
                // place and reloads; a delete, rename or move empties the page.
                if (File.Exists(_currentFilePath))
                {
                    StatusLabel.Text = "Reloading...";
                    await OpenMarkdownFile(_currentFilePath);
                }
                else
                {
                    ShowEmptyDocument();
                }
            });
        };
        _debounceTimer = debounceTimer;

        _fileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        void Restart(object sender, FileSystemEventArgs e)
        {
            try
            {
                debounceTimer.Stop();
                debounceTimer.Start();
            }
            catch (ObjectDisposedException) { /* watcher being replaced */ }
        }

        _fileWatcher.Changed += Restart;
        _fileWatcher.Created += Restart;
        _fileWatcher.Deleted += Restart;
        _fileWatcher.Renamed += (s, e) => Restart(s, e);
        _fileWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// The open document no longer exists: back to the "no file selected" state,
    /// keeping the root folder and the tree.
    /// </summary>
    private void ShowEmptyDocument()
    {
        // A deleted file from the "Open files" section leaves the section too.
        if (_currentFilePath != null)
            RemoveOpenFile(_currentFilePath);

        ShowPlaceholder(NoFileSelectedMarkdown);
        StatusLabel.Text = "File no longer exists";
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
            // Targets outside the tree join the "Open files" section.
            await OpenStandaloneFile(targetPath);

            if (!string.IsNullOrEmpty(anchor))
            {
                var escaped = anchor.Replace("'", "\\'");
                await WebView.CoreWebView2.ExecuteScriptAsync(
                    $"document.getElementById('{escaped}')?.scrollIntoView({{behavior:'smooth'}})");
            }
        }
        else if (File.Exists(targetPath))
        {
            // Non-markdown files — open with the default handler, but only after
            // an explicit "Yes": the request may come from a script in the document.
            if (!ConfirmOpenExternalFile(targetPath)) return;
            try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); }
            catch { /* silently ignore */ }
        }
    }

    /// <summary>
    /// Asks before opening a local non-Markdown file with the Windows default
    /// app. Always asks (no "remember"), one confirmation per request; the
    /// default button is No.
    /// </summary>
    private bool ConfirmOpenExternalFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var result = MessageBox.Show(this,
            "The document wants to open a file with its default Windows application:\n\n" +
            $"{fileName}\n\n{path}\n\n" +
            "Only open files you trust. Open this file?",
            "MDViewer — Open file",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result == MessageBoxResult.Yes) return true;
        StatusLabel.Text = $"Opening cancelled — {fileName}";
        return false;
    }

    private async void NavigateBack_Click(object sender, RoutedEventArgs e)
    {
        if (_history.GoBack() is not { } previousFile) return;

        BackButton.IsEnabled = _history.CanGoBack;
        await OpenMarkdownFile(previousFile);
    }

    private void PushHistory()
    {
        _history.Push(_currentFilePath);
        BackButton.IsEnabled = _history.CanGoBack;
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

    private async Task ShowOpenFileDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open File",
            Filter = "Markdown files (*.md;*.markdown)|*.md;*.markdown",
            CheckFileExists = true
        };
        var initialDirectory = _currentFilePath != null ? Path.GetDirectoryName(_currentFilePath) : _rootFolder;
        if (initialDirectory != null && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog(this) == true)
            await OpenUserFile(dialog.FileName);
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        await ShowOpenFileDialog();
    }

    /// <summary>
    /// Opens a Markdown file picked by the user (menu or drop): a navigation,
    /// so it enters the history, unless it is the document already shown.
    /// </summary>
    private async Task OpenUserFile(string filePath)
    {
        if (!_webViewReady) return;

        if (!string.Equals(Path.GetFullPath(filePath), _currentFilePath, StringComparison.OrdinalIgnoreCase))
            PushHistory();
        await OpenStandaloneFile(filePath);
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
        _reversa = null;
        SetViewMode(TreeViewMode.Files);
        SyncTreeSelection(null);
        SettingsService.LastFolder = _rootFolder;

        _history.Clear();
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

        // Expansion state of each view, by FullPath (synthetic "reversa:" keys
        // included), so a refresh keeps the tree as the user left it.
        var filesExpansion = CaptureExpansion(_folderTree);
        var skillsExpansion = CaptureExpansion(_reversa?.Skills);
        var flowExpansion = CaptureExpansion(_reversa?.Flow);

        FolderNameLabel.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) is { Length: > 0 } name
            ? name
            : root;
        FolderNameLabel.ToolTip = root;
        StatusLabel.Text = "Scanning folder...";

        FolderTree tree;
        ReversaProject? reversa;
        try
        {
            (tree, reversa) = await Task.Run(() => (FolderTreeService.Build(root), DetectReversa(root)));
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

        RestoreExpansion(tree, filesExpansion);
        RestoreExpansion(reversa?.Skills, skillsExpansion);
        RestoreExpansion(reversa?.Flow, flowExpansion);

        // Files that are now part of the tree leave the "Open files" section.
        foreach (var openFile in _openFilesSection.Children.ToList())
        {
            if (tree.Find(openFile.FullPath) != null)
                _openFilesSection.Children.Remove(openFile);
        }

        // The section's nodes survive the rebind: drop the old selection flag,
        // or the regenerated item would be re-selected (and reopened) later.
        if (_selectedNode != null)
            _selectedNode.IsSelected = false;

        _folderTree = tree;
        _selectedNode = null;
        ApplyReversaProject(reversa);
        RebindTree();
        UpdateFlowNavigation();
        StatusLabel.Text = "Ready";
    }

    /// <summary>
    /// Composes the tree's items: the "Open files" section first (only when it
    /// has files), then the nodes of the root folder (if a folder is open).
    /// </summary>
    private void RebindTree()
    {
        _treeItems.Clear();
        if (_openFilesSection.Children.Count > 0)
            _treeItems.Add(_openFilesSection);
        if (CurrentViewTree is { } tree)
        {
            foreach (var node in tree.Nodes)
                _treeItems.Add(node);
        }
    }

    /// <summary>
    /// Single entry point to show a Markdown file chosen by the user (menu,
    /// drag and drop, link). A file of the folder tree is just shown (and
    /// selected in the tree); any other file is added to the "Open files"
    /// section (no duplicates, in opening order) and shown, keeping the root
    /// folder.
    /// </summary>
    private async Task OpenStandaloneFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (_folderTree?.Find(fullPath) == null)
            AddOpenFile(fullPath);
        await OpenMarkdownFile(fullPath);
    }

    private FileTreeNode? FindOpenFile(string filePath)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(filePath); }
        catch { return null; }

        return _openFilesSection.Children.FirstOrDefault(n =>
            string.Equals(n.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private void AddOpenFile(string fullPath)
    {
        if (FindOpenFile(fullPath) != null) return;

        _openFilesSection.Children.Add(
            new FileTreeNode(Path.GetFileName(fullPath), fullPath, isDirectory: false, _openFilesSection));
        _openFilesSection.IsExpanded = true;
        if (_openFilesSection.Children.Count == 1)
            RebindTree();
    }

    /// <summary>Removes a file from the "Open files" section (hiding it when empty).</summary>
    private void RemoveOpenFile(string filePath)
    {
        if (FindOpenFile(filePath) is not { } node) return;

        if (ReferenceEquals(node, _selectedNode))
            _selectedNode = null;
        node.IsSelected = false;
        _openFilesSection.Children.Remove(node);
        if (_openFilesSection.Children.Count == 0)
            RebindTree();
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
    /// folders), looking in the folder tree and then in the "Open files"
    /// section, or clears the selection when the file is in neither.
    /// </summary>
    private void SyncTreeSelection(string? filePath)
    {
        var node = filePath != null ? CurrentViewTree?.Find(filePath) ?? FindOpenFile(filePath) : null;
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

        if (node.IsDirectory || node.IsSection) return;
        if (string.Equals(node.FullPath, _currentFilePath, StringComparison.OrdinalIgnoreCase)) return;

        // A file of the "Open files" section that no longer exists is removed.
        if (ReferenceEquals(node.Parent, _openFilesSection) && !File.Exists(node.FullPath))
        {
            MessageBox.Show($"The file no longer exists and was removed from \"Open files\":\n{node.FullPath}",
                "MDViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Leave the selection-change handler before touching the items.
            await Dispatcher.Yield(DispatcherPriority.Background);
            RemoveOpenFile(node.FullPath);
            SyncTreeSelection(_currentFilePath);
            return;
        }

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
        UpdateFlowNavigation();

        if (!_webViewReady) return;
        _renderComplete = false;
        NavigateHost(_renderService.RenderToHtml(markdown, _themeService.IsDarkTheme));
    }

    #endregion

    #region Reversa

    // Reversa views (feature 010). Read-only: nothing here writes to the folder.
    private ReversaProject? _reversa;
    private TreeViewMode _viewMode = TreeViewMode.Files;

    /// <summary>The tree shown by the active view; Files outside Reversa folders.</summary>
    private FolderTree? CurrentViewTree => _viewMode switch
    {
        TreeViewMode.Skills when _reversa != null => _reversa.Skills,
        TreeViewMode.Flow when _reversa != null => _reversa.Flow,
        _ => _folderTree,
    };

    /// <summary>Detection never fails the folder scan: a broken Reversa install is a plain folder.</summary>
    private static ReversaProject? DetectReversa(string root)
    {
        try { return ReversaProjectService.Detect(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, bool> CaptureExpansion(FolderTree? tree)
    {
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (tree == null) return state;
        foreach (var node in tree.AllNodes())
        {
            if (node.IsDirectory)
                state.TryAdd(node.FullPath, node.IsExpanded);
        }
        return state;
    }

    private static void RestoreExpansion(FolderTree? tree, Dictionary<string, bool> state)
    {
        if (tree == null || state.Count == 0) return;
        foreach (var node in tree.AllNodes())
        {
            if (node.IsDirectory && state.TryGetValue(node.FullPath, out var expanded))
                node.IsExpanded = expanded;
        }
    }

    /// <summary>Shows or hides the badge and the view selector; a non-Reversa folder falls back to Files.</summary>
    private void ApplyReversaProject(ReversaProject? project)
    {
        _reversa = project;

        if (project == null)
        {
            ReversaBadge.Visibility = Visibility.Collapsed;
            ViewSelector.Visibility = Visibility.Collapsed;
            SetViewMode(TreeViewMode.Files);
            return;
        }

        var label = project.Version is { Length: > 0 } version ? $"Reversa {version}" : "Reversa";
        ReversaBadgeLabel.Text = label;
        var statePath = Path.Combine(project.RootPath, ".reversa", "state.json");
        ReversaBadge.ToolTip = $"{label}\n{statePath}";
        System.Windows.Automation.AutomationProperties.SetName(ReversaBadgeLabel, $"{label} folder");
        System.Windows.Automation.AutomationProperties.SetHelpText(ReversaBadgeLabel, statePath);
        ViewSkillsButton.ToolTip = project.SkillCount == 1 ? "1 skill" : $"{project.SkillCount} skills";

        ReversaBadge.Visibility = Visibility.Visible;
        ViewSelector.Visibility = Visibility.Visible;
    }

    /// <summary>Sets the view without side effects (used on folder open and fallback).</summary>
    private void SetViewMode(TreeViewMode mode)
    {
        _viewMode = mode;
        var button = mode switch
        {
            TreeViewMode.Skills => ViewSkillsButton,
            TreeViewMode.Flow => ViewFlowButton,
            _ => ViewFilesButton,
        };
        // Checked re-enters ViewToggle_Checked, which is a no-op for the current mode.
        button.IsChecked = true;
    }

    private async void ViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<TreeViewMode>(tag, out var mode))
            return;
        await SwitchView(mode);
    }

    /// <summary>
    /// Shows another view of the same folder: same document, selection
    /// re-synced, and a re-render only when the Flow footer appears or goes.
    /// </summary>
    private async Task SwitchView(TreeViewMode mode)
    {
        if (mode == _viewMode) return;
        if (mode != TreeViewMode.Files && _reversa == null) return;

        var hadFooter = IsFlowFooterActive;

        if (_selectedNode != null)
            _selectedNode.IsSelected = false;
        _selectedNode = null;

        _viewMode = mode;
        RebindTree();
        SyncTreeSelection(_currentFilePath);
        UpdateFlowNavigation();

        if (hadFooter != IsFlowFooterActive && _currentFilePath != null)
            await OpenMarkdownFile(_currentFilePath);
    }

    /// <summary>Position of <paramref name="path"/> in the Flow; -1 when it is not part of it.</summary>
    private bool IsFlowFooterActive =>
        _viewMode == TreeViewMode.Flow && FlowNavigator.IndexOf(_reversa?.FlowDocuments, _currentFilePath) >= 0;

    private (string? Prev, string? Next) GetFlowNeighbors(string? path) =>
        FlowNavigator.Neighbors(_reversa?.FlowDocuments, path);

    /// <summary>Tree label of a Flow document (e.g. "addenda/003-x.md"), else its file name.</summary>
    private string FlowLabel(string path) =>
        _reversa?.Flow.Find(path)?.Name ?? Path.GetFileName(path);

    private (string? PrevLabel, string? NextLabel) BuildFlowFooter(string path)
    {
        var (prev, next) = GetFlowNeighbors(path);
        return (prev != null ? FlowLabel(prev) : null, next != null ? FlowLabel(next) : null);
    }

    /// <summary>Prev/Next toolbar buttons: visible only in the Flow view on a Flow document.</summary>
    private void UpdateFlowNavigation()
    {
        var active = IsFlowFooterActive;
        var visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FlowPrevButton.Visibility = visibility;
        FlowNextButton.Visibility = visibility;
        if (!active) return;

        var (prev, next) = GetFlowNeighbors(_currentFilePath);
        FlowPrevButton.IsEnabled = prev != null;
        FlowNextButton.IsEnabled = next != null;
        FlowPrevButton.ToolTip = prev != null ? $"Previous: {FlowLabel(prev)} (Alt+PgUp)" : "First document of the flow";
        FlowNextButton.ToolTip = next != null ? $"Next: {FlowLabel(next)} (Alt+PgDn)" : "Last document of the flow";
    }

    private async void FlowPrev_Click(object sender, RoutedEventArgs e) => await NavigateFlow(previous: true);

    private async void FlowNext_Click(object sender, RoutedEventArgs e) => await NavigateFlow(previous: false);

    /// <summary>Footer link of the page; anything but "prev"/"next" is ignored.</summary>
    private Task HandleFlowMessage(string dir) => dir switch
    {
        "prev" => NavigateFlow(previous: true),
        "next" => NavigateFlow(previous: false),
        _ => Task.CompletedTask,
    };

    /// <summary>Opens the neighbor in the Flow; a navigation, so it enters the history.</summary>
    private async Task NavigateFlow(bool previous)
    {
        if (!IsFlowFooterActive) return;

        var (prev, next) = GetFlowNeighbors(_currentFilePath);
        var target = previous ? prev : next;
        if (target == null) return;

        if (!File.Exists(target))
        {
            StatusLabel.Text = $"{Path.GetFileName(target)} no longer exists — press F5 to refresh";
            return;
        }

        PushHistory();
        await OpenMarkdownFile(target);
    }

    #endregion

    #region Drag and Drop

    /// <summary>First dropped item, when it is a folder or a Markdown file.</summary>
    private static string? GetDroppedItem(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } items) return null;

        return IsSupportedDropPath(items[0]) ? items[0] : null;
    }

    private static bool IsSupportedDropPath(string path) =>
        Directory.Exists(path) || (FolderTreeService.IsMarkdownFile(path) && File.Exists(path));

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedItem(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var item = GetDroppedItem(e);
        // Report Copy (never Move): a Move result can make the drag source
        // (e.g. Explorer) delete the original file.
        e.Effects = item != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (item != null)
            await OpenDroppedPath(item);
    }

    /// <summary>A dropped folder opens as the root folder; a Markdown file as a document.</summary>
    private async Task OpenDroppedPath(string path)
    {
        Activate();
        if (Directory.Exists(path))
            await OpenFolder(path);
        else if (FolderTreeService.IsMarkdownFile(path) && File.Exists(path))
            await OpenUserFile(path);
    }

    /// <summary>
    /// Local path of a file:// URL (a file or folder dropped on the document),
    /// or null for any other URL.
    /// </summary>
    private static string? TryGetDroppedPath(string? uri)
    {
        if (uri == null || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var u = new Uri(uri);
            return u.IsFile ? Path.GetFullPath(u.LocalPath) : null;
        }
        catch
        {
            return null;
        }
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

    #region Zoom

    private void UpdateZoom()
    {
        if (!_webViewReady) return;
        WebView.ZoomFactor = _zoom.Factor;
        ZoomLabel.Text = _zoom.Label;
        ZoomInButton.IsEnabled = _zoom.CanZoomIn;
        ZoomOutButton.IsEnabled = _zoom.CanZoomOut;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_zoom.ZoomIn()) UpdateZoom();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_zoom.ZoomOut()) UpdateZoom();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_zoom.Reset()) UpdateZoom();
    }

    #endregion

    #region Theme

    private async void OnThemeChanged(object? sender, bool isDark)
    {
        // Restyle the WPF interface (toolbar, tree, status, dialogs, title bar)
        // and re-render the current file or placeholder with the new theme
        await Dispatcher.InvokeAsync(async () =>
        {
            ThemeApplier.Apply(isDark);
            if (!_webViewReady) return;

            ApplyWebViewColorScheme(isDark);

            if (_currentFilePath != null)
                await OpenMarkdownFile(_currentFilePath);
            else if (_placeholderMarkdown != null)
                ShowPlaceholder(_placeholderMarkdown);
        });
    }

    #endregion

    #region Theme selector

    /// <summary>
    /// Scrollbars and other built-in parts of the page follow the app's theme,
    /// not the Windows one (which may differ when a fixed theme is chosen).
    /// </summary>
    private void ApplyWebViewColorScheme(bool isDark)
    {
        try
        {
            WebView.CoreWebView2.Profile.PreferredColorScheme = isDark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
        catch { /* older runtime without the setting: keep the default */ }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeLightItem.IsChecked = _themeService.Mode == AppThemeMode.Light;
        ThemeDarkItem.IsChecked = _themeService.Mode == AppThemeMode.Dark;
        ThemeSystemItem.IsChecked = _themeService.Mode == AppThemeMode.System;

        ThemeMenu.PlacementTarget = ThemeButton;
        ThemeMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ThemeMenu.IsOpen = true;
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<AppThemeMode>(tag, out var mode))
        {
            _themeService.SetMode(mode);
            StatusLabel.Text = mode switch
            {
                AppThemeMode.Light => "Theme: light",
                AppThemeMode.Dark => "Theme: dark",
                _ => "Theme: system setting",
            };
        }
    }

    #endregion

    #region Keyboard Shortcuts

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            _ = ShowOpenFileDialog();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left)
        {
            NavigateBack_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.PageUp or Key.PageDown)
        {
            _ = NavigateFlow(previous: e.SystemKey == Key.PageUp);
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
            case Key.OemPlus or Key.Add:
                if (_zoom.ZoomIn()) UpdateZoom();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                if (_zoom.ZoomOut()) UpdateZoom();
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                if (_zoom.Reset()) UpdateZoom();
                e.Handled = true;
                break;
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
