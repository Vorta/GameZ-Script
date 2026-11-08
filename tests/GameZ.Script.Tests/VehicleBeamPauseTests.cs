using System.Text;
using GameZ.Script.Core;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Language;
using GameZ.Script.Core.Sources;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class VehicleBeamPauseTests
{
    private static ZrdNode Parse(string text) => ZrdText.Parse(text, TestContext.Current.CancellationToken);
    private static byte[] Write(ZrdNode tree) => ZrdWriter.Write(tree, TestContext.Current.CancellationToken);
    private static ZrdNode Decode(ReadOnlyMemory<byte> bytes) => ZrdDecoder.Read(bytes, TestContext.Current.CancellationToken);
    private static string Text(ZrdNode tree) => ZrdText.Write(tree, TestContext.Current.CancellationToken);
    private const string Nine = "wep_6.1 1 1 500 1.5 3.0 325.0 0 1";
    private static string Source(string tail = "", string vehicle = "bot", string weapon = "wep_6.1") =>
        "(" + vehicle + " (common_mode (weapons ((" + Nine.Replace("wep_6.1", weapon, StringComparison.Ordinal) + tail + ")))))";
    private static ZrdNode Row(ZrdNode tree)
    {
        while (tree.Children.Count == 1 && tree.Children[0].Kind == ZrdKind.Array) tree = tree.Children[0];
        return tree.Children[1].Children[1].Children[1].Children[0];
    }
    private static SourceBuilder.Built Build(string text, string file = "vehicle.zrd", bool binary = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "gamez-beam-" + Guid.NewGuid().ToString("N"));
        try
        {
            string relative = "data/common/zrdr/" + file;
            Directory.CreateDirectory(Path.Combine(root, "data/common/zrdr"));
            File.WriteAllBytes(Path.Combine(root, relative), binary ? Write(Parse(text)) : Encoding.Latin1.GetBytes(text));
            return SourceBuilder.Build(root, new("zrdr.zbd", "archive", [relative]), new(root), TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("vehicle.zrd")]
    [InlineData("vehicle_easy.zrd")]
    [InlineData("VEHICLE_HARD.ZRD")]
    public void OptionalPauseCompilesAsFloatAndRoundTrips(string file)
    {
        foreach (string tail in new[] { " 4.0", " 0.0", " -0.0", " f32:00000001" })
        {
            string text = Source(tail);
            var doc = RecoilLanguage.Analyze(file, text, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "error");
            var hints = RecoilLanguage.InlayHints(doc, new(0, text.Length), TestContext.Current.CancellationToken);
            Assert.Equal("beam pause (s):", hints.Items[^1].Label);
            Assert.Contains("after ending", hints.Items[^1].Documentation);
            Assert.Equal("beam pause (s)?", RecoilLanguage.SignatureHelp(doc, hints.Items[^1].Span.Start)!.Parameters[9].Label);
            var standalone = FormatRegistry.Default.OpenBytes(file, Encoding.Latin1.GetBytes(text), token: TestContext.Current.CancellationToken);
            var view = new ResourceMembers(standalone, TestContext.Current.CancellationToken);
            Assert.Equal(ZrdKind.Float, Row(Decode(Assert.Single(view.Members).Data)).Children[9].Kind);
            foreach (bool binary in new[] { false, true })
            {
                var built = Build(text, file, binary);
                Assert.Empty(built.Warnings);
                var member = Assert.Single(ArchiveSources.Read(built.Bytes));
                var decoded = Decode(member.Payload);
                var row = Row(decoded);
                Assert.Equal(10, row.Children.Count);
                Assert.Equal(ZrdKind.Float, row.Children[9].Kind);
                Assert.Equal(2, (int)row.Children[9].Kind);
                Assert.Equal(Row(Parse(text)).Children[9].Bits, row.Children[9].Bits);
                byte[] again = Write(Parse(Text(decoded)));
                Assert.Equal(member.Payload.ToArray(), again);
            }
        }
    }

    [Fact]
    public void NineOperandBytesStayIdenticalAndNoDefaultIsInserted()
    {
        string text = Source();
        var tree = Parse(text);
        // Synthetic pre-extension row: array count10, name, three ints, three floats, two ints.
        byte[] golden = Convert.FromHexString("040000000A00000003000000070000007765705F362E310100000001000000010000000100000001000000F4010000020000000000C03F0200000000004040020000000080A24301000000000000000100000001000000");
        Assert.Equal(golden, Write(Row(tree)));
        var expected = ArchiveSources.Write([new ArchiveSources.Entry("vehicle.zrd", "data\\common\\zrdr\\vehicle.zrd", Write(tree))]);
        Assert.Equal(expected, Build(text).Bytes);
        Assert.Equal(expected, Build(text, binary: true).Bytes);
        var decoded = Decode(Assert.Single(ArchiveSources.Read(Build(text).Bytes)).Payload);
        Assert.Equal(9, Row(decoded).Children.Count);
    }

    [Theory]
    [InlineData(" 4", "GAMEZ_TYPE", "4.0")]
    [InlineData(" -1.0", "RECOIL_RANGE", "nonnegative")]
    [InlineData(" f32:7F800000", "GAMEZ_FINITE", "finite")]
    [InlineData(" f32:7FC00001", "GAMEZ_FINITE", "finite")]
    [InlineData(" invalid", "GAMEZ_TYPE", "FLOAT")]
    [InlineData(" (4.0)", "GAMEZ_TYPE", "FLOAT")]
    [InlineData(" 4.0 5.0", "GAMEZ_ARITY", "9 or 10")]
    public void InvalidPauseOrExcessOperandsFailEditorAndBothCompilationPaths(string tail, string code, string message)
    {
        string text = Source(tail);
        var doc = RecoilLanguage.Analyze("vehicle.zrd", text, TestContext.Current.CancellationToken);
        var error = Assert.Single(doc.Diagnostics, d => d.Code == code && d.Severity == "error");
        Assert.Contains(message, error.Message);
        if (code != "GAMEZ_ARITY") Assert.Equal(text.IndexOf(tail, StringComparison.Ordinal) + 1, error.Span.Start);
        foreach (bool binary in new[] { false, true })
        {
            var ex = Assert.Throws<InvalidDataException>(() => Build(text, binary: binary));
            Assert.Contains("data/common/zrdr/vehicle.zrd", ex.Message);
            Assert.Contains("row 1", ex.Message);
            Assert.Contains(message, ex.Message);
        }
        var document = FormatRegistry.Default.OpenBytes("vehicle.zrd", Encoding.Latin1.GetBytes(text), token: TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => new ResourceMembers(document, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FewerThanNineStillFailsAndOtherResourceRowsRemainGeneric()
    {
        string text = Source().Replace("325.0 0 1", "325.0 0", StringComparison.Ordinal);
        Assert.Contains(RecoilLanguage.Analyze("vehicle.zrd", text, TestContext.Current.CancellationToken).Diagnostics, d => d.Code == "GAMEZ_ARITY");
        Assert.Throws<InvalidDataException>(() => Build(text));
        Assert.NotEmpty(Build(Source(" 4 5"), "custom.zrd").Bytes);
    }

    [Fact]
    public void BinaryInspectionPreservesEvenAnInvalidStoredPauseForDiagnosis()
    {
        var bytes = Write(Parse(Source(" 4")));
        var document = FormatRegistry.Default.OpenBytes("vehicle.zrd", bytes, token: TestContext.Current.CancellationToken);
        var view = new ResourceMembers(document, TestContext.Current.CancellationToken);
        var tree = view.Tree(Assert.Single(view.Members), TestContext.Current.CancellationToken);
        Assert.Equal(ZrdKind.Int, Row(tree).Children[9].Kind);
        Assert.Equal(bytes, Write(Parse(Text(tree))));
    }

    [Fact]
    public async Task PublicReadOnlyBuildReportsWarningsAndRejectsInvalidPause()
    {
        string root = Path.Combine(Path.GetTempPath(), "gamez-beam-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "gamegen"));
            Directory.CreateDirectory(Path.Combine(root, "data/common/zrdr"));
            string file = Path.Combine(root, "data/common/zrdr/vehicle.zrd");
            string source = Source(" 4.0", "bft");
            File.WriteAllText(file, source, Encoding.Latin1);
            var valid = await SourceBuilder.CheckAsync(root, ["zrdr.zbd"], token: TestContext.Current.CancellationToken);
            Assert.Equal(0, valid.Failed);
            Assert.Single(Assert.Single(valid.Outputs).Warnings, w => w.Contains("beam pause is ignored"));
            Assert.Equal(source, File.ReadAllText(file, Encoding.Latin1));
            File.WriteAllText(file, Source(" 4"), Encoding.Latin1);
            var invalid = await SourceBuilder.CheckAsync(root, ["zrdr.zbd"], token: TestContext.Current.CancellationToken);
            Assert.Equal(1, invalid.Failed);
            Assert.Contains("FLOAT", Assert.Single(invalid.Outputs).Error!);
            Assert.False(File.Exists(Path.Combine(root, "zrdr.zbd")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("((bot (common_mode (weapons ((ROW))))) (other (common_mode ())))")]
    [InlineData("(bot ((common_mode (weapons ((ROW))))))")]
    [InlineData("(bot (common_mode ((weapons ((ROW))))))")]
    [InlineData("(junk bot (junk common_mode (junk weapons ((ROW)))) )")]
    [InlineData("(group (bot (common_mode (weapons ((ROW))))))")]
    [InlineData("(bot (group (common_mode (weapons ((ROW))))))")]
    [InlineData("(bot (common_mode (group (weapons ((ROW))))))")]
    [InlineData("(group (bot (group (common_mode (group (weapons ((ROW))))))))")]
    public void GroupingWrappersAndUnpairedStringsCannotBypassCompilationRules(string template)
    {
        string text = template.Replace("ROW", Nine + " 4", StringComparison.Ordinal);
        Assert.Contains(RecoilLanguage.Analyze("vehicle.zrd", text, TestContext.Current.CancellationToken).Diagnostics, d => d.Code == "GAMEZ_TYPE");
        foreach (bool binary in new[] { false, true })
            Assert.Contains("FLOAT", Assert.Throws<InvalidDataException>(() => Build(text, binary: binary)).Message);
        var document = FormatRegistry.Default.OpenBytes("vehicle.zrd", Encoding.Latin1.GetBytes(text), token: TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => new ResourceMembers(document, TestContext.Current.CancellationToken));
        string valid = template.Replace("ROW", Nine + " 4.0", StringComparison.Ordinal);
        var doc = RecoilLanguage.Analyze("vehicle.zrd", valid, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "error");
        Assert.Equal(10, RecoilLanguage.InlayHints(doc, new(0, valid.Length), TestContext.Current.CancellationToken).Items.Count);
        Assert.Equal("beam pause (s)?", RecoilLanguage.SignatureHelp(doc, valid.IndexOf("4.0", StringComparison.Ordinal))!.Parameters[9].Label);
        foreach (bool binary in new[] { false, true }) Assert.NotEmpty(Build(valid, binary: binary).Bytes);
        string nine = template.Replace("ROW", Nine, StringComparison.Ordinal);
        var expected = ArchiveSources.Write([new ArchiveSources.Entry("vehicle.zrd", "data\\common\\zrdr\\vehicle.zrd", Write(Parse(nine)))]);
        foreach (bool binary in new[] { false, true }) Assert.Equal(expected, Build(nine, binary: binary).Bytes);
    }

    [Fact]
    public void PlayerWarningSurvivesNamedGroupingWithoutDuplicates()
    {
        string text = "(group (bft (group (common_mode (group (weapons ((" + Nine + " 0.0))))))))";
        var doc = RecoilLanguage.Analyze("vehicle.zrd", text, TestContext.Current.CancellationToken);
        Assert.Single(doc.Diagnostics, d => d.Code == "RECOIL_IGNORED" && d.Message.Contains("bft"));
        var built = Build(text);
        Assert.Single(built.Warnings, w => w.Contains("bft"));
    }

    [Theory]
    [InlineData("bot", "wep_6.0", false)]
    [InlineData("bot", "wep_6.1", false)]
    [InlineData("bot", "wep_7.1", false)]
    [InlineData("bft", "wep_6.1", true)]
    [InlineData("bot", "wep_2.0", true)]
    [InlineData("bft", "wep_2.0", true)]
    public void IgnoredPauseWarnsWithoutBlockingOrRemovingIt(string vehicle, string weapon, bool warning)
    {
        string text = Source(" 0.0", vehicle, weapon);
        var doc = RecoilLanguage.Analyze("vehicle_hard.zrd", text, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "error");
        Assert.Equal(warning ? 1 : 0, doc.Diagnostics.Count(d => d.Code == "RECOIL_IGNORED"));
        var built = Build(text);
        Assert.Equal(warning ? 1 : 0, built.Warnings.Count);
        Assert.Equal(10, Row(Decode(Assert.Single(ArchiveSources.Read(built.Bytes)).Payload)).Children.Count);
    }
}
