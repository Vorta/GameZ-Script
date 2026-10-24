using GameZ.Script.Core.Formats;

namespace GameZ.Script.Core.Language;

public static partial class RecoilLanguage
{
    private static LanguageArgument A(string? label, string type, string documentation = "", string[]? choices = null, string? role = null) =>
        new(label, type, documentation, choices, Role: role);
    private static LanguageArgument Tuple(string label, params LanguageArgument[] children) => new(label, "a", Children: children);
    private static LanguageArgument[] XYZ(string type = "f", string suffix = "") =>
        [A("x" + suffix, type), A("y" + suffix, type), A("z" + suffix, type)];
    private static LanguageArgument[] RGB(string type = "i") => [A("red", type), A("green", type), A("blue", type)];
    private static LanguageArgument[] Roles(string shape, params string?[] names) =>
        shape.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((type, i) => A(i < names.Length ? names[i] : null, type)).ToArray();
    private static LanguageArgument[] WeaponRow() =>
    [
        A("weapon", "s", "Weapon catalog key. For the player tank, characters 4 and 6 select bank and side; wep_2.0 means bank 2, side 0.", role: "weapon"),
        A("mission threshold", "i", "Single player: available when nonzero and no greater than the mission number. Multiplayer uses its mission availability table."),
        A("dual mount", "i", "The low bit selects the weapon mount layout: 0 uses the single model; 1 uses the _L and _R model variants. Higher bits do not select the layout."),
        A("starting ammo", "i", "Initial ammunition/charge; read as an integer then converted to float."),
        A("repeat delay (s)", "f", "Delay between dispatches."),
        A("AI minimum range", "f", "Lower attack range supplied to the AI weapon selection."),
        A("AI maximum range", "f", "Upper attack range supplied to the AI weapon selection."),
        A("recoil flag", "i", "Stored firing-slot recoil flags. No unverified bit meanings are inferred."),
        A("initial hardpoint state", "i", "Initial weapon hardpoint selection state. No unverified enum names are inferred.")
    ];

