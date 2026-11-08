using GameZ.Script.Core.Sources;

namespace GameZ.Script.Core.Language;

internal static partial class GameGenLanguage
{
    // Native command names and strncmp counts: mw3-re 8b0ceaff, zinterp_parse.cpp (MIT, Owen Morgan).
    // This table is deliberately separate from RECOIL's dispatch table.
    private sealed record NativeCommand(string Name, bool Exact, int PrefixLength);
    private static readonly NativeCommand[] Mw3Dispatch =
    [
        new("AddChild", false, 8),
        new("AddClutterTemplates", false, 19),
        new("AddEnhancerImage", false, 16),
        new("AnimSetZBDFile", false, 14),
        new("AnimSetDebugFrame", false, 17),
        new("CameraSetRotate", false, 12),
        new("CameraSetActive", false, 15),
        new("CameraSetDynamicLOD", false, 19),
        new("CameraSetFOV", false, 12),
        new("CameraSetHorizonXZ", false, 18),
        new("CameraSetHorizon", false, 16),
        new("CameraSetLODMultiplier", false, 22),
        new("CameraSetNearFarClip", false, 20),
        new("CameraSetObjectHSETest", false, 22),
        new("CameraSetWindow", false, 15),
        new("CameraSetWorld", false, 14),
        new("CameraSetTranslate", false, 15),
        new("CountCameraNodes", false, 14),
        new("CountUsedNodes", false, 14),
        new("CycleTextureSetLooping", false, 22),
        new("CycleTextureSetMap", false, 18),
        new("CycleTextureSetOn", false, 17),
        new("CycleTextureSetSpeed", false, 20),
        new("ClearScreenBuffer", false, 17),
        new("DeleteChild", false, 11),
        new("DeleteFile", false, 10),
        new("DeleteTree", false, 10),
        new("DisplayOrigin", false, 13),
        new("DisplayResolution", false, 17),
        new("DisplaySetClearColor", false, 20),
        new("FindNode", false, 8),
        new("FindSubNode", false, 11),
        new("FogLargeObjects", false, 15),
        new("FreeNode", false, 8),
        new("GameZReadZBDFile", false, 16),
        new("GameZWriteZBDFile", false, 17),
        new("GetBFETolerance", false, 15),
        new("GlobalWindRandomMaxSpeed", false, 24),
        new("GlobalWindRandomAccel", false, 21),
        new("GlobalWindRandomAngVel", false, 22),
        new("GlobalWindStaticVelocity", false, 24),
        new("LensFlareTexture", false, 16),
        new("LightNew", false, 8),
        new("LightSetActive", false, 14),
        new("LightSetAmbient", false, 15),
        new("LightSetColor", false, 13),
        new("LightSetDiffuse", false, 15),
        new("LightSetDirectedSource", false, 22),
        new("LightSetDirectional", false, 19),
        new("LightSetOrientation", false, 19),
        new("LightSetPointSource", false, 19),
        new("LightSetRanges", false, 14),
        new("LightSetSaturated", false, 17),
        new("LightSetStatic", false, 14),
        new("LightSetSubdivide", false, 17),
        new("LightSetTranslate", false, 17),
        new("LoadSoils", false, 9),
        new("LODAddChild", false, 11),
        new("LODSetRange", false, 11),
        new("MatlFaceColor", false, 13),
        new("MatlNew", false, 7),
        new("MatlTexture", false, 11),
        new("ModelNew", false, 8),
        new("ModelDoShadows", false, 14),
        new("ModelPolygonBegin", false, 17),
        new("ModelPolygonEnd", false, 15),
        new("ModelPolygonUV", false, 14),
        new("ModelPolygonVertex", false, 18),
        new("NewCamera", false, 9),
        new("NewDisplay", false, 10),
        new("NewLOD", false, 6),
        new("NewNode", false, 7),
        new("NewObject3D", false, 11),
        new("NewSEQ", false, 6),
        new("NewWindow", false, 9),
        new("NewWorld", false, 8),
        new("NodeSetActive", false, 13),
        new("NodeSetDescription", false, 18),
        new("NodeSetCanModify", false, 16),
        new("NodeSetLighting", false, 15),
        new("NodeSetOverwrite", false, 16),
        new("Object3DAddChild", false, 16),
        new("Object3DRegisterTexturesToWorld", false, 31),
        new("Object3DRotate", false, 14),
        new("Object3DScale", false, 13),
        new("Object3DSetActionPriority", false, 25),
        new("Object3DSetActive", false, 17),
        new("Object3DSetColor", false, 16),
        new("Object3DSetFacade", false, 17),
        new("Object3DSetOpacityIsSet", false, 23),
        new("Object3DSetOpacity", false, 18),
        new("Object3DSetPoints", false, 17),
        new("Object3DSetPriority", false, 19),
        new("Object3DSetScrollAlways", false, 23),
        new("Object3DSetScroll", false, 17),
        new("Object3DSetShowBackFace", false, 23),
        new("Object3DSetTextureWorldBaseCoordinates", false, 38),
        new("Object3DSetTextureWorldTexturesPerMeter", false, 37),
        new("Object3DSetMorphVertex", false, 22),
        new("Object3DTranslate", false, 17),
        new("PerspectiveTexture", false, 18),
        new("PrintNodeCount", false, 14),
        new("PrintUsedNodes", false, 14),
        new("RdrAddPath", false, 10),
        new("RdrSetPath", false, 10),
        new("SEQAddChild", false, 11),
        new("SEQNew", false, 6),
        new("SEQSetActive", false, 12),
        new("SEQSetLoop", false, 10),
        new("SEQSetPause", true, 12),
        new("SEQSetRepeat", false, 12),
        new("SetAltitudeSurface", false, 18),
        new("SetBFETolerance", false, 15),
        new("SetCoplanarTolerance", false, 20),
        new("SetColinearTolerance", false, 20),
        new("SetGameZNodeArraySize", false, 21),
        new("SetMaterialArraySize", false, 20),
        new("SetModel3DArraySize", false, 19),
        new("SetIntersectBBOX", false, 16),
        new("SetIntersectSurface", false, 19),
        new("SetLandmark", false, 11),
        new("SetPaletteName", false, 14),
        new("SetPaletteShading", false, 17),
        new("SetPerspectiveAdaptiveCorrection", false, 32),
        new("SetPerspectiveTextureDeltaX", false, 27),
        new("SetInverseZTolerance", false, 20),
        new("SetPerspectiveInverseZTolerance", false, 20),
        new("SetPerspectiveTextureFarZ", false, 25),
        new("SetProximity", false, 12),
        new("SetSmallPolygonRejectArea", false, 25),
        new("SetTextureDirectory", false, 19),
        new("SetVertexShading", false, 16),
        new("Verbose", false, 7),
        new("VideoSetDither", true, 15),
        new("VideoSetWireFrame", true, 18),
        new("WindowAddClearPolygonVertex", false, 27),
        new("WindowBuffer", true, 15),
        new("WindowCloseClearPolygon", false, 23),
        new("WindowOrigin", false, 12),
        new("WindowResolution", false, 16),
        new("WindowSetClearPolygon", false, 21),
        new("WorldAddLight", false, 13),
        new("WorldExtents", false, 12),
        new("WorldOrigin", false, 11),
        new("WorldPartitionInclusionTolerance", false, 32),
        new("WorldPartitionMaxDECFeatureCount", false, 32),
        new("WorldPartition", false, 14),
        new("WorldSetFogAltitude", false, 19),
        new("WorldSetFogColor", false, 16),
        new("WorldGetFogColor", true, 17),
        new("WorldSetFogDensity", false, 18),
        new("WorldSetFogRange", true, 17),
        new("WorldSetFogRangeNear", true, 21),
        new("WorldSetFogRangeFar", true, 20),
        new("WorldGetFogRange", true, 17),
        new("WorldSetFogState", false, 16),
        new("WorldSetVirtualPartitionMapSize", false, 31),
        new("WorldSetVirtualPartition", false, 24),
        new("WriteTextureSetType", false, 19),
        new("WriteTextureSetMap", false, 18),
        new("Echo", false, 4),
        new("echo", false, 4),
        new("TextureAdd", false, 10),
        new("CameraGetTranslate", true, 18),
        new("CameraSetNearClip", true, 17),
        new("CameraSetFarClip", true, 16),
        new("DirectedLightFluctuate", true, 22),
        new("DirectedLightFluctuatePeriod", true, 28),
        new("DirectedLightFluctuateWavelength", true, 32),
        new("DirectedLightFluctuateBaseAndAmp", true, 32),
        new("DirectedLightFluctuateHeading", true, 29),
        new("MakeShadows", true, 11),
        new("NodeDump", true, 8),
        new("Object3DGetTranslate", true, 20),
        new("PrintTree", true, 9),
    ];
    private static readonly Lazy<IReadOnlyList<GameGenCommand>> NativeCatalog = new(BuildMw3Catalog);
    private static IReadOnlyList<GameGenCommand> Mw3Commands => NativeCatalog.Value;

