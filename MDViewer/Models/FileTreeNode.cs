using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MDViewer.Models;

/// <summary>
/// A folder or Markdown file shown in the folder tree, or the synthetic
/// "Open files" section that groups files opened outside the root folder.
/// </summary>
public sealed class FileTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public FileTreeNode(string name, string fullPath, bool isDirectory, FileTreeNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileTreeNode? Parent { get; }
    /// <summary>
    /// True only for the synthetic "Open files" section node. A section is a
    /// container (<see cref="IsDirectory"/> is also true) and never a document.
    /// </summary>
    public bool IsSection { get; init; }

    /// <summary>Tooltip text (e.g. a skill's description); null shows <see cref="FullPath"/>.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// A grouping node of the Reversa views (Flow stage, skill family or the
    /// "No skills found" notice): a container (<see cref="IsDirectory"/> is
    /// also true), never a document. Its <see cref="FullPath"/> is a synthetic
    /// "reversa:" key that never exists on disk.
    /// </summary>
    public bool IsGroup { get; init; }

    /// <summary>A core Flow stage that has no documents yet (shown dimmed).</summary>
    public bool IsPending { get; init; }

    /// <summary>Emphasized node (the active Reversa feature).</summary>
    public bool IsHighlighted { get; init; }

    /// <summary>Fixed icon replacing the computed folder/file icon.</summary>
    public string? IconOverride { get; init; }

    /// <summary>Tree tooltip: the description, else the path; none for groups.</summary>
    public string? ToolTipText => IsGroup ? null : Description ?? FullPath;

    /// <summary>Observable so the "Open files" section updates in place.</summary>
    public ObservableCollection<FileTreeNode> Children { get; } = new();

    public string Icon => IconOverride ?? (IsSection ? "🗂️" : IsDirectory ? (IsExpanded ? "📂" : "📁") : "📄");

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
                OnPropertyChanged(nameof(Icon));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
