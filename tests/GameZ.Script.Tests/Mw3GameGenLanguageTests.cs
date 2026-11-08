using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class Mw3GameGenLanguageTests
{
    private static LanguageDocument Script(string text, GameVariant game = GameVariant.MechWarrior3) =>
        GameGenLanguage.Analyze("gamegen/native.gw", text, TestContext.Current.CancellationToken, game);

    [Fact]
    public void NativeLoadOptionsHaveHintsCompletionAndOptionalSignatureWithoutWeakeningRecoil()
    {
        const string text = "LoadGameGen model.gltf model intersect_off altitude_off";
        var doc = Script(text);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code is "GAMEZ_COMMAND" or "GAMEZ_OPERANDS" or "GAMEZ_LOAD_OPTION");
        var hints = RecoilLanguage.InlayHints(doc, new(0, text.Length), TestContext.Current.CancellationToken).Items;
        Assert.Equal(4, hints.Count);
        Assert.Equal("loadOption:", hints[^1].Label);
        Assert.Contains("altitude_off", hints[^1].Documentation);
        var signature = RecoilLanguage.SignatureHelp(doc, text.Length)!;
        Assert.Equal(2, signature.ActiveParameter);
        Assert.Equal("loadOption...", signature.Parameters[2].Label);
        const string incomplete = "LoadGameGen model.gltf model alt";
        Assert.Contains(RecoilLanguage.Complete(Script(incomplete), incomplete.Length), c => c.Label == "altitude_off");
        Assert.Contains(Script(text, GameVariant.Recoil).Diagnostics, d => d.Code == "GAMEZ_OPERANDS");
        Assert.Contains(Script("LoadGameGen model.gltf model altitud_off").Diagnostics, d => d.Code == "GAMEZ_LOAD_OPTION");
    }

    [Fact]
    public void NativePerspectiveAndLightSignaturesAreDialectSpecific()
    {
        const string text = "SetModelDirectory ..\\data\\models\nGameGenSetWorld world\nLightSetStatic on\nLightSetSubdivide OFF\nSetPerspectiveTextureDeltaX 32\nSetPerspectiveAdaptiveCorrection 1 2 0.01\n";
        Assert.Empty(Script(text).Diagnostics);
        Assert.Contains(Script("LightSetStatic on", GameVariant.Recoil).Diagnostics, d => d.Code == "GAMEZ_COMMAND");
        Assert.Contains(Script("SetPerspectiveTextureDeltaX 32", GameVariant.Recoil).Diagnostics, d => d.Code == "GAMEZ_OPERANDS");
        Assert.Contains(Script("SetPerspectiveTextureDeltaX 1 2 3").Diagnostics, d => d.Code == "GAMEZ_OPERANDS");
        Assert.Contains(Script("CameraSetDynamicLOD on").Diagnostics, d => d.Code == "GAMEZ_NUMBER");
        Assert.DoesNotContain(Script("CameraSetDynamicLOD 0.5").Diagnostics, d => d.Code == "GAMEZ_BOOLEAN");
    }

    [Fact]
    public void NativeExactAndTruncatedDispatchPrefixesMatchTheInterpreter()
    {
        Assert.Equal("CameraSetRotate", GameGenLanguage.Command("CameraSetRotSuffix", GameVariant.MechWarrior3)!.Name);
        Assert.Equal("CameraSetTranslate", GameGenLanguage.Command("CameraSetTranslSuffix", GameVariant.MechWarrior3)!.Name);
        Assert.Equal("WorldSetVirtualPartitionMapSize", GameGenLanguage.Command("WorldSetVirtualPartitionMapSizeSuffix", GameVariant.MechWarrior3)!.Name);
        Assert.Null(GameGenLanguage.Command("VideoSetDitherSuffix", GameVariant.MechWarrior3));
        Assert.Null(GameGenLanguage.Command("CameraSetNearClipSuffix", GameVariant.MechWarrior3));
        Assert.Null(GameGenLanguage.Command("lightSetStatic", GameVariant.MechWarrior3));
        Assert.Null(GameGenLanguage.Command("Object3DSetScrllAlways", GameVariant.MechWarrior3));
        Assert.Equal("CameraRotate", GameGenLanguage.Command("CameraRotateSuffix", GameVariant.Recoil)!.Name);
    }

    [Fact]
    public void NativePackedColorAndZeroArgumentModesDoNotUseRecoilContracts()
    {
        Assert.Empty(Script("Object3DSetColor 255\nObject3DSetFacade\nMatlNew\nWindowSetClearPolygon on\nObject3DRegisterTexturesToWorld off").Diagnostics);
        Assert.Contains(Script("Object3DSetColor 1 2 3").Diagnostics, d => d.Code == "GAMEZ_OPERANDS");
        Assert.Contains(Script("Object3DSetFacade on").Diagnostics, d => d.Code == "GAMEZ_OPERANDS");
        Assert.DoesNotContain(Script("Object3DSetOpacityIsSet 1").Diagnostics, d => d.Code == "GAMEZ_BOOLEAN");
    }

    [Theory]
    [InlineData("Object3DSetScroll")]
    [InlineData("Object3DSetScrollAlways")]
    public void UnsafeNativeScrollOffIsAnExactOperandError(string command)
    {
        string text = command + " off 0.0 1.0";
        var issue = Assert.Single(Script(text).Diagnostics, d => d.Code == "GAMEZ_UNSAFE_SCROLL");
        Assert.Equal("off", text.Substring(issue.Span.Start, issue.Span.Length));
        Assert.Equal("error", issue.Severity);
        Assert.DoesNotContain(Script(command + " on 0.0 1.0").Diagnostics, d => d.Code == issue.Code);
        Assert.DoesNotContain(Script(text, GameVariant.Recoil).Diagnostics, d => d.Code == issue.Code);
        Assert.DoesNotContain(Script(command + " %SCROLL% 0.0 1.0").Diagnostics, d => d.Code == issue.Code);
    }

    [Fact]
    public void RuntimeVariableHelpIsAdvisoryAndCommentsRetainNoHints()
    {
        const string text = "var DamageScale set 1.0 # extra words\nvar custom set text";
        var doc = Script(text);
        Assert.Empty(doc.Diagnostics);
        Assert.Contains("DamageScale", RecoilLanguage.Hover(doc, 5)!.Markdown);
        Assert.Contains(RecoilLanguage.Complete(Script("var Dam"), 7), c => c.Label == "DamageScale");
        Assert.Null(RecoilLanguage.Hover(doc, text.IndexOf("extra", StringComparison.Ordinal)));
        Assert.Empty(RecoilLanguage.Complete(doc, text.IndexOf("extra", StringComparison.Ordinal)));
    }
}
