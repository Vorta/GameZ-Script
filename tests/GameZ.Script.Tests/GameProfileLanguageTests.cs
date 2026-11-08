using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class GameProfileLanguageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static LanguageDocument Neutral(string path, string text) => RecoilLanguage.AnalyzeResolved(path, text, null, Token);

    [Theory]
    [InlineData(null)]
    [InlineData(GameVariant.Recoil)]
    [InlineData(GameVariant.MechWarrior3)]
    public void ObjectiveNodeStateIsNeverASpatialNetworkDeclaration(GameVariant? game)
    {
        const string source = "(m1 (primary (condition (node_state (node (citybuild01 healthy) state (active))))))";
        var doc = RecoilLanguage.AnalyzeResolved("objectives.zrd", source, game, Token);
        Assert.Equal("objectives", doc.Family);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(game, doc.ResolvedGame);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code.Contains("NODE", StringComparison.Ordinal));
        // Legacy callers still get their previous RECOIL default, without the false network inference.
        Assert.Empty(RecoilLanguage.Analyze("objectives.zrd", source, Token).Diagnostics);
    }

    [Theory]
    [InlineData("node_state")]
    [InlineData("node_00")]
    public void NestedNodeNamesDoNotClassifyAnUnrelatedResourceAsANetwork(string key)
    {
        var doc = RecoilLanguage.Analyze("other.zrd", $"(custom (condition ({key} (invalid))))", Token);
        Assert.Equal("resource", doc.Family);
        Assert.Empty(doc.Diagnostics);
    }

    [Theory]
    [InlineData("node_99")]
    [InlineData("node_bad")]
    [InlineData("node_+1")]
    public void RealNetworkFilesStillValidateInvalidTopLevelNames(string key)
    {
        var doc = RecoilLanguage.Analyze("net_01.zrd", $"((\"{key}\" (0 (1.0 2.0 3.0) (-1 -1 -1))))", Token);
        Assert.Equal("network", doc.Family);
        Assert.Contains(doc.Diagnostics, d => d.Code == "RECOIL_NODE_ID");
    }

    [Fact]
    public void NumericRootNodesStillInferANetworkEvenWhenTheirIndexIsInvalid()
    {
        var doc = RecoilLanguage.Analyze("other.zrd", "(node_99 (0 (1.0 2.0 3.0) (-1 -1 -1)))", Token);
        Assert.Equal("network", doc.Family);
        Assert.Contains(doc.Diagnostics, d => d.Code == "RECOIL_NODE_ID");
    }

    [Fact]
    public void GenuineRecoilNetworkShapeTypeAndLinkChecksRemainActive()
    {
        const string source = "(node_00 (0 (1 2.0 3.0) (1 -1 -1)) node_01 (0 (1.0 2.0 3.0) (-1 -1 -1)) node_02 (0))";
        var doc = RecoilLanguage.Analyze("net_01.zrd", source, Token);
        Assert.Contains(doc.Diagnostics, d => d.Code == "GAMEZ_TYPE");
        Assert.Contains(doc.Diagnostics, d => d.Code == "RECOIL_NODE_SHAPE");
        // An invalid node is deliberately absent from the engine-resolvable link set.
        var dangling = RecoilLanguage.Analyze("net_01.zrd", "(node_00 (0 (1.0 2.0 3.0) (98 -1 -1)))", Token);
        Assert.Contains(dangling.Diagnostics, d => d.Code == "RECOIL_NODE_LINK");
    }

    [Fact]
    public void NeutralResourceKeepsSyntaxAndOutlineButHasNoRecoilSchemaAssistance()
    {
        const string source = "(node_00 (0 (1 2.0 3.0) (-1 -1 -1)))";
        var doc = Neutral("net_01.zrd", source);
        Assert.False(doc.HasGameProfile);
        Assert.Null(doc.ResolvedGame);
        Assert.Empty(doc.Diagnostics);
        Assert.NotEmpty(doc.Tokens);
        Assert.NotEmpty(doc.Symbols);
        Assert.Empty(RecoilLanguage.Complete(doc, 1));
        Assert.Null(RecoilLanguage.Hover(doc, 1));
        int number = source.IndexOf("1 2", StringComparison.Ordinal);
        Assert.Null(RecoilLanguage.SignatureHelp(doc, number));
        Assert.Empty(RecoilLanguage.InlayHints(doc, new(0, source.Length), Token).Items);
        var known = RecoilLanguage.Analyze("net_01.zrd", source, Token);
        Assert.NotEmpty(known.Diagnostics);
        Assert.NotEmpty(RecoilLanguage.InlayHints(known, new(0, source.Length), Token).Items);
    }

    [Fact]
    public void NeutralAnimationDoesNotExposeTheRecoilAnimationSchema()
    {
        const string source = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (MOVE (POSITION (1 2 3)))))))";
        var doc = Neutral("anim.zad", source);
        Assert.Empty(doc.Diagnostics);
        Assert.NotEmpty(doc.Symbols);
        Assert.Empty(RecoilLanguage.Complete(doc, source.Length - 4));
        Assert.Null(RecoilLanguage.Hover(doc, source.IndexOf("POSITION", StringComparison.Ordinal)));
        Assert.Null(RecoilLanguage.SignatureHelp(doc, source.IndexOf("1 2", StringComparison.Ordinal)));
        Assert.Empty(RecoilLanguage.InlayHints(doc, new(0, source.Length), Token).Items);
    }

    [Theory]
    [InlineData("build.gs")]
    [InlineData("world.gw")]
    public void NeutralGameGenDefersCommandValidationAndAllGameSpecificProviders(string path)
    {
        const string source = "UnrecognizedCommand 1\nNodeSetPosition 1 2 3\n";
        var doc = Neutral(path, source);
        Assert.False(doc.HasGameProfile);
        Assert.Empty(doc.Diagnostics);
        Assert.NotEmpty(doc.Tokens);
        Assert.NotEmpty(doc.Symbols);
        Assert.Empty(RecoilLanguage.Complete(doc, source.Length));
        Assert.Null(RecoilLanguage.Hover(doc, source.IndexOf("NodeSetPosition", StringComparison.Ordinal)));
        Assert.Null(RecoilLanguage.SignatureHelp(doc, source.IndexOf("1 2", StringComparison.Ordinal)));
        Assert.Empty(RecoilLanguage.InlayHints(doc, new(0, source.Length), Token).Items);
        Assert.Contains(RecoilLanguage.Analyze(path, source, Token).Diagnostics, d => d.Code == "GAMEZ_COMMAND");
    }

    [Fact]
    public void NeutralSiKeepsSharedAnimationFormatAnnotations()
    {
        const string source = "SI Animation Script\nFRAMES: 1\nOBJECTS: 1\nFrame: 1\nObject: root\nScaling: 1.0 1.0 1.0\nRotation: 0.0 0.0 0.0\nTranslation: 0.0 1.0 2.0\n";
        var doc = Neutral("clip.zan", source);
        Assert.False(doc.HasGameProfile);
        Assert.Null(doc.ResolvedGame);
        Assert.Empty(doc.Diagnostics);
        Assert.NotEmpty(RecoilLanguage.InlayHints(doc, new(0, source.Length), Token).Items);
        Assert.NotNull(RecoilLanguage.Hover(doc, source.IndexOf("Translation", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GameVariant.Recoil)]
    [InlineData(GameVariant.MechWarrior3)]
    public void SharedParsingErrorsUseGamezCodesForEveryProfile(GameVariant? game)
    {
        var syntax = RecoilLanguage.AnalyzeResolved("other.zrd", "(NAME (", game, Token);
        Assert.Contains(syntax.Diagnostics, d => d.Code == "GAMEZ_SYNTAX");
        var encoding = RecoilLanguage.AnalyzeResolved("other.zrd", "(NAME (\"\u0100\"))", game, Token);
        Assert.Contains(encoding.Diagnostics, d => d.Code == "GAMEZ_ENCODING");
        var binary = RecoilLanguage.AnalyzeResolved("other.zrd", "\0", game, Token);
        Assert.Contains(binary.Diagnostics, d => d.Code == "GAMEZ_BINARY");
    }

    [Fact]
    public void NeutralAnalysisStillEnforcesTheSharedSourceSizeLimit()
    {
        string source = new(' ', GameZ.Script.Core.Sources.SourceProject.MaximumSourceTextBytes + 1);
        var doc = Neutral("other.zrd", source);
        Assert.False(doc.HasGameProfile);
        Assert.Contains(doc.Diagnostics, d => d.Code == "GAMEZ_LIMIT" && d.Severity == "information");
        Assert.Empty(doc.Tokens);
    }

    [Theory]
    [InlineData(GameVariant.Recoil)]
    [InlineData(GameVariant.MechWarrior3)]
    public void SharedShapeChecksAndDiagnosticCapsUseGamezCodes(GameVariant game)
    {
        const string source = "(node_00 (0 (1 2.0) (-1 -1 -1)) node_01 (0 (1 2.0 3.0) (-1 -1 -1)))";
        var doc = RecoilLanguage.AnalyzeResolved("net_01.zrd", source, game, Token);
        Assert.Contains(doc.Diagnostics, d => d.Code == "GAMEZ_ARITY");
        Assert.Contains(doc.Diagnostics, d => d.Code == "GAMEZ_TYPE");
        string many = "(" + string.Concat(Enumerable.Repeat("node_bad () ", RecoilLanguage.MaximumDiagnostics + 2)) + ")";
        var capped = RecoilLanguage.AnalyzeResolved("net_01.zrd", many, game, Token);
        Assert.Contains(capped.Diagnostics, d => d.Code == "GAMEZ_LIMIT");
        Assert.Equal(RecoilLanguage.MaximumDiagnostics + 1, capped.Diagnostics.Count);
    }

    [Theory]
    [InlineData("(version (106))", true)]
    [InlineData("(version (105 mech3))", true)]
    [InlineData("(note (\"version (106)\"))", false)]
    [InlineData("# version (106)\n(version (105))", false)]
    [InlineData("SI Animation Script\nFRAMES: 1", false)]
    public void NullableDetectionDistinguishesPositiveEvidenceFromAbsence(string source, bool native)
    {
        Assert.Equal(native ? GameVariant.MechWarrior3 : (GameVariant?)null, RecoilLanguage.TryDetectDocumentGame(source, Token));
        Assert.Equal(native ? GameVariant.MechWarrior3 : GameVariant.Recoil, RecoilLanguage.DetectDocumentGame(source, Token));
    }
}