    private static IReadOnlyList<GameGenCommand> BuildMw3Catalog()
    {
        var sourceCommands = new[] { "ifdef", "ifndef", "endif", "mkdir", "set", "source", "who", "var", "Quit",
            "SetModelDirectory", "LoadGameGen", "GameGenSetWorld", "GameGenForceFlatShading", "GenericTerrainMorph", "SetMission", "SetKeyboardInput" };
        var native = Mw3Dispatch.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var known = Commands.Where(c => native.Contains(c.Name) || sourceCommands.Contains(c.Name, StringComparer.Ordinal))
            .ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (var command in Mw3Dispatch)
        {
            if (!known.TryGetValue(command.Name, out var existing))
                existing = new(command.Name, "", "Recognized MechWarrior 3 interpreter command. Unverified operands remain unlabelled.");
            known[command.Name] = existing with { Exact = command.Exact, PrefixLength = command.PrefixLength };
        }
        void Add(string names, string args, string help)
        {
            foreach (string name in names.Split(' '))
            {
                var dispatch = Mw3Dispatch.FirstOrDefault(c => c.Name == name);
                known[name] = new(name, args, help, dispatch?.Exact ?? true, dispatch?.PrefixLength, true);
            }
        }
        Add("LoadGameGen", "model name loadOption...", "Load a source glTF and name its root. Native load options altitude_off and intersect_off clear those flags on the loaded root. Unknown option spellings have no effect. This authoring command is not in the runtime Mech3 interpreter.");
        Add("SetModelDirectory GameGenSetWorld GameGenForceFlatShading", "", "Source-build command handled by the reconstructed authoring pipeline, not the native Mech3 runtime interpreter. Refer to its RECOIL-compatible authoring operands.");
        // Preserve these authoring signatures rather than importing runtime contracts into the build tool.
        foreach (string name in new[] { "SetModelDirectory", "GameGenSetWorld", "GameGenForceFlatShading", "GenericTerrainMorph", "SetMission", "SetKeyboardInput" })
            if (Commands.FirstOrDefault(c => c.Name == name) is { } source) known[name] = source with { Exact = true, PrefixLength = null };
        Add("LightSetStatic LightSetSubdivide", "enabled", "Set this native MW3 light switch. on/true enables it, case-insensitively; other values disable it.");
        Add("LightSetDirectedSource LightSetPointSource", "", "Select this native light-source mode. This command consumes no on/off operand.");
        Add("CameraSetRotate", "xDegrees yDegrees zDegrees", "Set the camera's rotation in degrees. Native dispatch compares the CameraSetRot prefix.");
        Add("CameraSetTranslate", "x y z", "Set the camera's local position. Native dispatch compares the CameraSetTransl prefix.");
        Add("CameraSetDynamicLOD", "multiplier", "Set the native renderer's dynamic-LOD scalar. This command consumes numeric text rather than an on/off switch.");
        Add("SetPerspectiveTextureDeltaX", "step", "Set the native interpreter's single integer perspective-correction step.");
        Add("SetPerspectiveAdaptiveCorrection", "minimum maximum tolerance", "Configure native perspective correction with two integers and a float.");
        Add("SetGameZNodeArraySize", "capacity", "The native MW3 runtime consumes this integer but its setter is an empty stub. The source build retains the declaration for inspection.");
        Add("RdrAddPath", "directories", "Add semicolon-separated native reader search folders. Archive lookup follows the mounted archive order separately from loose-source lookup.");
        Add("RdrSetPath", "directories", "Replace the native reader search folders with this semicolon-separated list. Archive lookup follows the mounted archive order separately from loose-source lookup.");
        Add("CycleTextureSetMap TextureAdd WriteTextureSetMap LensFlareTexture", "texture", "Register a texture by its native engine name. The source project resolves the corresponding PNG through its texture search folders.");
        Add("LensFlareTexture", "slot texture", "Register a native lens-flare texture in this integer slot.");
        Add("NewNode", "name", "Create and select a named native node.");
        Add("echo Echo", "text...", "Write the remaining native interpreter operands to its output. Analysis does not execute it.");
        Add("var", "variable operation value", "Assign an externally registered interpreter variable with set or =. MW3 registers DamageScale as a float at startup. It is a runtime variable, separate from macros; arbitrary engine extensions may register other names.");
        Add("ifdef ifndef", "macro expression...", "Test macros whose value is exactly lowercase true in the native MW3 runtime. Operators || and && combine left to right. While skipping, nested conditions are ignored; the first endif ends the skip. Reconstructed authoring scripts use the source build's condition policy.");
        Add("AddClutterTemplates", "template", "Register a named clutter template. The native command takes exactly one argument; gameplay clutter creation is not simulated.");
        Add("AddEnhancerImage", "texture", "Register the named texture for enhancer imagery.");
        Add("AnimSetDebugFrame", "", "Native animation inspection command; it consumes no frame operand.");
        Add("FogLargeObjects", "enabled", "Native MW3 consumes this boolean, but its setter is an empty stub.");
        Add("GlobalWindRandomMaxSpeed", "speed", "Set the maximum native random wind speed. The native command requires exactly one operand.");
        Add("GlobalWindRandomAccel", "acceleration", "Set native random wind acceleration. The native command requires exactly one operand.");
        Add("GlobalWindRandomAngVel", "degreesPerSecond", "Set native random wind angular velocity in degrees per second. The native command requires exactly one operand.");
        Add("GlobalWindStaticVelocity", "x y z", "Set native static wind velocity XYZ. The native command requires exactly three operands.");
        Add("DirectedLightFluctuate", "enabled", "Enable native light fluctuation. This exact command requires one operand.");
        Add("DirectedLightFluctuatePeriod", "seconds", "Set native light fluctuation period. This exact command requires one operand.");
        Add("DirectedLightFluctuateWavelength", "distance", "Set native light fluctuation wavelength. This exact command requires one operand.");
        Add("DirectedLightFluctuateBaseAndAmp", "base amplitude", "Set native light fluctuation base and amplitude. This exact command requires two operands.");
        Add("DirectedLightFluctuateHeading", "degrees", "Set native light fluctuation heading in degrees. This exact command requires one operand.");
        Add("ModelDoShadows", "enabled", "Enable model shadows on the native hardware path. The native command requires exactly one operand.");
        Add("MatlNew", "", "Begin a native procedural material. This command consumes no material-name operand.");
        Add("ModelNew", "name kind", "Create a native procedural model and select its root; a kind beginning with Facade selects facade rendering.");
        Add("ModelPolygonVertex", "x y z", "Add a native procedural polygon vertex.");
        Add("ModelPolygonUV", "u v", "Add native procedural polygon texture coordinates.");
        Add("ModelPolygonBegin ModelPolygonEnd", "", "Begin or finish a native procedural polygon.");
        Add("Object3DSetFacade Object3DSetPoints", "", "Select this native model's facade mode. These commands consume no on/off operand.");
        Add("Object3DSetActionPriority", "priority", "Set the current object's integer action priority.");
        Add("Object3DSetPriority", "priority", "Set the current model's integer priority.");
        Add("Object3DSetColor", "color", "Set the native model's packed integer colour value. It does not consume an RGB triple.");
        Add("Object3DSetOpacityIsSet", "value", "Set the native object's integer opacity-is-set value.");
        Add("WorldSetVirtualPartitionMapSize", "width height", "Set native virtual-partition map dimensions as two integers.");
        Add("WorldPartitionMaxDECFeatureCount", "count", "Set the native maximum DEC feature count per partition.");
        Add("WindowAddClearPolygonVertex", "x y z", "Add one native window clear-polygon vertex.");
        Add("WindowBuffer", "buffer", "Choose the native window's integer buffer setting.");
        Add("WindowCloseClearPolygon", "", "Finish the native window's clear polygon.");
        Add("WindowSetClearPolygon", "enabled", "Enable or disable the native window's clear polygon.");
        Add("Object3DRegisterTexturesToWorld", "enabled", "Set the native model's texture-registration flag.");
        Add("CameraGetTranslate Object3DGetTranslate CountCameraNodes CountUsedNodes GetBFETolerance PrintNodeCount PrintUsedNodes PrintTree NodeDump MakeShadows WorldGetFogColor WorldGetFogRange", "", "Recognized native inspection or rendering command. Analysis does not execute it.");
        Add("Object3DSetScroll Object3DSetScrollAlways", "enabled uRate vRate", "Enable native texture scrolling with U/V rates. Turning it off with this command crashes the original MW3 1.2 interpreter; remove the instruction and disable the model's scrolling property instead.");
        return known.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateMw3(LanguageDocument doc, GameGenScriptLine line, GameGenCommand command)
    {
        if (command.Name == "LoadGameGen")
            for (int i = 3; i < line.Tokens.Count; i++)
                if (!line.Tokens[i].Contains('%') && line.Tokens[i] is not ("altitude_off" or "intersect_off"))
                    doc.Report("GAMEZ_LOAD_OPTION", "warning", "Unknown native load option; this spelling does not change the loaded root's flags.", line.Spans[i]);
        if (command.Name is "Object3DSetScroll" or "Object3DSetScrollAlways"
            && line.Tokens.Count > 1 && !line.Tokens[1].Contains('%')
            && !line.Tokens[1].Equals("on", StringComparison.OrdinalIgnoreCase) && !line.Tokens[1].Equals("true", StringComparison.OrdinalIgnoreCase))
            doc.Report("GAMEZ_UNSAFE_SCROLL", "error", "The original MW3 1.2 interpreter crashes when texture scrolling is turned off by this command. Remove it and disable the model's scrolling property instead.", line.Spans[1]);
    }
}
