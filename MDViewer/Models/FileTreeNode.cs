using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MDViewer.Models;

/// <summary>
/// A folder or Markdown file shown in the folder tree.
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
    public List<FileTreeNode> Children { get; } = new();

    public string Icon => IsDirectory ? (IsExpanded ? "📂" : "📁") : "📄";

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
