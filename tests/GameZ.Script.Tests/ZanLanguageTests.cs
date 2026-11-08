using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using GameZ.Script.Core.Sources;
using System.Text;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class ZanLanguageTests
{
    private const string Si = """
        SI Animation Script
        FRAMES: 2
        OBJECTS: 1
        Frame: 1
        Object: probe
        Scaling: 1.0 1.0 1.0
        Rotation: 0.0 0.0 0.0
        Translation: 2.0 3.0 4.0
        Frame: 7
        Object: probe
        Scaling: 1.0 1.0 1.0
        Rotation: 0.0 0.0 0.0
        Translation: 4.0 5.0 6.0
        """;
    private const string Legacy = "OBJECT probe\nFRAME 0 POSITION 1.0 2.0 3.0 ROTATION 1.0 0.0 0.0 0.0\nFRAME 7";
    private const string Motion = "MOTION ( LOOP_SECONDS ( 0.5 ) FRAME_COUNT ( 1 ) PART ( NAME ( hip ) SAMPLES ( ( 1.0 2.0 3.0 1.0 0.0 0.0 0.0 ) ( 4.0 5.0 6.0 1.0 0.0 0.0 0.0 ) ) ) )";
    private static LanguageDocument Doc(string text, GameVariant? game = null) =>
        ZanLanguage.Analyze("probe.zan", text, TestContext.Current.CancellationToken, game);
    private static IReadOnlyList<LanguageInlayHint> Hints(LanguageDocument doc) =>
        RecoilLanguage.InlayHints(doc, new(0, doc.Text.Length), TestContext.Current.CancellationToken).Items;

    [Theory]
    [InlineData(GameVariant.Recoil)]
    [InlineData(GameVariant.MechWarrior3)]
    public void SiUsesSceneFormatIndependentlyOfGame(GameVariant game)
    {
        var doc = Doc(Si, game);
        Assert.Equal(game, doc.Game);
        Assert.Equal("zan-si", doc.Family);
        Assert.True(doc.Complete);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(24, Hints(doc).Count);
        var hint = Assert.Single(Hints(doc).Where(h => h.Label == "rotation x (rad):").Take(1));
        Assert.Equal("0.0", doc.Text[hint.Span.Start..hint.Span.End]);
        Assert.Contains("Euler", RecoilLanguage.Hover(doc, hint.Span.Start)!.Markdown);
        Assert.Equal("rotation x (rad)", RecoilLanguage.SignatureHelp(doc, hint.Span.Start)!.Parameters[0].Label);
        Assert.Single(doc.Symbols);
        Assert.Equal("probe", doc.Symbols[0].Name);
        Assert.Equal(2, doc.Symbols[0].Children.Count);
    }

    [Fact]
    public void SparseAndReversedSiLabelsAreNotComparedToHeaderCount()
    {
        string text = Si.Replace("Frame: 1", "Frame: 90", StringComparison.Ordinal)
            .Replace("Frame: 7", "Frame: 2", StringComparison.Ordinal);
        Assert.Empty(Doc(text).Diagnostics);
        Assert.Empty(Doc(text.Replace("Frame: 2", "Frame: 90", StringComparison.Ordinal)).Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SiColonBoundariesDoNotRequireWhitespace(bool mixedSpacing)
    {
        string text = mixedSpacing
            ? Si.Replace("Frame: ", "Frame:", StringComparison.Ordinal)
                .Replace("Object: ", "Object:\t", StringComparison.Ordinal)
                .Replace("Scaling: ", "Scaling:", StringComparison.Ordinal)
                .Replace("Rotation: ", "Rotation:\t ", StringComparison.Ordinal)
                .Replace("Translation: ", "Translation:", StringComparison.Ordinal)
            : Si.Replace(": ", ":", StringComparison.Ordinal);
        var doc = Doc(text);
        Assert.True(doc.Complete);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(24, Hints(doc).Count);
        Assert.Equal("probe", Assert.Single(doc.Symbols).Name);
        var index = ZanLanguage.Index("probe.zan", text, TestContext.Current.CancellationToken);
        Assert.Equal("probe", Assert.Single(index.Tracks).Name);
        Assert.Equal(text.IndexOf("probe", StringComparison.Ordinal), index.Tracks[0].Span.Start);
        var hint = Assert.Single(Hints(doc).Where(h => h.Label == "translation y:").Take(1));
        Assert.Equal("3.0", text[hint.Span.Start..hint.Span.End]);
        Assert.Equal("translation y", RecoilLanguage.SignatureHelp(doc, hint.Span.Start)!.Parameters[1].Label);
        var firstOperand = Assert.Single(Hints(doc).Where(h => h.Label == "translation x:").Take(1));
        Assert.Equal("translation x", RecoilLanguage.SignatureHelp(doc, firstOperand.Span.Start)!.Parameters[0].Label);
        Assert.Empty(ZanLanguage.Complete(doc, firstOperand.Span.Start));
    }

    [Theory]
    [InlineData("probe#1")]
    [InlineData("#probe")]
    [InlineData("probe#part")]
    public void SiHashInObjectNameIsNotAnInlineComment(string name)
    {
        string text = Si.Replace("probe", name, StringComparison.Ordinal);
        var doc = Doc(text);
        Assert.True(doc.Complete);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(24, Hints(doc).Count);
        var index = ZanLanguage.Index("probe.zan", text, TestContext.Current.CancellationToken);
        var track = Assert.Single(index.Tracks);
        Assert.Equal(name, track.Name);
        Assert.Equal(name, text[track.Span.Start..track.Span.End]);
        Assert.Equal(name, Assert.Single(doc.Symbols).Name);
        int at = text.IndexOf(name, StringComparison.Ordinal) + name.Length - 1;
        Assert.False(ZanLanguage.InComment(doc, at));
        Assert.Contains("Object track", RecoilLanguage.Hover(doc, at)!.Markdown);
    }

    [Fact]
    public void LegacyUsesZeroBasedFramesAndQuaternionOrder()
    {
        var doc = Doc(Legacy);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal("zan-legacy", doc.Family);
        Assert.Equal(new[] { "object track:", "frame (0-based):", "translation x:", "translation y:", "translation z:",
            "quaternion w:", "quaternion x:", "quaternion y:", "quaternion z:", "frame (0-based):" }, Hints(doc).Select(h => h.Label));
        var q = Hints(doc)[5];
        Assert.Equal("1.0", doc.Text[q.Span.Start..q.Span.End]);
        Assert.Contains("W X Y Z", RecoilLanguage.Hover(doc, q.Span.Start)!.Markdown);
        Assert.Empty(Doc(Legacy.Replace("FRAME 7", "FRAME 0", StringComparison.Ordinal)).Diagnostics);
    }

    [Fact]
    public void MotionLabelsEverySampleAndItsClosingRole()
    {
        var doc = Doc(Motion);
        Assert.Equal(GameVariant.MechWarrior3, doc.Game);
        Assert.Equal("zan-motion", doc.Family);
        Assert.True(doc.Complete);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(17, Hints(doc).Count);
        Assert.Equal(new[] { "translation x:", "translation y:", "translation z:", "quaternion w:", "quaternion x:", "quaternion y:", "quaternion z:" },
            Hints(doc).Skip(3).Take(7).Select(h => h.Label));
        Assert.Contains("closing sample", Hints(doc)[10].Documentation);
        Assert.Equal("quaternion z", RecoilLanguage.SignatureHelp(doc, Hints(doc)[^1].Span.Start)!.Parameters[6].Label);
        Assert.Equal("hip", Assert.Single(doc.Symbols).Name);
        Assert.Equal(Motion, doc.Text);
        var grouped = Doc("(\n ( " + Motion + " )\n)");
        Assert.Equal("zan-motion", grouped.Family);
        Assert.Empty(grouped.Diagnostics);
    }

    [Theory]
    [InlineData("(# author comment\n", ")")]
    [InlineData("( # author comment\r\n(# nested comment\r\n", "))")]
    [InlineData("# leading comment\n(\n# inner comment\n", ")")]
    public void GroupedMotionCommentsPreserveRecognitionAndNavigation(string prefix, string suffix)
    {
        string text = prefix + Motion + suffix;
        var doc = Doc(text);
        Assert.Equal("zan-motion", doc.Family);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(17, Hints(doc).Count);
        Assert.Equal("hip", Assert.Single(ZanLanguage.Index("probe.zan", text, TestContext.Current.CancellationToken).Tracks).Name);
        Assert.NotEmpty(Mw3LibrarySources.BuildMotion(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ImplicitLegacyTrackIndexesItsFirstFrameWithoutInventingAName()
    {
        const string text = "# an unsectioned track\nFRAME 0 POSITION 1 2 3\nFRAME 5";
        var index = ZanLanguage.Index("probe.zan", text, TestContext.Current.CancellationToken);
        var track = Assert.Single(index.Tracks);
        Assert.Equal("", track.Name);
        Assert.Equal("object", track.Kind);
        Assert.Equal(text.IndexOf("FRAME", StringComparison.Ordinal), track.Span.Start);
        Assert.Empty(Doc(text).Diagnostics);
    }

    [Fact]
    public void DuplicateMotionRootsGetAnActionableStructureError()
    {
        string text = Motion + "\n" + Motion;
        var doc = Doc(text);
        var error = Assert.Single(doc.Diagnostics, d => d.Code == "ZAN_ARITY");
        Assert.Contains("exactly one MOTION", error.Message);
        Assert.Equal(text.LastIndexOf("MOTION", StringComparison.Ordinal), error.Span.Start);
        Assert.NotEmpty(Hints(doc));
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("duplicate header")]
    [InlineData("scalar name")]
    [InlineData("sample junk")]
    [InlineData("unsupported part field")]
    public void EditorAndCompilerShareSampledMotionStructureChecks(string change)
    {
        string text = change switch
        {
            "prefix" => "junk " + Motion,
            "duplicate header" => Motion.Replace("LOOP_SECONDS ( 0.5 )", "LOOP_SECONDS ( 0.5 ) LOOP_SECONDS ( 1.0 )", StringComparison.Ordinal),
            "scalar name" => Motion.Replace("NAME ( hip )", "NAME hip", StringComparison.Ordinal),
            "sample junk" => Motion.Replace("SAMPLES (", "SAMPLES ( junk", StringComparison.Ordinal),
            _ => Motion.Replace("PART (", "PART ( EXTRA ( 1 )", StringComparison.Ordinal)
        };
        // Both entry points reject every structural mutation, including stray root text.
        var doc = ZanLanguage.Analyze("data/common/mechs/motions/probe.zan", text, TestContext.Current.CancellationToken, GameVariant.MechWarrior3);
        Assert.Contains(doc.Diagnostics, d => d.Severity == "error");
        Assert.Throws<InvalidDataException>(() => Mw3LibrarySources.BuildMotion(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("LOOP_SECONDS ( 0.5 )", "LOOP_SECONDS ( 0.0 )", "ZAN_RANGE")]
    [InlineData("FRAME_COUNT ( 1 )", "FRAME_COUNT ( 2 )", "ZAN_ARITY")]
    [InlineData("1.0 2.0 3.0 1.0 0.0 0.0 0.0", "1.0 2.0 3.0 0.0 0.0 0.0 0.0", "ZAN_QUATERNION")]
    [InlineData("1.0 2.0 3.0 1.0 0.0 0.0 0.0", "1.0 2.0 3.0 1.0 0.0 0.0", "ZAN_ARITY")]
    [InlineData("1.0 2.0 3.0 1.0 0.0 0.0 0.0", "1.0 invalid 3.0 1.0 0.0 0.0 0.0", "ZAN_NUMBER")]
    public void MotionValidationUsesTypedCountsAndTransforms(string from, string to, string code)
    {
        var doc = Doc(Motion.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, d => d.Code == code && d.Severity == "error");
    }

    [Fact]
    public void InvalidNumberIsMarkedPreciselyAndHintsSurviveIncompleteEdits()
    {
        string text = Si.Replace("Translation: 2.0", "Translation: invalid", StringComparison.Ordinal);
        var doc = Doc(text);
        var invalid = Assert.Single(doc.Diagnostics, d => d.Code == "ZAN_NUMBER");
        Assert.Equal("invalid", text[invalid.Span.Start..invalid.Span.End]);
        var unfinished = Doc("MOTION ( LOOP_SECONDS ( 1.0 ) FRAME_COUNT ( 1 ) PART ( NAME ( hip ) SAMPLES ( ( 1.0 2.0 ");
        Assert.Contains(unfinished.Diagnostics, d => d.Code == "ZAN_SYNTAX");
        Assert.Contains(Hints(unfinished), h => h.Label == "translation y:");
    }

    [Fact]
    public void IndexRetainsClosedTrackLocationsAndCrLfLines()
    {
        string text = Si.Replace("\n", "\r\n", StringComparison.Ordinal);
        var index = ZanLanguage.Index("probe.zan", text, TestContext.Current.CancellationToken);
        var track = Assert.Single(index.Tracks);
        Assert.Equal("probe", track.Name);
        Assert.Equal(text.IndexOf("probe", StringComparison.Ordinal), track.Span.Start);
        Assert.Equal(text.IndexOf("Frame: 1", StringComparison.Ordinal), index.LineStarts[3]);
        var parts = ZanLanguage.Index("motion.zan", Motion, TestContext.Current.CancellationToken);
        Assert.Equal("hip", Assert.Single(parts.Tracks).Name);
        Assert.Equal("part", parts.Tracks[0].Kind);
    }

    [Fact]
    public void CommentsCompletionAndRangedHintsRemainReadOnly()
    {
        var doc = Doc(Legacy + "\n# FRAME 4 POSITION 7 8 9");
        Assert.Empty(ZanLanguage.Complete(doc, doc.Text.Length));
        Assert.Null(RecoilLanguage.SignatureHelp(doc, doc.Text.Length));
        Assert.Contains(ZanLanguage.Complete(Doc(""), 0), c => c.Label == "SI Animation Script");
        var motion = Doc("MOTION ( LOOP_SECONDS ( 0.5 ) FRAME_COUNT ( 1 ) PART ( NAME ( hip ) SAMPLES ( ");
        Assert.Single(ZanLanguage.Complete(motion, motion.Text.Length), c => c.Label == "sample" && c.IsSnippet);
        var hint = Hints(doc)[3];
        var range = RecoilLanguage.InlayHints(doc, new(hint.Span.Start, hint.Span.Length), TestContext.Current.CancellationToken);
        Assert.Single(range.Items);
        Assert.Equal(hint, range.Items[0]);
    }

    [Fact]
    public void IndexAndAnalysisObserveCancellationAndEncoding()
    {
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ZanLanguage.Analyze("test.zan", Si, canceled.Token));
        Assert.Throws<OperationCanceledException>(() => ZanLanguage.Index("test.zan", Si, canceled.Token));
        Assert.Contains(Doc(Legacy + "\n# \u0100").Diagnostics, d => d.Code == "GAMEZ_ENCODING");
    }

    [Fact]
    public void LargeMotionHintsFilterViewportBeforeApplyingLimit()
    {
        const string row = "(0 0 0 1 0 0 0)";
        string text = "MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1000) PART (NAME (hip) SAMPLES ("
            + string.Join(" ", Enumerable.Repeat(row, 1001)) + ")))";
        var doc = Doc(text);
        Assert.Empty(doc.Diagnostics);
        var all = RecoilLanguage.InlayHints(doc, new(0, text.Length), TestContext.Current.CancellationToken);
        Assert.True(all.Truncated);
        Assert.Equal(RecoilLanguage.MaximumInlayHints, all.Items.Count);
        int late = text.LastIndexOf(row, StringComparison.Ordinal);
        var viewport = RecoilLanguage.InlayHints(doc, new(late, row.Length), TestContext.Current.CancellationToken);
        Assert.False(viewport.Truncated);
        Assert.Equal(7, viewport.Items.Count);
        Assert.Contains("closing sample", viewport.Items[0].Documentation);
    }
}
