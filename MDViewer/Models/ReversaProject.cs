using MDViewer.Services;

namespace MDViewer.Models;

/// <summary>What the folder tree panel shows for the root folder.</summary>
public enum TreeViewMode
{
    /// <summary>The regular folder tree (default, the only view outside Reversa folders).</summary>
    Files,
    /// <summary>One entry per Reversa skill, opening its SKILL.md.</summary>
    Skills,
    /// <summary>Reversa documents grouped by software engineering stage.</summary>
    Flow
}

/// <summary>
/// A root folder with the Reversa framework installed (it has
/// <c>.reversa/state.json</c>), as read by <see cref="ReversaProjectService"/>.
/// Read-only snapshot, rebuilt on every folder open and refresh.
/// </summary>
public sealed class ReversaProject
{
    public ReversaProject(
        string rootPath, string? version, string outputFolder, string forwardFolder,
        string? activeFeatureDir, FolderTree skills, int skillCount,
        FolderTree flow, IReadOnlyList<string> flowDocuments)
    {
        RootPath = rootPath;
        Version = version;
        OutputFolder = outputFolder;
        ForwardFolder = forwardFolder;
        ActiveFeatureDir = activeFeatureDir;
        Skills = skills;
        SkillCount = skillCount;
        Flow = flow;
        FlowDocuments = flowDocuments;
    }

    public string RootPath { get; }

    /// <summary><c>state.json#version</c>; null when unreadable or missing.</summary>
    public string? Version { get; }

    /// <summary>Extraction folder name (default <c>_reversa_sdd</c>).</summary>
    public string OutputFolder { get; }

    /// <summary>Forward features folder name (default <c>_reversa_forward</c>).</summary>
    public string ForwardFolder { get; }

    /// <summary>Full path of the active feature folder, if any.</summary>
    public string? ActiveFeatureDir { get; }

    public FolderTree Skills { get; }
    public int SkillCount { get; }
    public FolderTree Flow { get; }

    /// <summary>Flow documents in display order: the basis of previous/next.</summary>
    public IReadOnlyList<string> FlowDocuments { get; }
}