    private static void BuildArguments(LanguageDocument doc, CancellationToken token)
    {
        if (doc.Root == null) return;
        if (doc.Family == "pickup-placements")
        {
            var root = doc.Root;
            while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array
                && root.Children[0].Children.FirstOrDefault()?.Kind == ZrdKind.Array) root = root.Children[0];
            foreach (var row in root.Children.Where(n => n.Kind == ZrdKind.Array))
                BindArguments(doc, row, "pickup", [A("pickup type", "s"), A("amount", "i", "Zero uses the pickup type's default amount."),
                    Tuple("position", XYZ()), Tuple("rotation", XYZ("f", " (rad)")), A("respawn delay", "f")], token);
            return;
        }
        foreach (var record in doc.Records)
        {
            token.ThrowIfCancellationRequested();
            if (record.Value == null || doc.Arguments.Owned.Contains(record.KeyNode.Id)) continue;
            string key = record.Key, parent = record.Parent?.Key ?? "";
            var path = Ancestors(record.Parent);
            void Bind(LanguageArgument[] specs, bool repeat = false, int count = int.MaxValue, bool claim = true) =>
                BindArguments(doc, record.Value, key, specs, token, repeat, count, claim,
                    fieldName: doc.Family == "placements" ? null : key);
            void Rows(LanguageArgument[] specs, int maximum = int.MaxValue)
            {
                Bind([new LanguageArgument("row", "a", Children: specs)], true, maximum);
            }
            if (doc.Family == "network" && record.Parent == null && NetworkNodeIndex(key) != null)
            {
                Bind([A("unused", "i", "The loader stores this integer, but RECOIL does not use it. The actual node index comes from node_00–node_98."),
                    Tuple("position", XYZ()), Tuple("links", Enumerable.Range(1, 3).Select(i => A("link " + i, "i",
                        "Index of another node in this network. Any negative value means absent; a missing nonnegative target can crash mission loading.", role: "network-link")).ToArray())]);
                continue;
            }
            if (doc.Family == "placements" && record.Parent == null)
            {
                Bind([A("network", "i", "Network file number; zero means no AI. An unknown network leaves the vehicle inert."),
                    Tuple("position", XYZ()), A("heading (deg)", "f")]); continue;
            }
            if (doc.Family == "vehicle" && parent == "common_mode")
            {
                if (key == "weapons") { Rows(WeaponRow()); continue; }
                if (key == "camback") { Bind([Tuple("camera offset", XYZ()), Tuple("camera point 2", XYZ()), Tuple("camera point 3", XYZ())]); continue; }
            }
            if (doc.Family == "vehicle" && path.Length == 2 && parent != "common_mode" && key is "platform" or "collision")
            { Rows(XYZ()); continue; }
            if (doc.Family == "animation" && key == "OBJECT_ACTIVE_LIST")
            {
                Bind([new LanguageArgument("node path", "a", Children: [A("node", "s", "Root followed by child node names; the final node must be active.")], Repeat: true)], true);
                continue;
            }
            if (doc.Family == "objectives" && record.Parent == null && key.StartsWith("OBJECTIVE", StringComparison.Ordinal)
                && int.TryParse(key.AsSpan(9), out _))
            {
                Bind(Roles("s s s s", "image", "title message", "description message", "completion message"), count: 4, claim: false); continue;
            }
            if (doc.Family == "startanims" && record.Parent == null && key is "NEW_GAME_START" or "LOAD_GAME_START")
            { Rows([A("animation", "s", "Animation to restart on this mission entry path.")]); continue; }
            if (doc.Family == "effects" && parent == "" && key == "EFFECTS")
            {
                foreach (var row in record.Values.Where(n => n.Kind == ZrdKind.Array))
                    BindArguments(doc, row, "effect", [A("model node", "s")], token, count: 1, claimRemainder: false, fieldName: key);
                continue;
            }
            if (doc.Family == "sounds" && parent == "" && key == "SETS")
            {
                foreach (var set in record.Values.Where(n => n.Kind == ZrdKind.Array))
                    foreach (var row in set.Children.Where(n => n.Kind == ZrdKind.Array))
                    {
                        if (SoundSyntax(doc) == 2)
                            BindArguments(doc, row, "sound", Roles("s s", "sample ID", "resource filename"), token, count: 2, claimRemainder: false, fieldName: key);
                        else if (SoundSyntax(doc) == 1)
                        {
                            var specs = new List<LanguageArgument>(Roles("s s", "sample ID", "resource filename"));
                            bool gain = row.Children.Count > 2 && row.Children[2].Kind == ZrdKind.Float;
                            if (gain) specs.Add(A("gain", "f"));
                            specs.AddRange([A("looping", "s", choices: ["TRUE", "FALSE"]), A(null, "s"), A(null, "s")]);
                            if (row.Children.Count == specs.Count + 2)
                                specs.AddRange([A("minimum distance", "f"), A("maximum distance", "f")]);
                            BindArguments(doc, row, "sound", specs.ToArray(), token, fieldName: key);
                        }
                    }
                continue;
            }
            if (doc.Family == "sounds" && parent == "" && key == "CD_TRACKS")
            { Rows(Roles("s i", "track name", "track number")); continue; }
            if (doc.Family == "hud" && parent == "" && BindHudArguments(doc, record, token)) continue;
            if (doc.Family == "ui" && BindUiArguments(doc, record, token)) continue;
            if (doc.Family == "weapons" && parent == "IMPACT" && key == "DAMAGE_ANIM_ON_HEALTH")
            { Rows(Roles("f s", "health threshold", "animation")); continue; }
            if (doc.Family == "weapons" && parent == "IMPACT" && key is "FREEZE" or "HEAT" or "DESIGNATE")
            {
                var specs = key == "DESIGNATE" ? Roles("f f f f f f", "minimum light range", "maximum light range", "red", "green", "blue", null)
                    : Roles("f f f f f f", "minimum light range", "maximum light range", "status update delay", "red", "green", "blue");
                if (record.Values.Count < 6) specs = specs.Select(s => s with { InactiveReason = "Ignored until all six operands are supplied." }).ToArray();
                Bind(specs);
                continue;
            }
            if (doc.Family == "turrets" && key == "PARTS" && path.Length == 2 && path[0] == "TURRET")
            {
                Bind(record.Values.Count switch
                {
                    2 => Roles("s s", "barrel", "firepoint"),
                    3 => Roles("s s s", "base", "barrel", "firepoint"),
                    4 => Roles("s s s s", "base", "barrel", "firepoint 1", "firepoint 2"),
                    _ => []
                }); continue;
            }
            var field = Field(doc.Family, key, parent, path);
            if (field == null) continue;
            if (!field.ValidateValues)
            {
                if (field.UsageWarning != null)
                    Bind([A("unused", "", field.UsageWarning)], repeat: true);
                continue;
            }
            if (doc.Family == "sounds" && key is "HIGH" or "MED" or "LOW" && record.Value.Kind == ZrdKind.String)
            { Bind([A("variant filename", "s", FieldDocumentation(field))]); continue; }
            var args = FieldArguments(doc, record, field);
            if (args.Length > 0)
            {
                bool repeat = (doc.Family, key) is ("effects", "MAPS") or ("objectives", "ACTIVE" or "INACTIVE") or ("weapons", "SOUND" or "BOUNCE_SOUND")
                    or ("turrets", "TARGETS" or "DEACTIVATE") || doc.Family == "animation"
                    && (key == "ANIMATION_LIST" && path.Contains("ACTIVATION_PREREQUISITE") || key == "NAME" && parent == "ANIMATION_DEFINITION");
                Bind(args, repeat, count: doc.Family == "turrets" && key == "TARGETS" ? 8 : int.MaxValue);
            }
        }
    }

    private static LanguageArgument[] FieldArguments(LanguageDocument doc, LanguageRecord record, LanguageField field)
    {
        string key = field.Name, parent = record.Parent?.Key ?? "";
        string[] shape = field.Shape.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(s => s != "...").ToArray();
        if (doc.Family == "weapons" && key is "SOUND" or "BOUNCE_SOUND" && field.Shape == "...") shape = ["s"];
        if (shape.Length == 0) return [];
        string?[]? names = doc.Family == "animation" ? AnimationArgumentNames(key, parent) : (doc.Family, key) switch
        {
            ("player", "mouse_push") => ["horizontal response", "vertical response"],
            ("player", "fp_cam_el_lim") => ["minimum elevation (rad)", "maximum elevation (rad)"],
            ("player", "underwater_cam") => ["distance", "height", "steps", "far distance", "red", "green", "blue", "alpha"],
            ("player", "low_shield_snd") => ["warning sample", "interval (s)", "looping sample"],
            ("vehicle", "health") => ["single-player health", "multiplayer health"],
            ("vehicle", "nanite") => ["build rate", "maximum level"],
            ("vehicle", "aimy") => ["yaw rate", "maximum yaw"],
            ("vehicle", "camera_ud_swing") => ["elevation limit scale", null, null, null],
            ("vehicle", "track_switch") => ["track distance 1", "track distance 2", "track distance 3"],
            ("vehicle", "pickups") => ["pickup type", "capacity"],
            ("vehicle", "rates") => ["acceleration", "maximum speed"],
            ("vehicle", "turns") => ["yaw acceleration", "maximum yaw rate"],
            ("vehicle", "rate_damping") => ["acceleration damping", "deceleration damping"],
            ("vehicle", "friction") => ["static friction", "dynamic friction", "sliding friction"],
            ("vehicle", "alt_control") => ["lift damping rate", "lift scale", "normal interpolation rate"],
            ("vehicle", "chas_pitch") => ["pitch rate", "maximum pitch", "pitch damping"],
            ("vehicle", "chas_roll") => ["roll rate", "maximum roll", "roll damping"],
            ("vehicle", "gun_pitch") => ["minimum pitch", "pitch rate"],
            ("vehicle", "amphib_wave" or "hover_wave" or "sub_wave") => ["pitch base rate", "pitch speed rate", "pitch amplitude", "roll base rate", "roll speed rate", "roll amplitude", "roll/yaw coupling"],
            ("vehicle", "collision_d") => [null, null],
            ("network", "hide_times") => ["unused", "unused"],
            ("network", "pursuit_params" or "pursuit_range") => PursuitNames(doc),
            ("weapons", "BEAM") => ["reciprocal timing", "interpolation rate", "low-bit flag"],
            ("weapons", "SOUND" or "BOUNCE_SOUND") => ["sample"],
            ("weapons", "CRATER" or "QUICKSAND") => ["minimum radius", "maximum radius"],
            ("weather", "COLOR") or ("ui", "COLOR") => ["red", "green", "blue"],
            ("weather", "ALPHA_GRADIENT") => ["start alpha", "end alpha"],
            ("objectives", "READ_SOUND") => ["sample", "delay (s)"],
            ("objectives", "ACTIVE" or "INACTIVE") => ["node"],
            ("effects", "MAPS") => ["texture"],
            ("effects", "NAME") => ["effect template"],
            ("sounds", "HIGH" or "MED" or "LOW") => ["sample rate (Hz)", "bits per sample", "channels"],
            ("sounds", "RANGE") => ["minimum distance", "maximum distance"],
            ("hud", "RETICULE") => ["range image", "off image", "on image"],
            ("hud", _) when parent == "FONTS" => ["font family", "height", "width", "weight"],
            ("ui", "POSITION" or "CENTER" or "TEXT_OFFSET" or "ENDP_REL" or "ENDP_ABS") => ["x (px)", "y (px)"],
            ("ui", "WORDWRAP") => ["width (px)", "height (px)"],
            ("ui", "MOUSE_RECT") => ["top offset (px)", "left offset (px)", "height (px)", "width (px)"],
            ("ui", "LISTSIZE") => ["total entries", "visible entries"],
            ("ui", "BITMAP") => ["image", "x (px)", "y (px)"],
            ("turrets", "EFFECT") => ["effect node", "duration (s)"],
            ("turrets", "NAME") when parent == "WEAPON" => ["weapon definition"],
            ("turrets", "FIRE_LIMITS") => ["burst duration (s)", "post-burst cooldown (s)"],
            ("turrets", "TARGETS" or "DEACTIVATE") => ["node"],
            _ => null
        };
        if (doc.Family == "vehicle" && key.EndsWith("_anims", StringComparison.Ordinal))
            names = ["transition animation 1", "transition animation 2"];
        if (names == null && shape.Length == 1)
            names = [field.Name.ToLowerInvariant().Replace('_', ' ')];
        string help = FieldDocumentation(field);
        var result = shape.Select((type, i) => A(names != null && i < names.Length ? names[i] : null,
            type, help, i == 0 ? field.Choices : null)).ToArray();
        if (doc.Family == "network")
        {
            if (key == "pursuit_range" && doc.Records.Any(r => r.Parent == null && r.Key == "pursuit_params"))
                result = result.Select(s => s with { Label = "unused", Documentation = "pursuit_params takes precedence over pursuit_range." }).ToArray();
            if (key == "type") result[0] = result[0] with { Choices = ["standard", "hide", "fixed", "defilade"] };
            if (key == "attack_strategy") result[0] = result[0] with { Choices = ["FOLLOW_NET", "CIRCLE", "HEAD_ON", "BACK_AND_FORTH", "ZIGZAG", "SIT"] };
        }
        if (doc.Family == "animation")
        {
            int count = record.Scalars.Count();
            if (key is "XYZ_ROTATION" or "SCALE" && count < 6)
                result = result.Select((s, i) => i >= 3 ? s with { InactiveReason = "Acceleration is ignored until the complete six-value form is supplied." } : s).ToArray();
            if (key == "AT_NODE")
                result = result.Select((s, i) => i > 0 && (i < 4 ? count < 4 : count < 7)
                    ? s with { InactiveReason = "This vector is ignored until its complete XYZ triple is supplied." } : s).ToArray();
            if (key == "GRAVITY" && parent == "OBJECT_MOTION" && result.Length > 1 && record.FirstText != "LOCAL")
                result[1] = result[1] with { Label = "unused", Documentation = "Only LOCAL gravity reads the following magnitude." };
            if (key == "VALUE" && parent == "CALLBACK") result[0] = result[0] with { Role = "callback" };
            if (key is "OPACITY_FROM" or "OPACITY_TO" && result.Length > 1) result[1] = result[1] with { Choices = ["ON", "OFF"] };
        }
        return result;
    }

    private static string?[] PursuitNames(LanguageDocument doc)
    {
        string strategy = doc.Records.FirstOrDefault(r => r.Parent == null && r.Key == "attack_strategy")?.FirstText ?? "HEAD_ON";
        // Retail compares the first three characters using uppercase literals; lowercase is an unrecognized value.
        return strategy.Length >= 3 ? strategy[..3] switch
        {
            "CIR" => ["orbit range", "unused"],
            "BAC" => ["attack range", "lateral movement"],
            "FOL" or "ZIG" or "SIT" => ["unused", "unused"],
            _ => ["minimum pursuit range", "maximum pursuit range"]
        } : ["minimum pursuit range", "maximum pursuit range"];
    }

    private static string?[]? AnimationArgumentNames(string key, string parent) => key switch
    {
        "ANIMATION_ROOT_NAME" => ["attachment node"],
        "RUN_TIME" => ["duration (s)"],
        "SCRIPT_FRAME_RATE" => ["frames per second"],
        "HEALTH" when parent == "ANIMATION_DEFINITION" => ["damage threshold"],
        "EXECUTION_BY_RANGE" => ["maximum distance (world units)"],
        "WITH_NODE" => ["position reference"],
        "OPERAND_NODE" => ["recipient node"],
        "START_TIME" => ["clock", "offset (s)"],
        "RESET_TIME" => ["delay (s)", "cleanup marker"],
        "GRAVITY" when parent != "ANIMATION_DEFINITIONS" => ["gravity source", "local magnitude"],
        "TRANSLATION" or "TRANSLATION_RANGE_MIN" or "TRANSLATION_RANGE_MAX" => ["yaw (deg)", "pitch (deg)", "speed", "acceleration"],
        "XYZ_ROTATION" => ["x rate (deg/s)", "y rate (deg/s)", "z rate (deg/s)", "x acceleration (deg/s²)", "y acceleration (deg/s²)", "z acceleration (deg/s²)"],
        "SCALE" => ["x rate", "y rate", "z rate", "x acceleration", "y acceleration", "z acceleration"],
        "FORWARD_ROTATION" => ["unused", "pitch offset (deg)", "roll offset (deg)"],
        "TRANSLATE_FROM" or "TRANSLATE_TO" or "SCALE_FROM" or "SCALE_TO" or "FROM_POS" or "TO_POS" or "RELATIVE" => ["x", "y", "z"],
        "ROTATE_FROM" or "ROTATE_TO" => ["x (deg)", "y (deg)", "z (deg)"],
        "STATE" when parent == "OBJECT_ACTIVE_STATE" => ["state"],
        "STATE" when parent == "OBJECT_OPACITY_STATE" => ["alpha override", "opacity"],
        "STATE" when parent == "OBJECT_ROTATE_STATE" => ["x (deg)", "y (deg)", "z (deg)"],
        "STATE" => ["x", "y", "z"],
        "OPACITY_FROM" or "OPACITY_TO" => ["opacity", "alpha override"],
        "AT_NODE" => ["position reference", "x offset", "y offset", "z offset", "x rotation (deg)", "y rotation (deg)", "z rotation (deg)"],
        "PARENT_CHILD" => ["parent node", "child node"],
        "COLOR" => ["red", "green", "blue"],
        "FROM" or "TO" => ["red", "green", "blue", "alpha"],
        "RANGE" when parent == "LIGHT_ANIMATION" => ["inner rate", "outer rate", "inner acceleration", "outer acceleration"],
        "RANGE" when parent == "FOG_STATE" => ["near distance", "far distance"],
        "RANGE" => ["inner distance", "outer distance"],
        "ALTITUDE" => ["lower altitude", "upper altitude"],
        "H_ZOOM_FROM_TO" or "V_ZOOM_FROM_TO" => ["start zoom", "end zoom"],
        "NODE_UNDERCOVER" => ["node", "probe distance"],
        "SCREEN_POS_FROM" or "SCREEN_POS_TO" => ["x", "y"],
        "CSIN_FROM" or "CSIN_TO" => [null, null, null],
        "NAME" when parent == "ANIMATION_DEFINITION" => ["root node"],
        "NAME" when parent == "SEQUENCE_DEFINITION" => ["sequence"],
        "NAME" when parent is "CALL_SEQUENCE" or "STOP_SEQUENCE" => ["target sequence"],
        "NAME" when parent is "CALL_ANIMATION" or "STOP_ANIMATION" or "RESET_ANIMATION" or "INVALIDATE_ANIMATION" or "CALL_OBJECT_CONNECTOR" => ["animation"],
        "NAME" when parent is "SOUND" or "BOUNCE_SOUND" => ["sound sample"],
        "NAME" when parent == "SOUND_NODE" => ["sound sample / node"],
        "NAME" when parent == "EFFECT" => ["effect template"],
        "NAME" when parent is "LIGHT_STATE" or "LIGHT_ANIMATION" => ["light node"],
        "NAME" when parent is "CAMERA_STATE" or "CAMERA_FROM_TO" => ["camera node"],
        "NAME" when parent is "OBJECT_ACTIVE_STATE" or "OBJECT_TRANSLATE_STATE" or "OBJECT_SCALE_STATE" or "OBJECT_ROTATE_STATE"
            or "OBJECT_MOTION" or "OBJECT_MOTION_FROM_TO" or "OBJECT_MOTION_SI_SCRIPT" or "OBJECT_OPACITY_STATE"
            or "OBJECT_OPACITY_FROM_TO" or "OBJECT_CYCLE_TEXTURE" or "OBJECT_CONNECTOR" => ["target node"],
        "NAME" => ["name"], "ANIMATION_LIST" => ["animation"],
        "VALUE" when parent == "CALLBACK" => ["event ID"],
        _ => null
    };
}
