using System.Text;
using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using GameZ.Script.Core.Sources;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class NestedLanguageProjectTests
{
    private sealed class Project : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gamez-nested-analysis-" + Guid.NewGuid().ToString("N"));
        public Project()
        {
            Directory.CreateDirectory(Path.Combine(Root, "gamegen"));
            Write("data/m1/zrdr/anim.zad", "(ANIMATION_DEFINITIONS (ANIMATION_LIST ()))");
            Write("data/m1/zrdr/net_01.zrd", "(node_00 (0 (0.0 0.0 0.0) (-1 -1 -1)))");
            Write("data/m1/models/identity.gltf", Model(false));
        }
        public void Write(string path, string text)
        {
            string full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, Encoding.Latin1);
        }
        public void Child(string prefix)
        {
            Directory.CreateDirectory(Path.Combine(Root, prefix, "gamegen"));
            Write(prefix + "/data/c1/models/identity.gltf", Model(true));
            Write(prefix + "/data/c1/zrdr/net_01.zrd", "(version (106) node_00 (0 (0.0 0.0 0.0) (1)) node_01 (0 (1.0 0.0 0.0) (-1)))");
            Write(prefix + "/data/c1/zrdr/anim.zad", "(ANIMATION_DEFINITIONS (ANIMATION_LIST ()))");
            Write(prefix + "/data/c1/zrdr/child.zan", "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1) PART (NAME (hip) SAMPLES ((0 0 0 1 0 0 0)(0 0 0 1 0 0 0))))");
            Write(prefix + "/gamegen/support.gw", "set CHILD yes\n");
        }
        public LanguageProject Analyze(bool deep, Dictionary<string, string>? overlays = null, Dictionary<string, byte[]>? sourceOverlay = null) =>
            LanguageProject.Analyze(Root, overlays, deep, TestContext.Current.CancellationToken, sourceOverlay,
                missionAnalysis: false, game: null);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static string Model(bool native) => "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"nodes\":[{\"name\":\"foreign\"}],\"scenes\":[{\"nodes\":[0],\"extras\":{\"zstudio\":{\"sourceFormat\":4" + (native ? ",\"game\":\"mw3\"" : "") + "}}}]}";
    private const string World = "NewWorld world\nFindNode world\nGameGenSetWorld world\nWorldOrigin 0 256\nWorldExtents 256 -256\nWorldPartition 256 -256\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentRecoilAnalysisDoesNotIndexOrValidateNestedMw3Projects(bool deep)
    {
        using var f = new Project();
        const string child = "data/nested";
        f.Child(child);
        string childNet = child + "/data/c1/zrdr/net_01.zrd";
        var overlays = new Dictionary<string, string> { [childNet] = "(version (106) node_00 (0 (0.0 0.0 0.0) ()))" };
        var sourceOverlay = new Dictionary<string, byte[]> { [child + "/data/c1/zrdr/only-in-buffer.zrd"] = Encoding.Latin1.GetBytes("(broken") };
        var parent = f.Analyze(deep, overlays, sourceOverlay);
        Assert.Equal(GameVariant.Recoil, parent.Game);
        Assert.Equal(2, parent.CheckedFileCount);
        Assert.All(parent.ValidationDocuments, d => Assert.False(d.Path.StartsWith(child + "/", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, parent.Documents.Count);
        Assert.Empty(parent.Documents["data/m1/zrdr/net_01.zrd"].Diagnostics);
        Assert.Empty(parent.Complete(childNet, 1));
        Assert.Empty(parent.Definition(childNet, 1));
        Assert.Equal("", parent.TargetText(childNet));
        var native = LanguageProject.Analyze(Path.Combine(f.Root, child), token: TestContext.Current.CancellationToken, missionAnalysis: false);
        Assert.Equal(GameVariant.MechWarrior3, native.Game);
        Assert.Empty(native.Documents["data/c1/zrdr/net_01.zrd"].Diagnostics);
        Assert.Equal(4, native.CheckedFileCount);
    }

    [Fact]
    public void ExplicitModelAndIncludePathsCannotCrossIntoIndependentNestedProjects()
    {
        using var f = new Project();
        f.Child("data/nested");
        f.Child("gamegen/nested");
        const string script = World + "SetModelDirectory ..\\data\\nested\\data\\c1\\models\nLoadGameGen identity.gltf foreign\nsource nested/gamegen/support.gw\nGameZWriteZBDFile zbd/m1/gamez.zbd\nQuit\n";
        f.Write("gamegen/m1.gs", script);
        var project = LanguageProject.Analyze(f.Root, token: TestContext.Current.CancellationToken, missionAnalysis: true);
        Assert.Equal(3, project.CheckedFileCount);
        var doc = project.Documents["gamegen/m1.gs"];
        Assert.Contains(doc.Diagnostics, d => d.Code == "GAMEZ_MODEL_MISSING");
        Assert.Contains(doc.Diagnostics, d => d.Code == "GAMEZ_SOURCE_MISSING");
        Assert.Empty(project.Definition(doc.Path, script.IndexOf("identity.gltf", StringComparison.Ordinal)));
        Assert.Empty(project.Definition(doc.Path, script.IndexOf("nested/gamegen", StringComparison.Ordinal)));
        Assert.DoesNotContain(project.Complete(doc.Path, script.IndexOf("identity.gltf", StringComparison.Ordinal)), c => c.Label == "identity.gltf");
    }

    [Fact]
    public void DeepAnalysisDoesNotDiscoverEffectsInsideANestedCommonProject()
    {
        using var f = new Project();
        f.Write("gamegen/m1.gs", World + "GameZWriteZBDFile zbd/m1/gamez.zbd\nQuit\n");
        f.Child("data/common/nested");
        f.Write("data/common/nested/data/common/zrdr/effects.zrd", "broken (\0");
        var project = f.Analyze(deep: true);
        Assert.Equal(3, project.CheckedFileCount);
        Assert.DoesNotContain(project.Documents["data/m1/zrdr/anim.zad"].Diagnostics, d => d.Code == "GAMEZ_PROJECT");
        Assert.DoesNotContain(project.ValidationDocuments, d => d.Path.StartsWith("data/common/nested/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdentityOwnershipAcceptsRootPathsWithTrailingSeparators()
    {
        using var f = new Project();
        f.Child("data/nested");
        string parent = f.Root + Path.DirectorySeparatorChar;
        string child = Path.Combine(f.Root, "data/nested") + Path.DirectorySeparatorChar;
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(GameVariant.Recoil, SourceGameProfile.Identify(parent, token));
        Assert.Null(SourceGameProfile.IdentifySources(parent, token: token));
        Assert.Equal(GameVariant.MechWarrior3, SourceGameProfile.Identify(child, token));
        Assert.Equal(GameVariant.MechWarrior3, SourceGameProfile.IdentifySources(child, token: token));
        Assert.Equal(2, LanguageProject.Analyze(parent, token: token, missionAnalysis: false).CheckedFileCount);
    }

    [Fact]
    public void CompilerSnapshotsKeepTheirExistingUnfilteredReadContract()
    {
        using var f = new Project();
        f.Child("data/nested");
        const string path = "data/nested/data/c1/zrdr/net_01.zrd";
        var snapshot = new SourceBuilder.Snapshot(f.Root, game: GameVariant.Recoil);
        Assert.True(snapshot.Files().FolderExists("data/nested/data/c1/zrdr"));
        Assert.True(snapshot.Files().Exists(path));
        Assert.Equal(File.ReadAllBytes(Path.Combine(f.Root, path)), snapshot.Files().Read(path, TestContext.Current.CancellationToken));
        snapshot.CheckUnchanged(TestContext.Current.CancellationToken);
    }
}
