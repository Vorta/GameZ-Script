using GameZ.Script.Core.Sources;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Language;

internal sealed record GameGenCommand(string Name, string Parameters, string Description, bool Exact = false)
{
    internal string[] Slots => Parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    internal string Help => $"**{Name} {Parameters}**\n\n{Description}\n\n" +
        (Exact ? "Command spelling is case-sensitive and exact." : "Command dispatch uses a case-sensitive prefix.") +
        "\n\nOperands are text tokens; script numbers do not have ZRD integer/float storage kinds. Analysis never executes file-writing commands.";
}

/// <summary>Script editor metadata. Unknown operand meanings deliberately have no invented signatures.</summary>
internal static class GameGenLanguage
{
    internal static bool IsScript(string path) => Path.GetExtension(path).Equals(".gs", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".gw", StringComparison.OrdinalIgnoreCase);
    internal static readonly IReadOnlyList<GameGenCommand> Commands = Catalog();
    private static IReadOnlyList<GameGenCommand> Catalog()
    {
        var known = ScriptCommands.Names.ToDictionary(n => n, n => new GameGenCommand(n, "",
            "Recognized engine command. Operand metadata is not verified; world analysis reports effects it cannot apply."), StringComparer.Ordinal);
        void Add(string names, string args, string help, bool exact = false)
        {
            foreach (string name in names.Split(' ')) known[name] = new(name, args, help, exact);
        }
        Add("ifdef ifndef", "macro expression...", "Test macros whose value is exactly TRUE. Operators || and && combine left to right. While skipping, nested conditions are ignored; the first endif ends the skip.");
        Add("endif", "", "End a skipped condition. Condition state is shared across sourced scripts.");
        Add("set", "macro value?", "Set a case-sensitive macro. References in operands expand before assignment; an unset macro expands to empty text.");
        Add("source", "script", "Run a script relative to gamegen/, sharing macros, paths and current node. Quit ends the sourced file, then its caller continues.");
        Add("Quit", "", "End this script only; return to the sourcing script.", true);
        Add("mkdir", "directory", "Create an output directory in the interpreter. Analysis describes this operation without creating anything.");
        Add("who", "", "List registered macros and variables.");
        Add("var", "variable operation value", "Assign an externally registered interpreter variable using set or =. Registered runtime variables are distinct from macros; string variables cannot be assigned.");
        Add("SetModelDirectory SetTextureDirectory RdrAddPath", "directories", "Add semicolon-separated existing search folders. New textual entries go to the head; repeating an existing spelling keeps its order.");
        Add("RdrSetPath", "directories", "Replace the resource search-path list before adding semicolon-separated folders. Compiled resource lookup also follows mounted archive precedence.");
        Add("LoadGameGen", "model name", "Load a glTF model through the current model search paths, naming its load root. An unplaced root can supply an AIV or pickup template.", true);
        Add("GameGenSetWorld", "world?", "Select the current world as the GameGen world.", true);
        Add("GameGenForceFlatShading", "enabled", "GameGen authoring shading setting; source model materials determine the reconstructed build.", true);
        Add("GenericTerrainMorph", "model name", "Legacy GameGen terrain-morph authoring command. Its effects are not modeled by the source assembler.", true);
        Add("GameZWriteZBDFile", "output", "Capture the built world here. Subsequent build-script loads do not enter this world. The language service never writes this output.");
        Add("GameZReadZBDFile", "world", "Read the mission world at runtime. Source analysis uses its corresponding built world, without requiring exported archives.");
        Add("AnimSetZBDFile", "output", "Declare the animation output, not an existing input file.");
        Add("NewWorld NewWindow NewDisplay NewCamera LightNew NewObject3D NewLOD NewNode NewSEQ SEQNew", "name", "Create and select a named node of this command's class.");
        Add("FindNode", "node", "Select the most recent live node with this exact name. Node names are case-sensitive.");
        Add("FindSubNode", "node", "Find within the current subtree, using the engine's child traversal order.");
        Add("NodeSetDescription", "name", "Rename the current node. Later lookups use the new name.");
        Add("AddChild Object3DAddChild LODAddChild SEQAddChild", "node", "Attach the named node to the current receiver; object identity and existing parents matter.");
        Add("DeleteChild", "node", "Remove a matching child from the current receiver.");
        Add("DeleteTree FreeNode", "node", "Free the named node/tree when ownership permits. Freed nodes cannot satisfy later lookups.");
        Add("DeleteFile", "file", "Delete a file in the interpreter. Analysis never performs this operation.");
        Add("Object3DTranslate CameraTranslate", "x y z", "Set the receiver's local translation in world units.");
        Add("Object3DRotate CameraRotate LightSetOrientation", "xDegrees yDegrees zDegrees", "Set rotation angles in degrees. Script transforms and AIV headings have different layouts.");
        Add("Object3DScale", "xScale yScale zScale", "Set the current object's local scale.");
        Add("LightSetTranslate", "x y z", "Set the current light's local position.");
        Add("CameraSetWorld", "world", "Assign an existing world to the current camera.");
        Add("CameraSetWindow", "window", "Assign an existing window to the current camera.");
        Add("CameraSetHorizon CameraSetHorizonXZ", "node", "Assign a named horizon to the current camera.");
        Add("CameraSetFOV", "horizontalDegrees verticalDegrees", "Set the current camera's horizontal and vertical field of view.");
        Add("CameraSetNearFarClip", "near far", "Set the current camera's clip distances.");
        Add("CameraSetNearClip CameraSetFarClip", "distance", "Set one clip distance.", true);
        Add("CameraSetLODMultiplier", "multiplier", "Set the current camera's LOD distance multiplier.");
        Add("WorldOrigin WorldExtents WorldPartition WorldPartitionInclusionTolerance", "x z", "Set the current world's grid values. Extents and partition values use the script's signed X/Z convention.");
        Add("WorldSetFogColor LightSetColor DisplaySetClearColor", "red green blue", "Set RGB color components.");
        Add("WorldSetFogRange", "near far", "Set the current world's fog range.", true);
        Add("WorldSetFogAltitude", "low high", "Set fog altitude bounds.");
        Add("WorldSetFogDensity", "density", "Set exponential fog density.");
        Add("WorldSetFogState", "state", "Fog mode: linear, exponential, or off.");
        Add("WorldAddLight", "light", "Attach a named light to the current world.");
        Add("WindowOrigin DisplayOrigin WindowResolution DisplayResolution", "x y", "Set the receiver's pixel origin or resolution.");
        Add("LightSetRanges", "near far", "Set light attenuation ranges; the engine orders them and separates equal ranges.");
        Add("LightSetAmbient LightSetDiffuse", "intensity", "Set the current light's intensity.");
        Add("LightSetDirectedSource LightSetPointSource", "", "Select this light-source mode; directed and point modes exclude each other.");
        Add("SetGameZNodeArraySize SetModel3DArraySize SetMaterialArraySize", "capacity", "Configure runtime pools in the mission initialization script. These capacities must accommodate world and runtime allocations.");
        Add("SetAltitudeSurface SetIntersectSurface SetIntersectBBOX SetLandmark SetProximity NodeSetCanModify NodeSetOverwrite NodeSetLighting NodeSetActive Object3DSetActive LightSetActive LightSetDirectional LightSetSaturated CameraSetActive CameraSetDynamicLOD CameraSetObjectHSETest PerspectiveTexture ClearScreenBuffer SetPerspectiveAdaptiveCorrection SetVertexShading SetPaletteShading VideoSetDither VideoSetWireFrame Verbose Object3DSetFacade Object3DSetOpacityIsSet Object3DSetShowBackFace WorldSetVirtualPartition SEQSetActive SEQSetLoop SEQSetPause", "enabled", "Boolean script operand: on/true enables (case-insensitive); other values disable.");
        Add("Object3DSetScroll Object3DSetScrollAlways", "enabled uRate vRate", "Enable or disable texture scrolling and specify U/V rates.");
        Add("Object3DSetOpacity", "opacity", "Set the current object's opacity.");
        Add("Object3DSetColor", "red green blue", "Set object color.");
        Add("Object3DSetActionPriority Object3DSetPriority", "priority", "Set the current object's priority.");
        Add("CycleTextureSetOn", "frames", "Enable a texture cycle with this frame count on the selected model.");
        Add("CycleTextureSetSpeed", "framesPerSecond", "Set the selected texture cycle's speed.");
        Add("CycleTextureSetLooping", "enabled", "Set texture-cycle looping.");
        Add("CycleTextureSetMap TextureAdd WriteTextureSetMap", "texture", "Register a texture by basename. The engine's runtime texture name is limited to 19 characters before the first dot.");
        Add("LensFlareTexture", "slot texture", "Register a lens-flare texture for this slot.");
        Add("WriteTextureSetType", "slot", "Choose the damage-mask texture slot.");
        Add("SetMission", "mission", "Legacy GameGen mission setting; runtime mission selection is controlled by the game.", true);
        Add("SetSmallPolygonRejectArea SetInverseZTolerance SetPerspectiveInverseZTolerance SetPerspectiveTextureFarZ SetBFETolerance SetCoplanarTolerance SetColinearTolerance WeaponSetMaxTetherAltitude", "value", "Set the named engine/build scalar. Integer-looking script numbers are accepted as numeric text.");
        Add("SetPerspectiveTextureDeltaX", "minimum maximum step", "Configure perspective texture correction steps.");
        Add("SetPaletteName", "palette", "Legacy palette selection. The source texture build uses the selected build profile.");
        Add("SetKeyboardInput", "enabled", "Legacy interpreter input setting.", true);
        Add("LoadSoils", "resource", "Load soil definitions from the selected resource member.");
        Add("MatlNew ModelNew", "name", "Create a material or model. Geometry-authoring effects require a supported analysis context.");
        Add("MatlTexture", "texture", "Assign a texture to the current material.");
        Add("MatlFaceColor", "red green blue", "Set material face color.");
        Add("ModelPolygonVertex", "x y z", "Add a polygon vertex.");
        Add("ModelPolygonUV", "u v", "Add polygon texture coordinates.");
        Add("ModelPolygonBegin ModelPolygonEnd", "", "Begin/end a procedural polygon.");
        Add("LODSetRange", "near far", "Set a level-of-detail range.");
        Add("SEQSetRepeat", "count", "Set sequence repetition.");
        Add("Object3DSetTextureWorldBaseCoordinates Object3DSetTextureWorldTexturesPerMeter", "x z", "Set world texture mapping coordinates or density.");
        Add("Object3DSetMorphVertex", "factor", "Set morph geometry parameters.");
        Add("CameraGetTranslate Object3DGetTranslate PrintTree CountCameraNodes WorldGetFogRange", "", "Interpreter inspection command.", true);
        Add("WorldSetFogRangeNear WorldSetFogRangeFar", "distance", "Set one fog-range bound.", true);
        // The original GameGen tool is unavailable; do not turn plausible signatures into asserted operand contracts.
        Add("GenericTerrainMorph", "", "Legacy GameGen terrain operation. Operand roles and name effects are unverified; dependent name analysis is incomplete.", true);
        Add("NewNode MatlNew ModelNew ModelPolygonBegin ModelPolygonEnd ModelPolygonVertex ModelPolygonUV", "",
            "Recognized procedural authoring command. Operand roles are not verified; unsupported world effects are reported by analysis.");
        return known.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
    }
    internal static GameGenCommand? Command(string token)
    {
        // Builtins precede core dispatch. Longer core prefixes must retain the retail dispatch order.
        foreach (string builtin in new[] { "endif", "ifdef", "ifndef", "mkdir", "set", "source", "who", "var" })
            if (token.StartsWith(builtin, StringComparison.Ordinal)) return Commands.First(c => c.Name == builtin);
        string resolved = ScriptCommands.Core(token);
        if (resolved != token) return Commands.FirstOrDefault(c => c.Name == resolved);
        return Commands.FirstOrDefault(c => c.Name == token)
            ?? Commands.Where(c => !c.Exact && token.StartsWith(c.Name, StringComparison.Ordinal)).OrderByDescending(c => c.Name.Length).FirstOrDefault();
    }
    internal static LanguageDocument Analyze(string path, string text, CancellationToken token)
    {
        var doc = new LanguageDocument(path, text) { Family = "gamegen" };
        if (text.Length > SourceProject.MaximumSourceTextBytes) { doc.Report("RECOIL_LIMIT", "information", "Document exceeds the 16 MiB analysis limit.", new(0, 0)); return doc; }
        int invalid = text.AsSpan().IndexOfAnyExceptInRange('\u0001', '\xFF');
        if (invalid >= 0) doc.Report("RECOIL_ENCODING", "error", "GameGen source must be Latin-1 text without NUL.", new(invalid, 1));
        try
        {
            if (GameGenScriptText.CountTokens(text, token) > RecoilLanguage.MaximumTokens)
            { doc.Report("RECOIL_LIMIT", "information", "Script token limit exceeded; analysis is incomplete.", new(0, 0)); return doc; }
            doc.Script = GameGenScriptSyntax.Parse(text, token);
            foreach (var line in doc.Script.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (doc.tokens.Count + line.Tokens.Count > RecoilLanguage.MaximumTokens) { doc.Report("RECOIL_LIMIT", "information", "Script token limit exceeded; analysis is incomplete.", new(line.Start, 0)); return doc; }
                for (int i = 0; i < line.Tokens.Count; i++) doc.tokens.Add(new(line.Spans[i], line.Tokens[i], i == 0 ? "command" : "operand"));
                if (!line.IsInstruction) continue;
                var command = Command(line.Tokens[0]);
                if (command == null) { doc.Report("GAMEZ_COMMAND", "warning", $"Unrecognized case-sensitive command '{RecoilLanguage.Shown(line.Tokens[0])}'; the interpreter may ignore it.", line.Spans[0]); continue; }
                string[] slots = command.Slots;
                if (slots.Length > 0)
                {
                    int required = slots.Count(p => !p.EndsWith('?') && !p.EndsWith("...", StringComparison.Ordinal));
                    if (line.Tokens.Count - 1 < required) doc.Report("GAMEZ_OPERANDS", "warning", $"{command.Name} expects {command.Parameters}; missing operands may default to zero or fail in the interpreter.", line.Spans[0]);
                    if (!slots[^1].EndsWith("...", StringComparison.Ordinal) && line.Tokens.Count - 1 > slots.Length)
                        doc.Report("GAMEZ_OPERANDS", "warning", $"{command.Name} does not consume these extra operands.", line.Spans[slots.Length + 1]);
                }
                for (int i = 1; i < line.Tokens.Count && i <= slots.Length; i++)
                {
                    string role = slots[i - 1].TrimEnd('?', '.');
                    doc.Arguments.Hints[line.Spans[i].Start] = new(line.Spans[i], role + ":", command.Help);
                    if (role == "enabled" && !line.Tokens[i].Contains('%') && !new[] { "on", "off", "true", "false" }.Contains(line.Tokens[i], StringComparer.OrdinalIgnoreCase))
                        doc.Report("GAMEZ_BOOLEAN", "warning", "Only on/true enables this setting; this operand disables it.", line.Spans[i]);
                    if (command.Name is not ("set" or "var") && NumericRoles.Contains(role) && !line.Tokens[i].Contains('%') &&
                        (!float.TryParse(line.Tokens[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number)))
                        doc.Report("GAMEZ_NUMBER", "warning", "Expected numeric text; the interpreter reads a leading number or defaults to zero.", line.Spans[i]);
                }
            }
            doc.Symbols = doc.Script.Lines.Where(l => l.IsInstruction && (Command(l.Tokens[0])?.Name is "set" or "source" or "LoadGameGen" or "NodeSetDescription" || l.Tokens[0].StartsWith("New", StringComparison.Ordinal) || l.Tokens[0] == "LightNew"))
                .Take(RecoilLanguage.MaximumResults).Select(l => new LanguageSymbol(string.Join(" ", l.Tokens), "property", new(l.Start, l.Length), l.Spans[0], [])).ToArray();
            doc.Complete = invalid < 0;
        }
        catch (InvalidDataException ex) { doc.Report("RECOIL_LIMIT", "information", ex.Message, new(0, 0)); }
        return doc;
    }
    private static readonly HashSet<string> NumericRoles = new("x y z xDegrees yDegrees zDegrees xScale yScale zScale horizontalDegrees verticalDegrees near far low high red green blue capacity frames framesPerSecond slot minimum maximum step value opacity intensity multiplier density priority distance count factor u v uRate vRate mission".Split(' '), StringComparer.Ordinal);
    internal static bool InComment(LanguageDocument doc, int offset)
    {
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        int start = offset == 0 ? 0 : doc.Text.LastIndexOf('\n', offset - 1) + 1;
        int hash = doc.Text.IndexOf('#', start, offset - start);
        return hash >= 0;
    }
    internal static (GameGenScriptLine? Line, int Index, TextSpan Span) Context(LanguageDocument doc, int offset)
    {
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        var line = doc.Script?.Lines.FirstOrDefault(l => l.Start <= offset && offset <= l.Start + l.Length);
        if (line == null) return (null, 0, new(offset, 0));
        for (int i = 0; i < line.Spans.Count; i++) if (line.Spans[i].Start <= offset && offset <= line.Spans[i].End) return (line, i, line.Spans[i]);
        for (int i = 0; i < line.Spans.Count; i++) if (offset < line.Spans[i].Start) return (line, i, new(offset, 0));
        return (line, line.Tokens.Count, new(offset, 0));
    }
    internal static IReadOnlyList<LanguageCompletion> Complete(LanguageDocument doc, int offset)
    {
        if (InComment(doc, offset)) return [];
        var (line, index, span) = Context(doc, offset);
        string prefix = doc.Text.Substring(span.Start, Math.Clamp(offset - span.Start, 0, span.Length));
        if (index == 0) return Commands.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(RecoilLanguage.MaximumResults)
            .Select(c => new LanguageCompletion(c.Name, c.Parameters, c.Name, Documentation: c.Help, ReplacementSpan: span)).ToArray();
        var command = line == null ? null : Command(line.Tokens[0]);
        string[] choices = command?.Slots.ElementAtOrDefault(index - 1) switch { "enabled" => ["on", "off"], "state" => ["linear", "exponential", "off"], "operation" => ["set", "="], _ => [] };
        return choices.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(c => new LanguageCompletion(c, command!.Name, c, "enumMember", command.Help, span)).ToArray();
    }
    internal static LanguageHover? Hover(LanguageDocument doc, int offset)
    {
        if (InComment(doc, offset)) return null;
        var (line, index, span) = Context(doc, offset);
        var command = line is { IsInstruction: true } ? Command(line.Tokens[0]) : null;
        return command == null ? null : new(command.Help + (index > 0 && index <= command.Slots.Length ? $"\n\nOperand: **{command.Slots[index - 1]}**." : ""), span);
    }
    internal static LanguageSignature? Signature(LanguageDocument doc, int offset)
    {
        if (InComment(doc, offset)) return null;
        var (line, index, _) = Context(doc, offset);
        var command = line is { IsInstruction: true } ? Command(line.Tokens[0]) : null;
        if (command == null || command.Slots.Length == 0 || index == 0) return null;
        return new(command.Name + " " + command.Parameters, command.Help,
            command.Slots.Select(p => new LanguageParameter(p, command.Help)).ToArray(), Math.Clamp(index - 1, 0, command.Slots.Length - 1));
    }
}
