using GameZ.Script.Core.Language;
using GameZ.Script.Core.Sources;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class LanguageArgumentTests
{
    private static LanguageDocument Doc(string file, string text) => RecoilLanguage.Analyze(file, text);
    private static IReadOnlyList<LanguageInlayHint> Hints(LanguageDocument doc) => RecoilLanguage.InlayHints(doc, new(0, doc.Text.Length)).Items;

    [Fact]
    public void WeaponRowsLabelEveryOperandWithoutChangingSource()
    {
        const string text = "(bft (common_mode (weapons ((wep_2.0 1 0 3 1.2 5.0 5.0 1 1)))))";
        var doc = Doc("vehicle.zrd", text);
        Assert.Equal(new[] { "weapon:", "mission threshold:", "dual mount:", "starting ammo:", "repeat delay (s):",
            "AI minimum range:", "AI maximum range:", "recoil flag:", "initial hardpoint state:" }, Hints(doc).Select(h => h.Label));
        Assert.Equal(new[] { "wep_2.0", "1", "0", "3", "1.2", "5.0", "5.0", "1", "1" }, Hints(doc).Select(h => text[h.Span.Start..h.Span.End]));
        Assert.Contains("integer", Hints(doc)[3].Documentation);
        Assert.Equal(text, doc.Text);
    }

    [Fact]
    public void NetworksDescribeUnusedValueAndLiveLinks()
    {
        const string text = "(node_00 (77 (1.0 2.0 3.0) (1 -1 -2)) node_01 (4 (0.0 0.0 0.0) (-1 -1 -1)))";
        var doc = Doc("net_01.zrd", text);
        Assert.Equal(14, Hints(doc).Count);
        Assert.Equal("unused:", Hints(doc)[0].Label);
        Assert.Contains("node_01", Hints(doc)[4].Label);
        Assert.Contains("no link", Hints(doc)[6].Label);
        int at = text.IndexOf("(1 -1", StringComparison.Ordinal) + 2;
        Assert.Contains(RecoilLanguage.Complete(doc, at), c => c.Label == "1" && c.Detail.Contains("node_01"));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "RECOIL_NODE_LINK");
    }

    [Theory]
    [InlineData("(node_00 (", "node body", "unused")]
    [InlineData("(node_00 (0 ", "position", "position")]
    [InlineData("(node_00 (0 (1.0 ", null, "y")]
    [InlineData("(node_00 (0 (1.0 2.0 3.0) ", "links", "links")]
    [InlineData("(node_00 (0 (1.0 2.0 3.0) (", "-1", "link 1")]
    public void IncompleteNodeOffersContextAndRetainsSyntaxError(string text, string? completion, string parameter)
    {
        var doc = Doc("net_01.zrd", text);
        Assert.Contains(doc.Diagnostics, d => d.Code == "RECOIL_SYNTAX");
        if (completion != null) Assert.Contains(RecoilLanguage.Complete(doc, text.Length), c => c.Label == completion);
        var signature = RecoilLanguage.SignatureHelp(doc, text.Length);
        Assert.NotNull(signature);
        Assert.StartsWith(parameter, signature.Parameters[signature.ActiveParameter].Label);
    }

    [Fact]
    public void FilenameClassificationAndRootNodeSnippetsWorkBeforeFirstNode()
    {
        var doc = Doc("net_04.zrd", "(");
        Assert.Equal("network", doc.Family);
        Assert.Contains(RecoilLanguage.Complete(doc, 1), c => c.Label == "attack_strategy");
        Assert.Contains(RecoilLanguage.Complete(doc, 1), c => c.Label == "node_00" && c.IsSnippet && c.InsertText.Contains("0.0"));
        var unknown = Doc("unrelated.zrd", "(");
        Assert.DoesNotContain(RecoilLanguage.Complete(unknown, 1), c => c.Label == "node_00");
    }

    [Theory]
    [InlineData("HEAD_ON", "minimum pursuit range:", "maximum pursuit range:")]
    [InlineData("CIRCLE", "orbit range:", "unused:")]
    [InlineData("BACK_AND_FORTH", "attack range:", "lateral movement:")]
    [InlineData("FOLLOW_NET", "unused:", "unused:")]
    [InlineData("circle", "minimum pursuit range:", "maximum pursuit range:")]
    public void PursuitRolesFollowRetailStrategyDispatch(string strategy, string first, string second)
    {
        var doc = Doc("net_01.zrd", $"(attack_strategy ({strategy}) pursuit_params (10.0 20.0))");
        Assert.Equal(new[] { first, second }, Hints(doc).Select(h => h.Label));
    }

    [Theory]
    [InlineData("player.zrd", "(mouse_push (0.2 0.3))", 2, "horizontal response:")]
    [InlineData("weather.zrd", "(MISSION1 (TYPE RAIN PARTICLES 4 COLOR (1 2 3)))", 3, "red:")]
    [InlineData("aiv.zrd", "(tank_01 (1 (0.0 0.0 0.0) 90.0))", 5, "network:")]
    [InlineData("puppies.zrd", "((pu000 0 (0.0 0.0 0.0) (0.0 0.0 0.0) 0.0))", 9, "pickup type:")]
    [InlineData("weapons.zrd", "(BALLISTICS (gun (BEAM (2.0 3.0 1))))", 3, "reciprocal timing:")]
    [InlineData("sounds.zrd", "(SYNTAX (2) SETS (M1 ((snd sound.wav HIGH (44100 16 2) VOLUME 0.5))))", 5, "sample ID:")]
    [InlineData("effects.zrd", "(EFFECTS ((model NAME (fx) SPEED 2 MAPS (one two))))", 4, "model node:")]
    [InlineData("objectives.zrd", "(OBJECTIVE1 (image title desc done READ_SOUND (sample 2.0)))", 6, "image:")]
    [InlineData("pickups.zrd", "(PICKUP_DATA (pu000 (SOUND (snd) IMAGE (image))))", 0, null)]
    [InlineData("ai.zrd", "(TURRET (gun (PARTS (base barrel muzzle) WEAPON (NAME (gun) AMMO (10)))))", 4, "base:")]
    [InlineData("hud.zrd", "(TYPEI ((1 2 3 4)))", 4, "left (px):")]
    [InlineData("dialog.zrd", "(MENU (FONTS ((1 Arial 12 (255 0 0) 1 400 LEFT))))", 9, "style ID:")]
    [InlineData("startanims.zrd", "(NEW_GAME_START ((start)))", 1, "animation:")]
    public void ResourceFamiliesUseTheirOwnLayouts(string file, string text, int count, string? first)
    {
        var hints = Hints(Doc(file, text));
        Assert.Equal(count, hints.Count);
        Assert.Equal(first, hints.FirstOrDefault()?.Label);
    }

    [Fact]
    public void AnimationLabelsNestedParametersAndCallbackMeanings()
    {
        const string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (CALLBACK (START_TIME (EVENT_OFFSET 0.5) VALUE (14)) OBJECT_MOTION (TRANSLATION (10 20 30 40)))))))";
        var hints = Hints(Doc("anim.zad", text));
        Assert.Contains(hints, h => h.Label == "offset (s):");
        Assert.Contains(hints, h => h.Label == "yaw (deg):");
        Assert.Contains(hints, h => h.Label == "event ID:" && h.Documentation.Contains("Disable single-player"));
    }

    [Fact]
    public void UnknownSlotsCommentsAndExtraValuesDoNotAcquireGuessedMeanings()
    {
        const string text = "(bft (common_mode (camera_ud_swing (1.0 2.0 3.0 4.0)) basic_mode (collision_d (0.5 0.15) rates (10.0 30.0 99.0)))) # 123";
        var hints = Hints(Doc("vehicle.zrd", text));
        Assert.Equal(3, hints.Count);
        Assert.Equal(new[] { "1.0", "10.0", "30.0" }, hints.Select(h => text[h.Span.Start..h.Span.End]));
        Assert.Empty(Hints(Doc("custom.zrd", "(anything (1 2 3))")));
    }

    [Fact]
    public void RangeFilteringPrecedesDedicatedHintBudgetAndUsesUtf16Offsets()
    {
        var text = "# 😀\r\n(bft (common_mode (weapons (" + string.Concat(Enumerable.Repeat("(wep_2.0 1 0 3 1.2 5.0 5.0 1 1)\r\n", 600)) + "))))";
        var doc = Doc("vehicle.zrd", text);
        Assert.True(RecoilLanguage.InlayHints(doc, new(0, text.Length), TestContext.Current.CancellationToken).Truncated);
        int start = text.LastIndexOf("wep_2.0", StringComparison.Ordinal);
        var tail = RecoilLanguage.InlayHints(doc, new(start, text.Length - start), TestContext.Current.CancellationToken);
        Assert.False(tail.Truncated);
        Assert.Equal(9, tail.Items.Count);
        Assert.Equal(start, tail.Items[0].Span.Start);
        Assert.Throws<OperationCanceledException>(() => RecoilLanguage.InlayHints(doc, new(0, text.Length), new CancellationToken(true)));
    }

    [Fact]
    public void ConditionalOperandsRespectConsumerLayoutAndPrecedence()
    {
        var shadowed = Hints(Doc("net_01.zrd", "(pursuit_range (1.0 2.0) pursuit_params (3.0 4.0))"));
        Assert.Equal(new[] { "unused:", "unused:", "minimum pursuit range:", "maximum pursuit range:" }, shadowed.Select(h => h.Label));
        Assert.Contains(Hints(Doc("hud.zrd", "(SHIELD ((image 1 2 TRUE FALSE) (text 3 4) (0 0 10 10)))")), h => h.Label == "visible:");
        var extra = Hints(Doc("hud.zrd", "(SHIELD ((image 1 2 TRUE FALSE ignored) (text 3 4) (0 0 10 10)))"));
        Assert.DoesNotContain(extra, h => h.Label is "visible:" or "centered:");
        Assert.Equal(2, extra.Count(h => h.Label == "unused:"));
        var complete = Hints(Doc("sounds.zrd", "(SYNTAX (1) SETS (M1 ((snd file.wav 0.5 TRUE FALSE FALSE 2.0 3.0))))"));
        Assert.Contains(complete, h => h.Label == "minimum distance:");
        var partial = Hints(Doc("sounds.zrd", "(SYNTAX (1) SETS (M1 ((snd file.wav 0.5 TRUE FALSE FALSE 2.0))))"));
        Assert.DoesNotContain(partial, h => h.Label == "minimum distance:");
    }

    [Theory]
    [InlineData("sounds.zrd", "(SYNTAX (2) SETS (M1 ((snd file.wav ", "HIGH")]
    [InlineData("effects.zrd", "(EFFECTS ((model ", "SPEED")]
    [InlineData("objectives.zrd", "(OBJECTIVE1 (image title desc done ", "READ_SOUND")]
    public void PositionalHeaderAllowsTheFirstTaggedSetting(string file, string text, string expected)
    {
        var doc = Doc(file, text);
        Assert.Contains(RecoilLanguage.Complete(doc, text.Length), item => item.Label == expected);
        Assert.Null(RecoilLanguage.SignatureHelp(doc, text.Length));
    }

    [Fact]
    public void NestedTupleSnippetPreservesTheArrayStructure()
    {
        const string text = "(MENU (BACKGROUND_VIDEOS (";
        var completion = Assert.Single(RecoilLanguage.Complete(Doc("dialog.zrd", text), text.Length));
        Assert.True(completion.IsSnippet);
        Assert.Equal("(${1:name} ${2:0} ${3:0} (${4:0} ${5:0} ${6:0}))", completion.InsertText);
    }

    [Theory]
    [InlineData("(node_00 (0 (0.0 # comment ")]
    [InlineData("(node_00 (0 (0.0 0.0 0.0) (# comment ")]
    public void CommentsDoNotTriggerArgumentAssistance(string text)
    {
        var doc = Doc("net_01.zrd", text);
        Assert.Null(RecoilLanguage.SignatureHelp(doc, text.Length));
        Assert.Empty(RecoilLanguage.Complete(doc, text.Length));
        var nextLine = Doc("net_01.zrd", text + "\r\n");
        Assert.NotNull(RecoilLanguage.SignatureHelp(nextLine, nextLine.Text.Length));
    }

    [Fact]
    public void QuotedHashesAreStringsAndFloatLinksDoNotInventTargets()
    {
        const string text = "(name (\"a\\\"#b\") attack_strategy (";
        var doc = Doc("net_01.zrd", text);
        Assert.NotEmpty(RecoilLanguage.Complete(doc, text.Length));
        Assert.NotNull(RecoilLanguage.SignatureHelp(doc, text.Length));
        var links = Hints(Doc("net_01.zrd", "(node_00 (0 (0.0 0.0 0.0) (1.5 -1.0 -1)))"));
        Assert.Equal(new[] { "link 1:", "link 2:", "link 3 (no link):" }, links.Skip(4).Select(h => h.Label));
    }

    [Theory]
    [InlineData("XYZ_ROTATION (1 2 3 4 5)", 2, "x acceleration (deg/s²)")]
    [InlineData("SCALE (1 2 3 4)", 1, "x acceleration")]
    public void MotionAccelerationsRequireTheCompleteGroup(string field, int unused, string expectedParameter)
    {
        string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (OBJECT_MOTION (" + field + "))))))";
        var doc = Doc("anim.zad", text);
        Assert.Equal(unused, Hints(doc).Count(h => h.Label == "unused:"));
        var signature = RecoilLanguage.SignatureHelp(doc, text.IndexOf(" 4", StringComparison.Ordinal) + 1)!;
        Assert.Equal(expectedParameter + "?", signature.Parameters[signature.ActiveParameter].Label);
    }

    [Fact]
    public void IncompleteOffsetsAndImpactGroupsRemainUnused()
    {
        const string call = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (CALL_ANIMATION (NAME (other) AT_NODE (node 1 2)))))))";
        Assert.Equal(2, Hints(Doc("anim.zad", call)).Count(h => h.Label == "unused:"));
        var impact = Hints(Doc("weapons.zrd", "(BALLISTICS (test (IMPACT (HEAT (1.0 2.0 3.0)))))"));
        Assert.Equal(3, impact.Count(h => h.Label == "unused:"));
    }

    [Theory]
    [InlineData("node_0", 6)]
    [InlineData("\"node_0\"", 7)]
    [InlineData("node_00", 6)]
    public void CompletingAnExistingNodeKeyPreservesItsBody(string key, int cursorInKey)
    {
        string text = "(" + key + " (7 (1.0 2.0 3.0) (-1 -1 -1)) node_01 (0 (0.0 0.0 0.0) (-1 -1 -1)))";
        var completions = RecoilLanguage.Complete(Doc("net_01.zrd", text), 1 + cursorInKey);
        var item = Assert.Single(completions, c => c.Label == "node_00");
        Assert.False(item.IsSnippet);
        Assert.DoesNotContain(completions, c => c.Label == "node_01");
        var span = item.ReplacementSpan!.Value;
        string edited = text[..span.Start] + item.InsertText + text[span.End..];
        Assert.StartsWith("(node_00 (7 (1.0 2.0 3.0) (-1 -1 -1)) node_01", edited);
        Assert.Equal(14, Hints(Doc("net_01.zrd", edited)).Count);
    }

    [Theory]
    [InlineData("MODES", 4, "(a b c 1 2)")]
    [InlineData("WEAPON", 9, "(a b c d e f g 1 2)")]
    public void BoundedHudRowsStopAssistanceAtTheConsumerLimit(string field, int count, string row)
    {
        string text = "(" + field + " (" + string.Join(" ", Enumerable.Repeat(row, count)) + " ";
        foreach (string suffix in new[] { "", "(ignored " })
        {
            var doc = Doc("hud.zrd", text + suffix);
            Assert.Empty(RecoilLanguage.Complete(doc, doc.Text.Length));
            Assert.Null(RecoilLanguage.SignatureHelp(doc, doc.Text.Length));
        }
    }

    [Fact]
    public void CallbackFloatHintExplainsTheCompiledTruncatedEvent()
    {
        const string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (CALLBACK (VALUE (14.9)))))))";
        var doc = Doc("anim.zad", text);
        var hint = Assert.Single(Hints(doc), h => h.Label == "event ID:");
        Assert.Contains("Disable single-player", hint.Documentation);
        Assert.Contains("truncating", hint.Documentation);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "RECOIL_TYPE");
    }

    [Fact]
    public void BareCarriageReturnDoesNotTerminateAParserComment()
    {
        const string text = "(node_00 (0 (0.0 0.0 0.0) (# comment\r ";
        var doc = Doc("net_01.zrd", text);
        Assert.Empty(RecoilLanguage.Complete(doc, text.Length));
        Assert.Null(RecoilLanguage.SignatureHelp(doc, text.Length));
        var next = Doc("net_01.zrd", text + "\n ");
        Assert.NotEmpty(RecoilLanguage.Complete(next, next.Text.Length));
        Assert.NotNull(RecoilLanguage.SignatureHelp(next, next.Text.Length));
    }

    [Fact]
    public void ProjectReferenceSuggestionsRespectNumericArgumentSlots()
    {
        using var fixture = new SourceWorldFixture();
        const string path = "data/m1/zrdr/objectives.zrd";
        const string source = "(OBJECTIVE1 (image title desc done READ_SOUND (snd 0.5)))";
        fixture.Write(path, source);
        fixture.Write("data/m1/zrdr/sounds.zrd", "(SYNTAX (2) SETS (M1 ((snd sound.wav))))");
        var project = LanguageProject.Analyze(fixture.Project, token: TestContext.Current.CancellationToken);
        Assert.Contains(project.Complete(path, source.IndexOf("snd", StringComparison.Ordinal)), c => c.Label == "snd");
        Assert.Empty(project.Complete(path, source.IndexOf("0.5", StringComparison.Ordinal)));
    }

    [Fact]
    public void TurretTargetHintsAndAssistanceStopAfterEightTargets()
    {
        const string text = "(TURRET (gun (TARGETS (a b c d e f g h ignored))))";
        var doc = Doc("ai.zrd", text);
        Assert.Equal(8, Hints(doc).Count(h => h.Label == "node:"));
        int at = text.IndexOf("ignored", StringComparison.Ordinal);
        Assert.Null(RecoilLanguage.SignatureHelp(doc, at));
        Assert.Empty(RecoilLanguage.Complete(doc, at));
    }

    [Theory]
    [InlineData("root", 1)]
    [InlineData("root_a \"root_*\"", 2)]
    public void DefinitionNamesDescribeRootsAndRedundantNamesStayAvailableOnHover(string roots, int count)
    {
        string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (" + roots + ") ANIMATION_NAME (logical) SEQUENCE_DEFINITION (NAME (sequence) SOUND (NAME (sample)))))))";
        var doc = Doc("anim.zad", text);
        Assert.Equal(count, Hints(doc).Count(h => h.Label == "root node:"));
        Assert.Equal(count + 2, Hints(doc).Count);
        Assert.Contains(Hints(doc), h => h.Label == "sequence:");
        Assert.Contains(Hints(doc), h => h.Label == "sound sample:");
        int logical = text.IndexOf("logical", StringComparison.Ordinal);
        Assert.Contains("logical name", RecoilLanguage.Hover(doc, logical)!.Markdown);
        var signature = RecoilLanguage.SignatureHelp(doc, logical)!;
        Assert.Equal("animation name", signature.Parameters[signature.ActiveParameter].Label);
        Assert.Contains("first literal name", RecoilLanguage.Hover(doc, text.IndexOf(roots, StringComparison.Ordinal))!.Markdown);
        Assert.Equal(text, doc.Text);
    }

    [Fact]
    public void RedundantResourceHintsDoNotRemoveDocumentationOrChoices()
    {
        const string text = "(attack_strategy (HEAD_ON) activate_buddy (1) pursuit_params (2.0 3.0))";
        var doc = Doc("net_01.zrd", text);
        Assert.Equal(new[] { "minimum pursuit range:", "maximum pursuit range:" }, Hints(doc).Select(h => h.Label));
        int strategy = text.IndexOf("HEAD_ON", StringComparison.Ordinal);
        Assert.Contains("attack strategy", RecoilLanguage.Hover(doc, strategy)!.Markdown);
        Assert.Contains(RecoilLanguage.Complete(doc, strategy), item => item.Label == "CIRCLE");
        Assert.Equal("attack strategy", RecoilLanguage.SignatureHelp(doc, strategy)!.Parameters[0].Label);
    }

    [Fact]
    public void AnonymousRowsAndNestedTupleLabelsAreNotMistakenForFieldNames()
    {
        Assert.Equal("network:", Hints(Doc("aiv.zrd", "(network (1 (0.0 0.0 0.0) 0.0))"))[0].Label);
        var hud = Hints(Doc("hud.zrd", "(TYPEII ((0 0 640 480) (image 1 2 TRUE FALSE) (image 3 4 TRUE FALSE) (image 5 6 TRUE FALSE) (a b c d)))"));
        Assert.Equal(3, hud.Count(h => h.Label == "image:"));
        var ui = Hints(Doc("dialog.zrd", "(MENU (BACKGROUND_TEXT ((text 1 2 3)) BUTTONS (button (TEXT ((text 4 5 6)) LABEL (text 7 8 9)))))"));
        Assert.Equal(2, ui.Count(h => h.Label == "text:")); // BACKGROUND_TEXT and LABEL, not TEXT.
        Assert.Equal(3, ui.Count(h => h.Label == "x (px):"));
    }

    [Fact]
    public void HiddenLabelsDoNotConsumeTheVisibleHintBudget()
    {
        string text = "(BALLISTICS (gun (" + string.Concat(Enumerable.Repeat("FIRE_RATE (1.0) ", 4200)) + "BEAM (2.0 3.0 1))))";
        var doc = Doc("weapons.zrd", text);
        var hints = RecoilLanguage.InlayHints(doc, new(0, text.Length), TestContext.Current.CancellationToken);
        Assert.False(hints.Truncated);
        Assert.Equal(3, hints.Items.Count);
        Assert.Equal("reciprocal timing:", hints.Items[0].Label);
    }

    [Theory]
    [InlineData("CALL_SEQUENCE", "target sequence:")]
    [InlineData("STOP_SEQUENCE", "target sequence:")]
    [InlineData("CALL_ANIMATION", "animation:")]
    [InlineData("STOP_ANIMATION", "animation:")]
    [InlineData("RESET_ANIMATION", "animation:")]
    [InlineData("INVALIDATE_ANIMATION", "animation:")]
    [InlineData("CALL_OBJECT_CONNECTOR", "animation:")]
    [InlineData("SOUND", "sound sample:")]
    [InlineData("SOUND_NODE", "sound sample / node:")]
    [InlineData("EFFECT", "effect template:")]
    [InlineData("LIGHT_STATE", "light node:")]
    [InlineData("LIGHT_ANIMATION", "light node:")]
    [InlineData("CAMERA_STATE", "camera node:")]
    [InlineData("CAMERA_FROM_TO", "camera node:")]
    [InlineData("OBJECT_ACTIVE_STATE", "target node:")]
    [InlineData("OBJECT_TRANSLATE_STATE", "target node:")]
    [InlineData("OBJECT_SCALE_STATE", "target node:")]
    [InlineData("OBJECT_ROTATE_STATE", "target node:")]
    [InlineData("OBJECT_MOTION", "target node:")]
    [InlineData("OBJECT_MOTION_FROM_TO", "target node:")]
    [InlineData("OBJECT_MOTION_SI_SCRIPT", "target node:")]
    [InlineData("OBJECT_OPACITY_STATE", "target node:")]
    [InlineData("OBJECT_OPACITY_FROM_TO", "target node:")]
    [InlineData("OBJECT_CYCLE_TEXTURE", "target node:")]
    [InlineData("OBJECT_CONNECTOR", "target node:")]
    public void EventNamesExplainTheirContext(string eventName, string expected)
    {
        string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (" + eventName + " (NAME (target)))))))";
        var doc = Doc("anim.zad", text);
        int at = text.IndexOf("target", StringComparison.Ordinal);
        Assert.Equal(expected, Assert.Single(Hints(doc), h => h.Span.Start == at).Label);
        Assert.Equal(expected.TrimEnd(':'), RecoilLanguage.SignatureHelp(doc, at)!.Parameters[0].Label);
        Assert.NotNull(RecoilLanguage.Hover(doc, at));
    }

    [Fact]
    public void AttachmentRecipientsPositionAndUnitsStayDistinct()
    {
        const string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_ROOT_NAME (attach) HEALTH (5) EXECUTION_BY_RANGE (100) SEQUENCE_DEFINITION (CALL_ANIMATION (NAME (other) AT_NODE (position) WITH_NODE (alternate) OPERAND_NODE (recipient)) OBJECT_MOTION_SI_SCRIPT (NAME (target) SCRIPT_FRAME_RATE (30)) OBJECT_MOTION (NAME (moving) RUN_TIME (2))))))";
        var doc = Doc("anim.zad", text);
        var hints = Hints(doc);
        Assert.Contains(hints, h => h.Label == "attachment node:");
        Assert.Equal(2, hints.Count(h => h.Label == "position reference:"));
        Assert.Contains(hints, h => h.Label == "recipient node:");
        Assert.Contains(hints, h => h.Label == "damage threshold:");
        Assert.Contains(hints, h => h.Label == "maximum distance (world units):");
        Assert.Contains(hints, h => h.Label == "frames per second:");
        Assert.Contains(hints, h => h.Label == "duration (s):");
        Assert.Contains("weapon definition:", Hints(Doc("ai.zrd", "(TURRET (gun (WEAPON (NAME (cannon)))))")).Select(h => h.Label));
        Assert.Contains("effect template:", Hints(Doc("effects.zrd", "(EFFECTS ((model NAME (flame))))")).Select(h => h.Label));
    }

    [Fact]
    public void RootHelpDistinguishesLiteralAlternativesFromSinglePatterns()
    {
        var doc = Doc("anim.zad", "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (a b)))))");
        Assert.All(Hints(doc), h =>
        {
            Assert.Contains("first literal name present", h.Documentation);
            Assert.Contains("only to the single-value form", h.Documentation);
            Assert.Contains("not necessarily the world's topmost node", h.Documentation);
        });
    }
}
