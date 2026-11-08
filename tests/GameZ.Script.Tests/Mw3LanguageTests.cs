using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class Mw3LanguageTests
{
    private static LanguageDocument Native(string file, string text) =>
        RecoilLanguage.Analyze(file, text, TestContext.Current.CancellationToken, GameVariant.MechWarrior3);
    private static IReadOnlyList<LanguageInlayHint> Hints(LanguageDocument doc) =>
        RecoilLanguage.InlayHints(doc, new(0, doc.Text.Length), TestContext.Current.CancellationToken).Items;

    [Theory]
    [InlineData("# version (106)\n(version (105))", GameVariant.Recoil)]
    [InlineData("(note (\"version (106) PUFFER_STATE (\"))", GameVariant.Recoil)]
    [InlineData("(version (\"106\"))", GameVariant.Recoil)]
    [InlineData("(version (106))", GameVariant.MechWarrior3)]
    [InlineData("(version (105 mech3))", GameVariant.MechWarrior3)]
    [InlineData("(version (108)", GameVariant.MechWarrior3)]
    [InlineData("SI Animation Script\nFRAMES: 2\n", GameVariant.Recoil)]
    public void DocumentDetectionUsesStructureAndKeepsSiGameNeutral(string text, GameVariant expected)
    {
        Assert.Equal(expected, RecoilLanguage.DetectDocumentGame(text, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NativeWeaponUsesEightPositionsAndNamedHardpointsWithoutBeamPause()
    {
        const string text = "(bot (common_mode (weapons ((SRM2 1 1 5000 2.0 25.0 1000.0 0 firepoint (firepoint01 firepoint02))))))";
        var doc = Native("vehicle.zrd", text);
        Assert.Empty(doc.Diagnostics);
        var hints = Hints(doc);
        Assert.Contains(hints, h => h.Label == "starting ammo:");
        Assert.Contains(hints, h => h.Label == "delay (s):");
        Assert.DoesNotContain(hints, h => h.Label.Contains("beam pause", StringComparison.Ordinal));
        Assert.Contains(RecoilLanguage.Analyze("vehicle.zrd", text, TestContext.Current.CancellationToken, GameVariant.Recoil).Diagnostics,
            d => d.Code == "GAMEZ_TYPE");
    }

    [Fact]
    public void NativeNetworksHaveVariableLinksAndNonSpatialConditions()
    {
        const string text = "(version (106) node_00 (12 (1.0 2.0 3.0) (100) speed (1 4.0)) node_100 ((0) valve (1 trigger 0)))";
        var doc = Native("net_01.zrd", text);
        Assert.Empty(doc.Diagnostics);
        var hints = Hints(doc);
        Assert.Contains(hints, h => h.Label == "x:");
        Assert.Contains(hints, h => h.Label == "link 1 (node_100):");
        Assert.Contains(hints, h => h.Label == "required value:");
        Assert.Contains(RecoilLanguage.Complete(Native("net_01.zrd", "(version (106) node_00 (12 (1.0 2.0 3.0) () "), 58), c => c.Label == "speed");
    }

    [Fact]
    public void NativeDirectFloatsRemainStrictWhileGetterNumbersConvert()
    {
        var net = Native("net_01.zrd", "(version (106) node_00 (12 (1 2.0 3.0) ()))");
        Assert.Contains(net.Diagnostics, d => d.Code == "GAMEZ_TYPE" && d.Message.Contains("float", StringComparison.Ordinal));
        const string weapon = "(VERSION (2) BALLISTICS (beam (AMMO_LIMIT (10.0) BEAM (TIME_TO_MAX_RANGE (1) PULSE (0.2 0 1.0)))))";
        Assert.Empty(Native("weapons.zrd", weapon).Diagnostics);
        Assert.Contains(Native("weapons.zrd", weapon.Replace("0.2 0 1.0", "0.2 0.0 1.0", StringComparison.Ordinal)).Diagnostics, d => d.Code == "GAMEZ_TYPE");
    }

    [Fact]
    public void NativeAivTriggersExplainAllSeventeenPositions()
    {
        const string text = "(pilot (1 (1.0 2.0 3.0) 90.0 2 vlist ((1 1 0.25 1 1 0.1 1 0.8 1 area 1 10.0 1 60.0 0 trigger 0))))";
        var doc = Native("aiv.zrd", text);
        Assert.Empty(doc.Diagnostics);
        Assert.Contains(Hints(doc), h => h.Label == "AI network:");
        Assert.Contains(Hints(doc), h => h.Label == "health at most:");
        Assert.Contains(Hints(doc), h => h.Label == "heat at least:");
        Assert.Contains(Hints(doc), h => h.Label == "when outside:");
    }

    [Fact]
    public void NativeLabelsSupportBothTaggedAndCompactForms()
    {
        const string text = "(SHARED_IMAGE_PATH (images) menu (BUTTONS (ok (LABEL ((MSG_OK offset (10 20) font (main_red)) (MSG_NEXT 3 4 2)) POSITION (1 2)))))";
        var doc = Native("dialog.zrd", text);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(3, Hints(doc).Count(h => h.Label == "x (px):"));
        Assert.Contains(Hints(doc), h => h.Label == "font index:");
        Assert.Contains(Hints(doc), h => h.Label == "font style:");
    }

    [Fact]
    public void NativePufferLightAndConditionsHaveTheirOwnMetadata()
    {
        const string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (\"root#\") SEQUENCE_DEFINITION (NAME (start) IF (HW_RENDER ()) PUFFER_STATE (NAME (smoke) WORLD_VELOCITY (1 2 3) PARTICLES_PER_EMISSION (2)) LIGHT_STATE (NAME (sun) STATIC (ON) DIFFUSE (0.5)) ENDIF ())))))";
        var doc = Native("anim.zad", text);
        Assert.Empty(doc.Diagnostics);
        Assert.Contains(Hints(doc), h => h.Label == "particle count:");
        Assert.Contains("trailing #", RecoilLanguage.Hover(doc, text.IndexOf("root#", StringComparison.Ordinal))!.Markdown);
        var unfinished = Native("anim.zad", "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (");
        Assert.Contains(RecoilLanguage.Complete(unfinished, unfinished.Text.Length), c => c.Label == "PUFFER_STATE");
    }

    [Fact]
    public void WaypointLinksSelectOtherWaynetsRatherThanNodesInTheSameFile()
    {
        var doc = Native("waynet_2.zrd", "(version (107) node_00 (12 (0.0 0.0 0.0) (1)))");
        Assert.Empty(doc.Diagnostics);
        Assert.Contains(Hints(doc), h => h.Label == "waypoint network 1:");
    }

    [Fact]
    public void NativeNodeSuggestionsContinueAfterTwoHundredOccupiedNodesAndKeepExistingBodies()
    {
        string text = "(version (106) " + string.Join(" ", Enumerable.Range(0, 200).Select(i => $"node_{i:00} (12 (0.0 0.0 0.0) ())")) + " node_";
        var document = Native("net_01.zrd", text);
        Assert.Contains(RecoilLanguage.Complete(document, text.Length), c => c.Label == "node_200");
        string high = "(version (106) node_21474836";
        Assert.Contains(RecoilLanguage.Complete(Native("net_01.zrd", high), high.Length), c => c.Label == "node_214748360");
        const string rename = "(version (106) node_00 (12 (0.0 0.0 0.0) ()))";
        var edits = RecoilLanguage.Complete(Native("net_01.zrd", rename), rename.IndexOf("node_00", StringComparison.Ordinal) + 7);
        Assert.Equal("node_00", Assert.Single(edits, c => c.Label == "node_00").InsertText);
        const string link = "(version (106) node_00 (12 (0.0 0.0 0.0) (1)) node_100 (12 (0.0 0.0 0.0) ()))";
        Assert.Contains(RecoilLanguage.Complete(Native("net_01.zrd", link), link.IndexOf("(1)", StringComparison.Ordinal) + 2), c => c.Label == "100");
    }

    [Fact]
    public void NativeSmallResourcesHaveContextualArguments()
    {
        var mines = Native("landmines.zrd", "(weapon (testMine) radius (5.0) missionA ((1.0 2.0 3.0)))");
        Assert.Empty(mines.Diagnostics);
        Assert.Contains(Hints(mines), h => h.Label == "trigger radius (m):");
        Assert.Contains(Hints(mines), h => h.Label == "z:");
        var detail = Native("detail.zrd", "(Lighting (((CPU_MHZ \">=\" 200) TRUE) (DEFAULT FALSE)))");
        Assert.Empty(detail.Diagnostics);
        Assert.Contains(Hints(detail), h => h.Label == "threshold:");
        Assert.Contains(Hints(detail), h => h.Label == "selected value:");
        var scenario = Native("mpinfo.zrd", "(world_params (fog (range (20 100))) briefing (SOUND (intro) MARKERS ((caption (at (10 20) font (main))))))");
        Assert.Empty(scenario.Diagnostics);
        Assert.Contains(Hints(scenario), h => h.Label == "maximum:");
        Assert.Contains(Hints(scenario), h => h.Label == "x (px):");
        Assert.Contains(Hints(Native("soils.zrd", "(earth metal)")), h => h.Label == "material:");
    }

    [Fact]
    public void NativeTargetThresholdsRepeatAndOffsetsHaveAxes()
    {
        var doc = Native("targets.zrd", "(objectA (2 target (body (1 2 3)) health_below (first 1 0.5 second 2 0.25)))");
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(2, Hints(doc).Count(h => h.Label == "health at most:"));
        Assert.Contains(Hints(doc), h => h.Label == "z:");
        Assert.Contains(Native("targets.zrd", "(objectA (2 health_below (first 1)))").Diagnostics, d => d.Code == "GAMEZ_ARITY");
    }

    [Theory]
    [InlineData("v", "version")]
    [InlineData("NODE_200", "node_200")]
    [InlineData("node_999999999999999999999999", null)]
    [InlineData("health", null)]
    public void NativeNodeSuggestionsBoundEveryPrefix(string prefix, string? expected)
    {
        string text = "(version (106) " + prefix;
        var completions = RecoilLanguage.Complete(Native("net_01.zrd", text), text.Length);
        if (expected == null) Assert.Empty(completions);
        else Assert.Contains(completions, c => c.Label == expected);
    }
}
