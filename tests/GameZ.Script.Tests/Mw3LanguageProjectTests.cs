using System.Text;
using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class Mw3LanguageProjectTests
{
    private sealed class Project : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gamez-native-language-" + Guid.NewGuid().ToString("N"));
        public Project()
        {
            Write("data/c1/zrdr/anim.zad", "(ANIMATION_DEFINITIONS (ANIMATION_LIST ()))");
            Write("gamegen/c1.gs", "NewWorld world\nFindNode world\nGameGenSetWorld world\nWorldOrigin 0 256\nWorldExtents 256 -256\nWorldPartition 256 -256\nGameZWriteZBDFile zbd/c1/gamez.zbd\nQuit\n");
        }
        public void Write(string path, string text)
        {
            string file = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, Encoding.Latin1);
        }
        public LanguageProject Analyze(bool deep = false, Dictionary<string, string>? overlays = null, bool missions = false) =>
            LanguageProject.Analyze(Root, overlays, deep, TestContext.Current.CancellationToken, missionAnalysis: missions, game: GameVariant.MechWarrior3);
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }
    private const string Track = "SI Animation Script\nFRAMES: 2\nOBJECTS: 1\nFrame: 1\nObject: target\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\nFrame: 4\nObject: target\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 1 0 0\n";

    [Fact]
    public void ClosedSiTracksNavigateToCapturedLineAndUnsavedTrackOverridesDisk()
    {
        using var f = new Project();
        const string definition = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_NAME (move) ACTIVATION (ON_CALL) SEQUENCE_DEFINITION (NAME (start) OBJECT_MOTION_SI_SCRIPT (NAME (target) SCRIPT_FILENAME (travel.zan) SCRIPT_FRAME_RATE (30)))))))";
        f.Write("data/c1/zrdr/anim.zad", definition);
        f.Write("data/c1/zrdr/travel.zan", Track);
        var project = f.Analyze();
        Assert.DoesNotContain("data/c1/zrdr/travel.zan", project.Documents.Keys);
        int name = definition.IndexOf("NAME (target)", StringComparison.Ordinal) + 6;
        var location = Assert.Single(project.Definition("data/c1/zrdr/anim.zad", name), l => l.Path.EndsWith("travel.zan", StringComparison.Ordinal));
        Assert.Equal(Track.IndexOf("target", StringComparison.Ordinal), location.Span.Start);
        Assert.Contains(Track.IndexOf("Object:", StringComparison.Ordinal), project.TargetLineStarts(location.Path));
        string edited = Track.Replace("target", "other", StringComparison.Ordinal);
        var changed = f.Analyze(overlays: new() { ["data/c1/zrdr/travel.zan"] = edited });
        Assert.DoesNotContain(changed.Definition("data/c1/zrdr/anim.zad", name), l => l.Path.EndsWith("travel.zan", StringComparison.Ordinal));
        Assert.Equal(Track, File.ReadAllText(Path.Combine(f.Root, "data/c1/zrdr/travel.zan"), Encoding.Latin1));
    }

    [Fact]
    public void NamelessLegacyTrackCanBeSelectedByAnyDefinitionNode()
    {
        using var f = new Project();
        const string definition = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_NAME (move) ACTIVATION (ON_CALL) SEQUENCE_DEFINITION (NAME (start) OBJECT_MOTION_SI_SCRIPT (NAME (target) SCRIPT_FILENAME (travel.zan) SCRIPT_FRAME_RATE (30)))))))";
        const string track = "# one implicit track\nFRAME 0 POSITION 0 0 0\nFRAME 4 POSITION 1 0 0\n";
        f.Write("data/c1/zrdr/anim.zad", definition); f.Write("data/c1/zrdr/travel.zan", track);
        var project = f.Analyze();
        int name = definition.IndexOf("NAME (target)", StringComparison.Ordinal) + 6;
        var location = Assert.Single(project.Definition("data/c1/zrdr/anim.zad", name));
        Assert.EndsWith("travel.zan", location.Path);
        Assert.Equal(track.IndexOf("FRAME", StringComparison.Ordinal), location.Span.Start);
    }

    [Fact]
    public void ExhaustiveValidationIncludesUnusedClosedMotionWithoutRetainingItsTokens()
    {
        using var f = new Project();
        const string motion = "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (2) PART (NAME (hip) SAMPLES ((0 0 0 1 0 0 0))))";
        f.Write("data/common/mechs/motions/test_walk.zan", motion);
        var light = f.Analyze();
        Assert.DoesNotContain(light.ValidationDocuments, d => d.Path.EndsWith("test_walk.zan", StringComparison.Ordinal));
        var deep = f.Analyze(deep: true);
        var doc = Assert.Single(deep.ValidationDocuments, d => d.Path.EndsWith("test_walk.zan", StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, d => d.Severity == "error");
        Assert.Empty(doc.Tokens);
        Assert.Equal(3, deep.CheckedFileCount);
        Assert.False(File.Exists(Path.Combine(f.Root, "c1/anim.zbd")));
        Assert.Equal(motion, File.ReadAllText(Path.Combine(f.Root, doc.Path), Encoding.Latin1));
    }

    [Fact]
    public void SampledPartNavigatesIntoItsExactNativeModelNode()
    {
        using var f = new Project();
        const string motion = "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1) PART (NAME (hip) SAMPLES ((0 0 0 1 0 0 0)(0 0 0 1 0 0 0))))";
        const string model = "{\n\"asset\":{\"version\":\"2.0\"},\n\"nodes\":[{\"name\":\"thor\"},{\"name\":\"hip\"}],\n\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
        const string path = "data/common/mechs/motions/thor_walk.zan";
        f.Write(path, motion); f.Write("data/common/mechs/models/mech_thor.gltf", model);
        var project = f.Analyze(overlays: new() { [path] = motion });
        var location = Assert.Single(project.Definition(path, motion.IndexOf("hip", StringComparison.Ordinal)));
        Assert.EndsWith("mech_thor.gltf", location.Path);
        Assert.Equal("\"hip\"", model.Substring(location.Span.Start, location.Span.Length));
        Assert.Equal(4, project.TargetLineStarts(location.Path).Count);
    }

    [Fact]
    public void NativeRuntimeEntryUsesUsezbdBranchAndDoesNotRequireRecoilCompanions()
    {
        using var f = new Project();
        f.Write("gamegen/c1.gs", """
            ifndef USEZBD
            NewWorld world
            FindNode world
            GameGenSetWorld world
            WorldOrigin 0 256
            WorldExtents 256 -256
            WorldPartition 256 -256
            NewObject3D original
            GameZWriteZBDFile zbd/c1/gamez.zbd
            endif
            ifdef USEZBD
            GameZReadZBDFile zbd/c1/gamez.zbd
            FindNode original
            NodeSetDescription renamed
            endif
            Quit
            """);
        f.Write("data/common/zrdr/vehicle.zrd", "(renamed (common_mode (health (10.0))))");
        f.Write("data/c1/zrdr/aipath/m1/aiv.zrd", "(renamed_01 (12 (0.0 0.0 0.0) 0.0 1))");
        var project = f.Analyze(missions: true);
        var aiv = project.Documents["data/c1/zrdr/aipath/m1/aiv.zrd"];
        Assert.DoesNotContain(aiv.Diagnostics, d => d.Code is "GAMEZ_AIV_TEMPLATE_NOT_LOADED" or "GAMEZ_AIV_NETWORK");
        Assert.Contains(project.Definition(aiv.Path, aiv.Text.IndexOf("renamed_01", StringComparison.Ordinal)), l => l.Path == "gamegen/c1.gs");
    }

    [Fact]
    public void NativeReferencesStayWithinTheirMissionReader()
    {
        using var f = new Project();
        const string source = "(version (106) name (net_01) node_00 (12 (0.0 0.0 0.0) (-1) valve (1 switch_on 1)))";
        const string path = "data/c1/zrdr/aipath/m1/net_01.zrd";
        f.Write(path, source);
        f.Write("data/c1/zrdr/aipath/m1/valves.zrd", "(switch_on (update (later 1)))");
        f.Write("data/c1/zrdr/aipath/m2/valves.zrd", "(switch_on (update (different 1)))");
        var project = f.Analyze();
        var target = Assert.Single(project.Definition(path, source.IndexOf("switch_on", StringComparison.Ordinal)));
        Assert.Equal("data/c1/zrdr/aipath/m1/valves.zrd", target.Path);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("Common")]
    [InlineData("COMMON")]
    public void NativeCommonMemberPrecedenceIgnoresDirectoryCase(string common)
    {
        using var f = new Project();
        const string source = "(version (106) node_00 (12 (0.0 0.0 0.0) (-1) valve (1 switch_on 1)))";
        const string path = "data/c1/zrdr/aipath/m1/net_01.zrd";
        f.Write(path, source);
        f.Write("data/c1/zrdr/aipath/m1/valves.zrd", "(switch_on (update (local 1)))");
        string commonPath = $"data/{common}/zrdr/valves.zrd";
        f.Write(commonPath, "(switch_on (update (global 1)))");
        var project = f.Analyze();
        var target = Assert.Single(project.Definition(path, source.IndexOf("switch_on", StringComparison.Ordinal)));
        Assert.Equal(commonPath, target.Path);
    }

    [Fact]
    public void TriggerRowsResolveTheirTypedRegionAndValveSlots()
    {
        using var f = new Project();
        const string source = "(player (0 (0.0 0.0 0.0) 0.0 1 vlist ((1 0 0.25 0 0 0.0 0 0.8 1 reg_here 0 0.0 0 60.0 1 switch_on))))";
        const string path = "data/c1/zrdr/aipath/m1/aiv.zrd";
        f.Write(path, source);
        f.Write("data/c1/zrdr/aipath/m1/valves.zrd", "(switch_on (update (next 1)) \"switch other\" (update (next 1)))");
        f.Write("data/c1/zrdr/aipath/m1/reg_here.zrd", "(version (108) name (reg_here))");
        var project = f.Analyze();
        Assert.EndsWith("reg_here.zrd", Assert.Single(project.Definition(path, source.IndexOf("reg_here", StringComparison.Ordinal))).Path);
        Assert.EndsWith("valves.zrd", Assert.Single(project.Definition(path, source.IndexOf("switch_on", StringComparison.Ordinal))).Path);
        Assert.Contains(project.Complete(path, source.IndexOf("switch_on", StringComparison.Ordinal) + 4), c => c.Label == "switch_on");
        Assert.Equal("\"switch other\"", Assert.Single(project.Complete(path, source.IndexOf("switch_on", StringComparison.Ordinal) + 4), c => c.Label == "switch other").InsertText);
    }

    [Fact]
    public void NativeNodeSuggestionsFilterBeforeResultLimit()
    {
        using var f = new Project();
        var script = new StringBuilder("ifndef USEZBD\nNewWorld world\nFindNode world\nGameGenSetWorld world\nWorldOrigin 0 256\nWorldExtents 256 -256\nWorldPartition 256 -256\n");
        for (int i = 0; i < 300; i++) script.Append("NewObject3D actor_").Append(i.ToString("000", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        script.AppendLine("FindNode actor_299\nGameZWriteZBDFile zbd/c1/gamez.zbd\nendif\nifdef USEZBD\nGameZReadZBDFile zbd/c1/gamez.zbd\nendif\nQuit");
        f.Write("gamegen/c1.gs", script.ToString());
        var project = f.Analyze(missions: true);
        int at = script.ToString().IndexOf("FindNode actor_299", StringComparison.Ordinal) + "FindNode actor_29".Length;
        Assert.Contains(project.Complete("gamegen/c1.gs", at), c => c.Label == "actor_299");
        Assert.DoesNotContain(project.ValidationDocuments.SelectMany(d => d.Diagnostics), d => d.Code == "GAMEZ_LIMIT");
    }
}
