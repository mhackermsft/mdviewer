using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer.Tests;

public sealed class ReversaProjectServiceTests : IDisposable
{
    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private void State(string json = "{\"version\":\"1.3.6\"}") => _tmp.File(".reversa/state.json", json);

    private static IEnumerable<string> Stages(ReversaProject p) => p.Flow.Nodes.Select(n => n.Name);

    private static FileTreeNode Stage(ReversaProject p, string name) =>
        p.Flow.Nodes.Single(n => n.Name == name);

    #region Detection

    [Fact]
    public void Plain_folder_is_not_reversa()
    {
        _tmp.File("doc.md");
        Assert.Null(ReversaProjectService.Detect(_tmp.Root));
    }

    [Fact]
    public void Reads_the_version_from_state_json()
    {
        State();
        Assert.Equal("1.3.6", ReversaProjectService.Detect(_tmp.Root)!.Version);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\": 5}")]
    public void Unreadable_state_still_detects_with_defaults(string json)
    {
        State(json);

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Null(project.Version);
        Assert.Equal("_reversa_sdd", project.OutputFolder);
        Assert.Equal("_reversa_forward", project.ForwardFolder);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\\\b")]
    [InlineData("C:")]
    [InlineData(" spaced ")]
    public void Output_folder_must_be_a_single_segment_under_the_root(string folder)
    {
        State($"{{\"output_folder\":\"{folder}\"}}");
        Assert.Equal("_reversa_sdd", ReversaProjectService.Detect(_tmp.Root)!.OutputFolder);
    }

    [Fact]
    public void Custom_output_folder_is_used()
    {
        State("{\"output_folder\":\"specs\"}");
        _tmp.File("specs/architecture.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal("specs", project.OutputFolder);
        Assert.Contains(_tmp.PathOf("specs/architecture.md"), project.FlowDocuments);
    }

    [Fact]
    public void Active_feature_outside_the_forward_folder_is_ignored()
    {
        State();
        _tmp.Dir("_reversa_forward/001-x");
        _tmp.Dir("elsewhere");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"elsewhere\"}");

        Assert.Null(ReversaProjectService.Detect(_tmp.Root)!.ActiveFeatureDir);
    }

    [Fact]
    public void Detection_never_writes_to_the_folder()
    {
        State();
        _tmp.File(".claude/skills/reversa/SKILL.md", "---\nname: reversa\n---\n");
        _tmp.File("_reversa_sdd/architecture.md");
        _tmp.File("_reversa_forward/001-a/requirements.md");
        var before = _tmp.Snapshot();

        ReversaProjectService.Detect(_tmp.Root);

        Assert.Equal(before, _tmp.Snapshot());
    }

    #endregion

    #region Skill header

    [Fact]
    public void Reads_quoted_and_folded_header_values()
    {
        var path = _tmp.File("SKILL.md",
            "---\nname: 'it''s'\ndescription: >\n  first line\n  second line\nother: x\n---\nbody");

        Assert.Equal(("it's", "first line second line"), ReversaProjectService.ReadSkillHeader(path));
    }

    [Fact]
    public void Reads_double_quoted_values()
    {
        var path = _tmp.File("SKILL.md", "---\nname: \"x\"\ndescription: \"say \\\"hi\\\"\"\n---\n");

        Assert.Equal(("x", "say \"hi\""), ReversaProjectService.ReadSkillHeader(path));
    }

    [Theory]
    [InlineData("no front matter")]
    [InlineData("---\nname: never closed\n")]
    [InlineData("")]
    public void Malformed_header_gives_nulls(string content)
    {
        var path = _tmp.File("SKILL.md", content);
        Assert.Equal((null, null), ReversaProjectService.ReadSkillHeader(path));
    }

    [Fact]
    public void Missing_file_gives_nulls()
    {
        Assert.Equal((null, null), ReversaProjectService.ReadSkillHeader(_tmp.PathOf("none.md")));
    }

    #endregion

    #region Skills view

    [Fact]
    public void Prefers_the_claude_copy_of_a_skill_and_groups_by_family()
    {
        State();
        _tmp.File(".agents/skills/reversa-forward/SKILL.md", "---\nname: agents copy\n---\n");
        var claude = _tmp.File(".claude/skills/reversa-forward/SKILL.md", "---\nname: claude copy\ndescription: d\n---\n");
        _tmp.File(".claude/skills/reversa-forward/references/guide.md");
        _tmp.File(".claude/skills/my-tool/SKILL.md", "---\nname: mine\n---\n");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(2, project.SkillCount);
        Assert.Equal(new[] { "Forward", "Other" }, project.Skills.Nodes.Select(n => n.Name));
        var skill = project.Skills.Find(claude)!;
        Assert.Equal("claude copy", skill.Name);
        Assert.Equal("d", skill.Description);
        Assert.Equal(new[] { "references/guide.md" }, skill.Children.Select(c => c.Name));
    }

    [Fact]
    public void Unknown_reversa_skill_goes_to_reversa_other()
    {
        State();
        _tmp.File(".claude/skills/reversa-future/SKILL.md", "---\n---\n");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        var family = Assert.Single(project.Skills.Nodes);
        Assert.Equal("Reversa (other)", family.Name);
        Assert.Equal("reversa-future", Assert.Single(family.Children).Name); // folder name when no "name"
    }

    [Fact]
    public void No_skills_shows_a_single_notice()
    {
        State();

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(0, project.SkillCount);
        var notice = Assert.Single(project.Skills.Nodes);
        Assert.True(notice.IsGroup);
        Assert.Equal("No skills found", notice.Name);
    }

    #endregion

    #region Flow view

    [Fact]
    public void Core_stages_without_documents_are_pending_and_optional_ones_omitted()
    {
        State();

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(new[]
        {
            "Reconnaissance (pending)", "Excavation (pending)", "Interpretation (pending)",
            "Architecture (pending)", "Specification (pending)", "Review (pending)", "Evolution (pending)"
        }, Stages(project));
        Assert.All(project.Flow.Nodes, n => Assert.True(n.IsPending));
        Assert.Empty(project.FlowDocuments);
    }

    [Fact]
    public void Documents_are_classified_by_stage_in_engineering_order()
    {
        State();
        _tmp.File("_reversa_sdd/prd.md");
        _tmp.File("_reversa_sdd/inventory.md");
        _tmp.File("_reversa_sdd/domain.md");
        _tmp.File("_reversa_sdd/architecture.md");
        _tmp.File("_reversa_sdd/billing/requirements.md");
        _tmp.File("_reversa_sdd/gaps.md");
        _tmp.File("_reversa_sdd/loose-note.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal("prd.md", Assert.Single(Stage(project, "Conception").Children).Name);
        Assert.Equal("billing", Assert.Single(Stage(project, "Specification").Children).Name);
        Assert.Equal("loose-note.md", Assert.Single(Stage(project, "Other").Children).Name);
        Assert.Equal(
            new[] { "prd.md", "inventory.md", "domain.md", "architecture.md", "requirements.md", "gaps.md", "loose-note.md" },
            project.FlowDocuments.Select(Path.GetFileName));
    }

    [Fact]
    public void Each_document_appears_once_in_the_flow()
    {
        State();
        _tmp.File("_reversa_sdd/architecture.md");
        _tmp.File("_reversa_sdd/architecture/c4.md");
        _tmp.File("_reversa_sdd/flowcharts/login.md");

        var docs = ReversaProjectService.Detect(_tmp.Root)!.FlowDocuments;

        Assert.Equal(docs.Count, docs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, docs.Count);
    }

    [Fact]
    public void Features_follow_the_pipeline_order_with_the_addendum_last()
    {
        State();
        _tmp.File("_reversa_forward/001-login/onboarding.md");
        _tmp.File("_reversa_forward/001-login/actions.md");
        _tmp.File("_reversa_forward/001-login/requirements.md");
        _tmp.File("_reversa_forward/001-login/zzz-extra.md");
        _tmp.File("_reversa_sdd/addenda/001-login.md");
        _tmp.File("_reversa_forward/10-later/requirements.md");
        _tmp.File("_reversa_forward/2-earlier/requirements.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        var evolution = Stage(project, "Evolution");
        Assert.Equal(new[] { "001-login", "2-earlier", "10-later" }, evolution.Children.Select(c => c.Name));
        Assert.Equal(
            new[] { "requirements.md", "actions.md", "onboarding.md", "zzz-extra.md", "addenda/001-login.md" },
            evolution.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void Active_feature_is_highlighted()
    {
        State();
        _tmp.File("_reversa_forward/001-a/requirements.md");
        _tmp.File("_reversa_forward/002-b/requirements.md");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"_reversa_forward/002-b\"}");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(_tmp.PathOf("_reversa_forward/002-b"), project.ActiveFeatureDir);
        var features = Stage(project, "Evolution").Children;
        Assert.False(features[0].IsHighlighted);
        Assert.True(features[1].IsHighlighted);
    }

    #endregion
}
