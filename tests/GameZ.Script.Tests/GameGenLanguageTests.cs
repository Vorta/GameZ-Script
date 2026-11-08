using GameZ.Script.Core.Language;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class GameGenLanguageTests
{
    private static LanguageDocument Script(string text) => RecoilLanguage.Analyze("gamegen/test.GW", text, TestContext.Current.CancellationToken);
    [Fact]
    public void ScriptSyntaxIsDistinctFromResources()
    {
        var doc = Script("Object3DTranslate 1,2.0,3 # (not resource syntax\nsource \"a#comment\nFindNodes target\n");
        Assert.True(doc.Complete);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "GAMEZ_SYNTAX");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "GAMEZ_COMMAND");
        Assert.Contains(doc.Tokens, t => t.Text == "\"a");
        Assert.DoesNotContain(doc.Tokens, t => t.Text.Contains("comment"));
        Assert.DoesNotContain(Script("set worldName world\nset MISSION_DIR m1\nvar externally_registered set text").Diagnostics, d => d.Code == "GAMEZ_NUMBER");
    }
    [Theory]
    [InlineData("findnode target", "GAMEZ_COMMAND")]
    [InlineData("Object3DTranslate 1 nope 3", "GAMEZ_NUMBER")]
    [InlineData("Object3DTranslate NaN 0 0", "GAMEZ_NUMBER")]
    [InlineData("Object3DTranslate Infinity 0 0", "GAMEZ_NUMBER")]
    [InlineData("Object3DTranslate 1e999 0 0", "GAMEZ_NUMBER")]
    [InlineData("SetAltitudeSurface yes", "GAMEZ_BOOLEAN")]
    [InlineData("LoadGameGen file.gltf", "GAMEZ_OPERANDS")]
    [InlineData("Object3DTranslate 1 2 3 ignored", "GAMEZ_OPERANDS")]
    public void DiagnosesOperandsAndIgnoredSpelling(string text, string code) => Assert.Contains(Script(text).Diagnostics, d => d.Code == code);
    [Fact]
    public void ScriptAssistanceRespectsWhitespaceAndComments()
    {
        const string text = "Object3DTranslate 1    2 3 # ignored";
        var doc = Script(text);
        Assert.Equal("y", RecoilLanguage.SignatureHelp(doc, text.IndexOf("    ", StringComparison.Ordinal) + 1)!.Parameters[1].Label);
        Assert.Equal(1, RecoilLanguage.SignatureHelp(doc, text.IndexOf("    ", StringComparison.Ordinal) + 1)!.ActiveParameter);
        Assert.Null(RecoilLanguage.Hover(doc, text.Length));
        Assert.Empty(RecoilLanguage.Complete(doc, text.Length));
        Assert.Equal(3, RecoilLanguage.InlayHints(doc, new(0, text.Length), TestContext.Current.CancellationToken).Items.Count);
        Assert.Contains(RecoilLanguage.Complete(Script("   FindN"), 8), c => c.Label == "FindNode");
    }
    private static string Loaded(SourceWorldFixture f, string alias = "tank") =>
        File.ReadAllText(f.Path("gamegen/m1.gs")).Replace("GameZWriteZBDFile",
            $"source support\\bft1.gw\nGameZWriteZBDFile", StringComparison.Ordinal);
    private static void Vehicle(SourceWorldFixture f, string name = "tank_01") =>
        f.Write("data/m1/zrdr/aipath/aiv.zrd", $"({name} (0 (1.0 2.0 3.0) 80.0))");
    private static LanguageProject Project(SourceWorldFixture f, Dictionary<string, string>? overlay = null) =>
        LanguageProject.Analyze(f.Project, overlay, token: TestContext.Current.CancellationToken);
    [Fact]
    public void MissingTemplateUnderlinesExactAivNameAndUnsavedSupportLoadClearsIt()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/support/bft1.gw", "# no load");
        f.Write("gamegen/m1.gs", Loaded(f));
        var before = File.ReadAllBytes(f.Path("gamegen/support/bft1.gw"));
        var project = Project(f);
        var aiv = project.Documents["data/m1/zrdr/aipath/aiv.zrd"];
        var issue = Assert.Single(aiv.Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
        Assert.Equal("error", issue.Severity);
        Assert.Equal("tank_01", aiv.Text.Substring(issue.Span.Start, issue.Span.Length));
        Assert.Equal("m1", issue.Mission);
        Assert.NotEmpty(issue.Related!);
        string load = "SetModelDirectory ..\\data\\m2\\models\\bft\nLoadGameGen tank.gltf tank\nQuit\n";
        var overlay = new Dictionary<string, string> { ["gamegen/support/bft1.gw"] = load };
        var valid = Project(f, overlay);
        Assert.DoesNotContain(valid.Documents[aiv.Path].Diagnostics, d => d.Code == issue.Code);
        Assert.Contains(valid.Definition(aiv.Path, issue.Span.Start), l => l.Path == "gamegen/support/bft1.gw");
        Assert.Equal(before, File.ReadAllBytes(f.Path("gamegen/support/bft1.gw")));
        Assert.Contains(Project(f).Documents[aiv.Path].Diagnostics, d => d.Code == issue.Code);
    }
    [Theory]
    [InlineData("ifdef absent\nLoadGameGen tank.gltf tank\nendif")]
    [InlineData("Quit\nLoadGameGen tank.gltf tank")]
    [InlineData("LoadGameGen tank.gltf wrong_alias")]
    [InlineData("LoadGameGen tank.gltf tank\nDeleteTree tank")]
    public void UnavailableLoadsDoNotSatisfyAiv(string instructions)
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/support/bft1.gw", "SetModelDirectory ..\\data\\m2\\models\\bft\n" + instructions);
        f.Write("gamegen/m1.gs", Loaded(f));
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
    }
    [Fact]
    public void LateLoadAndOtherMissionCannotSupplyTemplate()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        string script = File.ReadAllText(f.Path("gamegen/m1.gs")).Replace("Quit", "SetModelDirectory ..\\data\\m2\\models\\bft\nLoadGameGen tank.gltf tank\nQuit", StringComparison.Ordinal);
        f.Write("gamegen/m1.gs", script);
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
    }
    [Fact]
    public void MissingSourceIsIncompleteRatherThanFalseMissingTemplate()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/m1.gs", Loaded(f));
        var aiv = Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"];
        Assert.Contains(aiv.Diagnostics, d => d.Code == "GAMEZ_CONTEXT_INCOMPLETE");
        Assert.DoesNotContain(aiv.Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
    }
    [Fact]
    public void IgnoredTypoDoesNotHideRealMissingTemplate()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/support/bft1.gw", "IgnoredTypo");
        f.Write("gamegen/m1.gs", Loaded(f));
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
    }
    [Theory]
    [InlineData("NewObject3D tank", false)]
    [InlineData("NewObject3D tank_01", false)]
    [InlineData("NewObject3D tank\nDeleteTree tank", true)]
    [InlineData("NewObject3D tank\nNewObject3D parent\nDeleteTree parent\nAddChild tank\nDeleteTree tank", true)]
    [InlineData("NewObject3D temp\nNodeSetDescription tank", false)]
    public void RuntimeNamesAreEvaluatedBeforeVehicleInitialization(string runtime, bool missing)
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/m1_zbd.gs", runtime);
        Assert.Equal(missing, Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics.Any(d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED"));
    }
    [Fact]
    public void HistoricalLookupNavigatesBeforeRename()
    {
        using var f = new SourceWorldFixture();
        const string support = "NewObject3D earlier\nFindNode earlier\nNodeSetDescription tank";
        f.Write("gamegen/support/bft1.gw", support); f.Write("gamegen/m1.gs", Loaded(f));
        var project = Project(f);
        var links = project.Definition("gamegen/support/bft1.gw", support.IndexOf("earlier", support.IndexOf("FindNode", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains(links, l => l.Path == "gamegen/support/bft1.gw" && l.Span.Start == support.IndexOf("earlier", StringComparison.Ordinal));
    }
    [Fact]
    public void InitializationCannotRenameWorldBeforeRead()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/support/bft1.gw", "NewObject3D tank"); f.Write("gamegen/m1.gs", Loaded(f));
        f.Write("gamegen/support/initm1.gw", "FindNode tank\nNodeSetDescription gone\nQuit");
        f.Write("gamegen/m1_zbd.gs", "GameZReadZBDFile ..\\m1\\gamez.zbd\nQuit");
        Assert.DoesNotContain(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
        f.Write("gamegen/m1_zbd.gs", "GameZReadZBDFile ..\\m2\\gamez.zbd\nQuit");
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_CONTEXT_INCOMPLETE");
    }
    [Fact]
    public void RuntimeHierarchyChangesAreAppliedOnce()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/m1_zbd.gs", "NewObject3D tank\nNewObject3D parent\nAddChild tank\nDeleteChild tank\nDeleteTree tank");
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
    }
    [Fact]
    public void MissingEntriesAndUnverifiedRuntimeCommandsStayIncomplete()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/m1_zbd.gs", "NewNode tank");
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_CONTEXT_INCOMPLETE");
        File.Delete(f.Path("gamegen/m1.gs"));
        Assert.Contains(Project(f).Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_CONTEXT_INCOMPLETE");
    }
    [Fact]
    public void CommonNetworkMemberTakesPrecedence()
    {
        using var f = new SourceWorldFixture();
        f.Write("data/m1/zrdr/aipath/aiv.zrd", "(tank_01 (1 (1.0 2.0 3.0) 80.0))");
        f.Write("data/common/zrdr/net_01.zrd", "(version (105))");
        f.Write("data/m1/zrdr/aipath/net_01.zrd", "(version (105))");
        var project = Project(f);
        var doc = project.Documents["data/m1/zrdr/aipath/aiv.zrd"];
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "GAMEZ_AIV_NETWORK");
        Assert.Contains(project.Definition(doc.Path, doc.Text.IndexOf('1', doc.Text.IndexOf('(') + 9)), l => l.Path == "data/common/zrdr/net_01.zrd");
    }
    [Fact]
    public void ObsoleteModelAliasDoesNotNavigateToUnloadedGltf()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        const string support = "SetModelDirectory ..\\data\\m2\\models\\bft\nLoadGameGen tank.flt tank";
        f.Write("gamegen/support/bft1.gw", support); f.Write("gamegen/m1.gs", Loaded(f));
        var project = Project(f);
        Assert.Contains(project.Documents["gamegen/support/bft1.gw"].Diagnostics, d => d.Code == "GAMEZ_MODEL_MISSING");
        Assert.Empty(project.Definition("gamegen/support/bft1.gw", support.IndexOf("tank.flt", StringComparison.Ordinal)));
        Assert.Contains(project.Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_CONTEXT_INCOMPLETE");
    }
    [Fact]
    public void MacroReferenceRetentionIsBounded()
    {
        using var f = new SourceWorldFixture();
        f.Write("gamegen/support/bft1.gw", "set x\nEcho " + string.Concat(Enumerable.Repeat("%x%", 20_000)));
        f.Write("gamegen/m1.gs", Loaded(f));
        Assert.Contains(Project(f).Documents["gamegen/support/bft1.gw"].Diagnostics, d => d.Code == "GAMEZ_LIMIT");
    }
    [Fact]
    public void CommonAivIsTheSelectedResource()
    {
        using var f = new SourceWorldFixture(); Vehicle(f);
        f.Write("gamegen/support/bft1.gw", "NewObject3D tank"); f.Write("gamegen/m1.gs", Loaded(f));
        f.Write("data/common/zrdr/aiv.zrd", "(missing_01 (0 (1.0 2.0 3.0) 80.0))");
        var project = Project(f);
        Assert.Contains(project.Documents["data/common/zrdr/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED" && d.Mission == "m1");
        Assert.DoesNotContain(project.Documents["data/m1/zrdr/aipath/aiv.zrd"].Diagnostics, d => d.Code == "GAMEZ_AIV_TEMPLATE_NOT_LOADED");
    }
}
