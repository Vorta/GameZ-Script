using System.Text;
using GameZ.Script.Core;
using GameZ.Script.Core.Animation;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;
using GameZ.Script.Core.Worlds;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class Mw3NativeBackendTests
{
    private sealed class Project : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gamez-native-" + Guid.NewGuid().ToString("N"));
        public Project() { Directory.CreateDirectory(Path.Combine(Root, "data")); Directory.CreateDirectory(Path.Combine(Root, "gamegen")); }
        public void Write(string path, string text)
        {
            string full = SourceProject.Resolve(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text, Encoding.Latin1);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public void NestedSourceProjectCannotChooseItsParentsGame()
    {
        using var project = new Project();
        project.Write("data/common/models/recoil.gltf", """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"extras":{"zstudio":{"sourceFormat":4}}}],"nodes":[]}
            """);
        project.Write("data/nested/data/common/models/native.gltf", """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"extras":{"zstudio":{"sourceFormat":4,"game":"mw3"}}}],"nodes":[]}
            """);
        project.Write("data/nested/data/c1/zrdr/net_01.zrd", "(version (106))");
        Directory.CreateDirectory(Path.Combine(project.Root, "data/nested/gamegen"));
        var token = TestContext.Current.CancellationToken;
        string child = Path.Combine(project.Root, "data/nested");
        Assert.Equal(GameVariant.Recoil, SourceGameProfile.Identify(project.Root, token));
        Assert.Null(SourceGameProfile.IdentifySources(project.Root, token: token));
        Assert.Equal(GameVariant.MechWarrior3, SourceGameProfile.Identify(child, token));
        Assert.Equal(GameVariant.MechWarrior3, SourceGameProfile.IdentifySources(child, token: token));
    }

    [Fact]
    public void SnapshotDetectsNativeIdentityAndExplicitOverrideRemainsLocal()
    {
        using var project = new Project();
        Assert.Null(SourceGameProfile.Identify(project.Root, TestContext.Current.CancellationToken));
        Assert.Equal(GameVariant.Recoil, SourceProject.Game(project.Root, TestContext.Current.CancellationToken));
        project.Write("data/common/models/native.gltf", """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[],"extras":{"zstudio":{"sourceFormat":4,"game":"mw3"}}}],"nodes":[]}
            """);
        Assert.Equal(GameVariant.MechWarrior3, SourceProject.Game(project.Root, TestContext.Current.CancellationToken));
        var native = new SourceBuilder.Snapshot(project.Root);
        Assert.Equal(GameVariant.MechWarrior3, native.Files().Game);
        Assert.Equal(GameVariant.Recoil, new SourceBuilder.Snapshot(project.Root, game: GameVariant.Recoil).Game);
        Assert.Equal(GameVariant.MechWarrior3, native.Game);
    }

    [Fact]
    public void NativeVehicleCompilationDoesNotApplyRecoilBeamPauseShape()
    {
        using var project = new Project();
        const string text = "(vehicle (common_mode (weapons ((laser 1 0 3 1.2 5.0 50.0 1 firepoint (left right))))))";
        project.Write("data/common/zrdr/vehicle.zrd", text);
        var plan = new SourceOutputPlan("reader.zbd", "archive", ["data/common/zrdr/vehicle.zrd"]);
        var native = new SourceBuilder.Snapshot(project.Root, game: GameVariant.MechWarrior3);
        var built = SourceBuilder.Build(project.Root, plan, native, TestContext.Current.CancellationToken);
        Assert.Equal(ZrdWriter.Write(ZrdText.Parse(text, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), Assert.Single(ArchiveSources.Read(built.Bytes)).Payload.ToArray());
        var document = FormatRegistry.Default.OpenBytes("vehicle.zrd", Encoding.Latin1.GetBytes(text), token: TestContext.Current.CancellationToken);
        Assert.Single(new ResourceMembers(document, TestContext.Current.CancellationToken, new(GameVariant.MechWarrior3)).Members);
        Assert.Throws<InvalidDataException>(() => new ResourceMembers(document, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NativeAnimationCompilesPuffersAndRepeatedRootBindingsAsVersion39()
    {
        using var project = new Project();
        project.Write("data/c1/zrdr/anim.zad", """
            (ANIMATION_DEFINITIONS (
              ANIMATION_LIST (
                ANIMATION_DEFINITION (NAME ("bot#") ANIMATION_NAME (trail)
                  SEQUENCE_DEFINITION (NAME (start)
                    PUFFER_STATE (NAME (smoke) ACTIVE_STATE (ACTIVE)
                      PARTICLES_PER_EMISSION (3) TRANSLATION (1.0 2.0 3.0) SIZE_RANGE (1.0 2.0))
                  )
                )
              )
            ))
            """);
        var snapshot = new SourceBuilder.Snapshot(project.Root, game: GameVariant.MechWarrior3);
        var result = AnimationCompiler.Compile(snapshot.Files(), "data/c1/zrdr/anim.zad", ["bot", "bot"], GameVariant.MechWarrior3, TestContext.Current.CancellationToken);
        Assert.Null(result.EngineRejection);
        Assert.Equal(39u, result.Package.Version);
        Assert.Equal(["trail#0", "trail#1"], result.Package.Entries.Skip(1).Select(e => e.Name));
        foreach (var entry in result.Package.Entries.Skip(1))
        {
            Assert.Equal(2, entry.Puffers.Count);
            Assert.Equal(42, Assert.Single(Assert.Single(entry.Sequences).Events).Type);
        }
    }

    [Fact]
    public void SampledMotionRetainsClosingSampleAndRejectsShortTracks()
    {
        const string source = "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1) PART (NAME (hip) SAMPLES ((0 0 0 1 0 0 0) (1 2 3 1 0 0 0))))";
        var bytes = Mw3LibrarySources.BuildMotion(Encoding.Latin1.GetBytes(source), TestContext.Current.CancellationToken);
        var motion = MotionClip.Read(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(1, motion.FrameCount);
        Assert.Throws<InvalidDataException>(() => Mw3LibrarySources.BuildMotion(Encoding.Latin1.GetBytes(source.Replace(" (1 2 3 1 0 0 0)", "", StringComparison.Ordinal)), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("FRAME_COUNT (1)", "FRAME_COUNT (1.9)", "FRAME_COUNT")]
    [InlineData("FRAME_COUNT (1)", "FRAME_COUNT (1 2)", "FRAME_COUNT")]
    [InlineData("FRAME_COUNT (1)", "FRAME_COUNT (1) FRAME_COUNT (1)", "FRAME_COUNT")]
    [InlineData("LOOP_SECONDS (1.0)", "LOOP_SECONDS (1.0 2.0)", "LOOP_SECONDS")]
    [InlineData("NAME (hip)", "NAME (hip other)", "NAME")]
    [InlineData("SAMPLES (", "SAMPLES (junk ", "SAMPLES")]
    [InlineData("PART (", "junk PART (", "field")]
    public void NativeMotionCompilerRejectsOrdinaryStructuralEditingErrors(string before, string after, string message)
    {
        const string valid = "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1) PART (NAME (hip) SAMPLES ((0 0 0 1 0 0 0) (1 2 3 1 0 0 0))))";
        string source = valid.Replace(before, after, StringComparison.Ordinal);
        var error = Assert.Throws<InvalidDataException>(() => Mw3LibrarySources.BuildMotion(Encoding.Latin1.GetBytes(source), TestContext.Current.CancellationToken));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeMotionGroupingIsEquivalentAndDuplicateRootsFailClearly()
    {
        const string valid = "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1) PART (NAME (hip) SAMPLES ((0 0 0 1 0 0 0) (1 2 3 1 0 0 0))))";
        byte[] Compile(string text) => Mw3LibrarySources.BuildMotion(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken);
        Assert.Equal(Compile(valid), Compile("((" + valid + "))"));
        Assert.Equal(Compile(valid), Compile(valid.Replace("FRAME_COUNT (1)", "FRAME_COUNT (1.0)", StringComparison.Ordinal)));
        var error = Assert.Throws<InvalidDataException>(() => Compile(valid + "\n" + valid));
        Assert.Contains("exactly one MOTION", error.Message);
    }
}
