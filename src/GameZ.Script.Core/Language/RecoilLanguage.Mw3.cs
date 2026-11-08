using System.Globalization;
using GameZ.Script.Core.Animation;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;

namespace GameZ.Script.Core.Language;

public static partial class RecoilLanguage
{
    private static bool NativeBareScalar(string family, string key) => family == "fmv" && key is "WAIT" or "BLUR" or "PLAYAVI" or "LOADIMAGE" or "PLAYSOUND"
        || family == "ui" && key is "FONT" or "SELECTED_FONT" or "LIST_FONT" or "LISTWIDTH" or "SPACING";
    // Profiles select consumer contracts. Shared token syntax does not imply shared positional layouts.
    public static GameVariant DetectDocumentGame(string text, CancellationToken token = default)
        => TryDetectDocumentGame(text, token) ?? GameVariant.Recoil;

    /// <summary>Only positive source markers establish a game; absence of a marker is unknown.</summary>
    public static GameVariant? TryDetectDocumentGame(string text, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        token.ThrowIfCancellationRequested();
        if (text.Length > SourceProject.MaximumSourceTextBytes) return null;
        ZrdNode root;
        int depth = 0;
        try { root = ZrdText.Parse(text, token, null, delimiter: (kind, _) => depth += kind == '(' ? 1 : -1); }
        catch (InvalidDataException)
        {
            if (depth is <= 0 or > 256) return null;
            try { root = ZrdText.Parse(text + "\n" + new string(')', depth), token); }
            catch (InvalidDataException) { return null; }
        }
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        foreach (var pair in NativePairs(root, token))
        {
            if (pair.Key == "version" && pair.Value.Children.FirstOrDefault() is { Kind: ZrdKind.Int } version
                && (version.Bits is 106 or 107 or 108 || version.Bits == 105 && pair.Value.Children.Skip(1).Any(n => n.Kind == ZrdKind.String && n.Text == "mech3")))
                return GameVariant.MechWarrior3;
            if (pair.Key == "MOTION" && NativePairs(pair.Value, token).Select(p => p.Key).ToHashSet(StringComparer.Ordinal) is var tags
                && tags.Contains("FRAME_COUNT") && tags.Contains("LOOP_SECONDS")) return GameVariant.MechWarrior3;
            if (pair.Key == "ANIMATION_DEFINITIONS" && NativeAnimationMarker(pair.Value, token)) return GameVariant.MechWarrior3;
        }
        return null;
    }
    private static IEnumerable<(string Key, ZrdNode Value)> NativePairs(ZrdNode node, CancellationToken token)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (node.Children[i].Kind == ZrdKind.Array) foreach (var pair in NativePairs(node.Children[i], token)) yield return pair;
            else if (node.Children[i].Kind == ZrdKind.String && i + 1 < node.Children.Count && node.Children[i + 1].Kind == ZrdKind.Array)
                yield return (node.Children[i].Text, node.Children[++i]);
        }
    }
    private static bool NativeAnimationMarker(ZrdNode node, CancellationToken token)
    {
        foreach (var pair in NativePairs(node, token))
            if (pair.Key is "PUFFER_STATE" or "DETONATE_WEAPON" || NativeAnimationMarker(pair.Value, token)) return true;
        return false;
    }

    private static string Mw3Family(LanguageDocument doc)
    {
        string name = Path.GetFileNameWithoutExtension(doc.Path).ToLowerInvariant();
        if (name.StartsWith("dfn_", StringComparison.Ordinal)) return "mech-definition";
        if (name.StartsWith("reg_", StringComparison.Ordinal)) return "region";
        if (name.StartsWith("net_", StringComparison.Ordinal)) return "network";
        if (name.StartsWith("waynet", StringComparison.Ordinal)) return "waypoint-network";
        if (name.StartsWith("dlg", StringComparison.Ordinal)) return "ui";
        return name switch
        {
            "aiv" => "placements", "aiturrets" => "turrets", "valves" => "valves", "targets" => "targets",
            "mechs" => "mechs", "mechlib" or "mechlibdemo" => "mech-library", "equipment" => "equipment",
            "world" => "world", "objectives" => "objectives", "location" => "location", "soils" => "soils",
            "detail" => "detail", "maps" => "maps", "fmv" => "fmv", "intensity" => "intensity", "declient" => "declient",
            "briefing" => "briefing", "camera" => "camera",
            "landmines" => "landmines", "mobilerepair" => "mobile-repair", "mpinfo" => "multiplayer",
            _ => doc.Family
        };
    }

    internal static LanguageField? Field(LanguageDocument doc, LanguageRecord record) =>
        Field(doc, record.Key, record.Parent?.Key ?? "", Ancestors(record.Parent));
    internal static LanguageField? Field(LanguageDocument doc, string name, string parent, IReadOnlyList<string>? context = null) =>
        !doc.HasGameProfile ? null : doc.Game == GameVariant.MechWarrior3 ? Mw3Fields(doc, parent, context ?? []).FirstOrDefault(f => f.Name == name)
            : Field(doc.Family, name, parent, context);

    private sealed record NativeSpec(string Names, string Shape, string Help, string?[] Labels, string? Reference = null, string[]? Choices = null);
    private static NativeSpec N(string names, string shape, string help, string? labels = null, string? reference = null, string[]? choices = null) =>
        new(names, shape, help, labels?.Split('|') ?? [], reference, choices);
    private static IEnumerable<LanguageField> NativeFields(IEnumerable<NativeSpec> specs)
    {
        foreach (var spec in specs)
            foreach (string name in spec.Names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return new(name, spec.Help, spec.Shape, spec.Choices, spec.Reference, Arguments: string.Join(", ", spec.Labels));
    }

    private static readonly NativeSpec[] NetHeader = [
        N("version", "i s?", "MW3 network format. Versions 105–108 are recognized; 105 with mech3 selects the native format.", "format version|game marker"),
        N("name type", "s", "Network or region identity."), N("netparams", "", "Network-wide properties."),
        N("pos", "f f f", "Stored network origin.", "x|y|z")];
    private static readonly NativeSpec[] NetProperties = [
        N("speed", "i f", "Enable an explicit movement speed on this node.", "enabled|speed"),
        N("canleave", "i i", "Node leave/continuation controls.", "enabled|state"),
        N("wait_interval wait_time", "i f", "Enable a timed wait at this node.", "enabled|wait (s)"),
        N("valve", "i s i?", "Enable a valve condition; the optional last integer is the required value.", "enabled|valve|required value", "valve"),
        N("name", "s", "Localized node label.", reference: "message"), N("auxname", "s", "Auxiliary unlocalized node label."),
        N("jump", "i f", "Enable the node's jump parameter.", "enabled|jump parameter"),
        N("scan_aim scan_time scan_hit", "i i", "Enable this AI scan control.", "enabled|scan value"),
        N("hidden fixed autonext visible", "i", "Native node state switch."),
        N("deploy", "s i s i i", "Deploy control: two valve/state pairs followed by a node index.", "first valve|first state|second valve|second state|node", "valve"),
        N("mcbwait", "s i f", "Wait for the mobile-field-base valve/state and timeout.", "valve|state|timeout (s)", "valve"),
        N("arrangenodes", "f", "Spacing supplied to node arrangement.", "spacing"),
        N("sound", "s", "Sound played at the node.", "sample", "sound"),
        N("backup shunt", "", "Presence flag for native node routing."), N("gate", "s", "Region gate name.", "region", "region"),
        N("valveunion", "", "Ordered valve conditions; each row is enabled, valve, optional count."),
        N("valve_assign", "s i", "Assign a value to this valve.", "valve|value", "valve")];

    private static readonly NativeSpec[] NativeMotion = [
        N("BOUNCE_SEQUENCE_WATER BOUNCE_SEQUENCE_LAVA", "s", "Sequence called on water/lava contact.", "collision sequence", "sequence"),
        N("BOUNCE_SOUND_WATER BOUNCE_SOUND_LAVA", "", "Sound and full-volume velocity for water/lava contact."),
        N("IMPACT_FORCE", "s", "Enable transfer of impact force.", "enabled", choices: ["ON", "OFF"]),
        N("GRAVITY", "s x? x?", "MW3 gravity mode: DEFAULT/GLOBAL, LOCAL magnitude, or COMPLEX. NO_ALTITUDE suppresses altitude contact.", "gravity mode|magnitude or altitude mode|altitude mode", choices: ["DEFAULT", "GLOBAL", "LOCAL", "COMPLEX", "NO_ALTITUDE"]),
        N("FORWARD_ROTATION", "s n n?", "Orient along motion by TIME or DISTANCE, with pitch and roll offsets in degrees.", "mode|pitch offset (deg)|roll offset (deg)")];
    private static readonly NativeSpec[] Puffer = [
        N("NAME", "s", "Puffer instance name.", "puffer"), N("ACTIVE_STATE", "s", "Set puffer emission active/inactive.", choices: ["ACTIVE", "INACTIVE"]),
        N("AT_NODE", "s n? n? n?", "Attach emission to this node, optionally at an XYZ offset.", "basis node|offset x|offset y|offset z", "node"),
        N("TRANSLATION LOCAL_VELOCITY WORLD_VELOCITY MIN_RANDOM_VELOCITY MAX_RANDOM_VELOCITY WORLD_ACCELERATION", "n n n", "Particle position, velocity or acceleration in the named coordinate space.", "x|y|z"),
        N("TIME_INTERVAL", "n", "Seconds between particle emissions.", "interval (s)"), N("DISTANCE_INTERVAL", "n", "Distance between particle emissions.", "distance"),
        N("SIZE_RANGE LIFETIME_RANGE START_AGE_RANGE FADE_IN_RANGE FADE_RANGE", "n n", "Minimum and maximum particle size or timing range; lifetime, age and fades use seconds.", "minimum|maximum"),
        N("DEVIATION_DISTANCE", "n", "Random emission position deviation.", "distance"), N("FRICTION WIND_FACTOR GROWTH_FACTOR", "n", "Particle motion or growth coefficient."),
        N("PARTICLES_PER_EMISSION", "n", "Number of particles per emission; converted to integer.", "particle count"),
        N("TEXTURES", "s? s? s? s? s? s?", "Up to six particle texture names.", "texture 1|texture 2|texture 3|texture 4|texture 5|texture 6", "texture")];

    private static IEnumerable<LanguageField> Mw3Fields(LanguageDocument doc, string parent, IReadOnlyList<string> path)
    {
        if (doc.Family == "animation")
        {
            IEnumerable<string> names = parent switch
            {
                "SEQUENCE_DEFINITION" or "RESET_STATE" => AnimationSourceFields.EventFields.Keys.Concat(["PUFFER_STATE", "DETONATE_WEAPON", "NAME", "ACTIVATION", "START_TIME"]),
                "PUFFER_STATE" => Puffer.SelectMany(s => s.Names.Split(' ')).Append("START_TIME"),
                "DETONATE_WEAPON" => ["WEAPON", "AT_NODE", "START_TIME"],
                "OBJECT_MOTION" => AnimationSourceFields.EventFields["OBJECT_MOTION"].Split(' ').Concat(NativeMotion.SelectMany(s => s.Names.Split(' '))).Append("START_TIME"),
                "LIGHT_STATE" => AnimationSourceFields.EventFields["LIGHT_STATE"].Split(' ').Concat(["TYPE", "STATIC", "SUBDIVIDE", "DIRECTIONAL", "AMBIENT", "DIFFUSE", "START_TIME"]),
                "IF" or "ELSEIF" => AnimationSourceFields.EventFields["IF"].Split(' ').Concat(["HW_RENDER", "SW_RENDER"]),
                "BOUNCE_SOUND_WATER" or "BOUNCE_SOUND_LAVA" => ["NAME", "FULL_VOLUME_VELOCITY"],
                _ => Fields("animation", parent, path).Select(f => f.Name)
            };
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                if (name is "PUFFER_STATE" or "DETONATE_WEAPON") { yield return new(name, name == "PUFFER_STATE" ? "Create or update a native particle emitter." : "Detonate a weapon at a node/offset."); continue; }
                var special = NativeFields(parent == "PUFFER_STATE" ? Puffer : parent == "OBJECT_MOTION" ? NativeMotion : []).FirstOrDefault(f => f.Name == name);
                if (special != null) { yield return special; continue; }
                if (name is "HW_RENDER" or "SW_RENDER") { yield return new(name, "Branch on the renderer mode."); continue; }
                if (parent == "LIGHT_STATE" && name is "STATIC" or "SUBDIVIDE" or "DIRECTIONAL") { yield return new(name, "Native light switch.", "s", ["TRUE", "FALSE", "ON", "OFF"]); continue; }
                if (parent == "LIGHT_STATE" && name is "AMBIENT" or "DIFFUSE") { yield return new(name, "Native light intensity.", "n"); continue; }
                if (parent == "LIGHT_STATE" && name == "TYPE") { yield return new(name, "Native light-source mode.", "s", ["POINT", "DIRECTED"]); continue; }
                if (parent == "DETONATE_WEAPON" && name == "WEAPON") { yield return new(name, "Weapon to detonate.", "s", Reference: "weapon"); continue; }
                if (name is "BOUNCE_SOUND_WATER" or "BOUNCE_SOUND_LAVA") { yield return new(name, "Contact sound settings for the indicated medium."); continue; }
                if (name is "BOUNCE_SEQUENCE_WATER" or "BOUNCE_SEQUENCE_LAVA") { yield return new(name, "Contact sequence for the indicated medium.", "s", Reference: "sequence"); continue; }
                if (name == "WITH_NODE") { yield return new(name, "Use the node as positional reference, with an optional complete XYZ offset.", "s n? n? n?", Reference: "node", Arguments: "position-reference node, offset x, offset y, offset z"); continue; }
                if (name == "AT_NODE_XYZ") { yield return new(name, "Orientation-reference node, with native optional XYZ values.", "s n? n? n?", Reference: "node"); continue; }
                if (name == "EXECUTION_BY_RANGE") { yield return new(name, "Native execution range and optional range scale.", "n n?"); continue; }
                if (name == "NAME" && parent == "ANIMATION_DEFINITION")
                { yield return new(name, "Root node names or digit-wildcard patterns. A trailing # expands each same-named world root; generated animation names receive instance suffixes.", "s ...", Reference: "node", Arguments: "root node"); continue; }
                yield return AnimationField(name, parent is "BOUNCE_SOUND_WATER" or "BOUNCE_SOUND_LAVA" ? "BOUNCE_SOUND" : parent, path);
            }
            yield break;
        }
        foreach (var field in NativeFields(Mw3Specs(doc.Family, parent, path))) yield return field;
    }

    private static IEnumerable<NativeSpec> Mw3Specs(string family, string parent, IReadOnlyList<string> path)
    {
        bool root = path.Count == 0;
        switch (family)
        {
            case "network": case "region": case "waypoint-network":
                return root ? NetHeader : parent == "netparams" ? [NetHeader[^1]] : parent.StartsWith("node_", StringComparison.Ordinal) ? NetProperties : [];
            case "placements": return path.Count == 1 ? AivSpecs : [];
            case "targets": return path.Count == 1 ? [
                N("health", "f", "Initial target health."), N("translate_to_anim_root", "", "Place at the animation root."),
                N("destroy_anim", "s", "Destruction animation.", "animation", "animation"), N("target centroid", "s a?", "Named target/centroid node with an optional XYZ offset.", "node|offset", "node"),
                N("name", "s", "Localized target display name.", reference: "message"), N("height", "n", "Target height."),
                N("health_below", "", "Ordered valve, value, health-threshold triples."), N("radar display reticule_select zoom_select", "x", "Native target selection/display switch.") ] : [];
            case "valves": return path.Count == 1 ? ValveSpecs : [];
            case "vehicle":
                if (parent == "common_mode") return VehicleCommon;
                if (parent == "sounds") return [N("collide land idle skid engine external", "s", "Vehicle sound alias.", "sample", "sound"), N("pitch_scale volume_scale", "f", "Vehicle sound scaling.")];
                if (parent == "weapons") return [N("firepoint", "s ...", "Named weapon hardpoints.", "hardpoint", "node")];
                if (path.Count == 2) return VehicleMode;
                return path.Count == 1 ? [N("common_mode basic_mode track_mode fly_mode hover_mode", "", "Vehicle mode configuration.")] : [];
            case "weapons": return WeaponSpecs(parent, path);
            case "sounds":
                if (root) return [N("SYNTAX", "i", "Sound definition syntax version."), N("SOUND_PATH", "s", "Sound search folders."), N("HIGH_DEFAULT MEDIUM_DEFAULT LOW_DEFAULT", "i i i", "Default wave format.", "sample rate (Hz)|bits per sample|channels"), N("PLAYBACK_FORMAT SETS CD_TRACKS DEMO_SETS", "", "Sound resource declarations.")];
                if (parent == "PLAYBACK_FORMAT") return [N("HIGH MEDIUM LOW", "i i i", "Playback wave format.", "sample rate (Hz)|bits per sample|channels")];
                return [N("VOLUME A3DDIST", "n", "Sound playback level/distance."), N("RANGE", "n n", "Sound attenuation range.", "minimum distance|maximum distance"), N("3D LOOPED FREQUENCY HARDWARE PURGEABLE VOICE", "", "Native sound playback flag.")];
            case "mech-library": return root ? [N("library model_path", "s", "Native model library or search folders."), N("models", "s ...", "Models in native library order.", "model", "model")] : [];
            case "mech-definition": return MechSpecs(parent, path);
            case "equipment": return path.Count == 1 ? [N("clan sphere", "x x?", "Technology-specific equipment mass and critical slots, or a symbolic classification.", "mass or classification|critical slots")] : [];
            case "world": return WorldSpecs(parent, path);
            case "multiplayer":
                if (root) return [N("db_name", "s", "Multiplayer scenario display name."), N("world_params objectives briefing", "", "Scenario environment, goals and front-end presentation.")];
                if (path.Contains("world_params")) return WorldSpecs(parent, path.SkipWhile(p => p != "world_params").ToArray());
                if (path.Contains("objectives")) return ObjectiveSpecs;
                if (parent == "SOUND") return [N("MARKERS", "", "Briefing text markers.")];
                if (parent == "briefing") return new[] { N("MAP", "s", "Briefing map resource.", "map"), N("SOUND", "", "Briefing sound alias with optional text markers.", "sample", "sound") }.Concat(UiSpecs(parent, path));
                return UiSpecs(parent, path).Concat([N("at", "n n", "Briefing marker coordinates.", "x (px)|y (px)")]);
            case "landmines": return root ? [N("weapon", "s", "Weapon detonated by a triggered mine.", "weapon", "weapon"), N("radius", "f", "Three-dimensional mine trigger radius in metres; the native reader uses a stored float.", "trigger radius (m)")] : [];
            case "detail": return root ? DetailSettings.Select(name => N(name, "", "Ordered hardware predicates and chosen option; DEFAULT supplies the fallback.")) : [];
            case "mechs": case "soils": return [];
            case "objectives": return ObjectiveSpecs;
            case "briefing": return [N("SOUND", "s ...", "Briefing sound aliases in playback order.", "sample", "sound"), N("TEXT", "s ...", "Localized briefing text.", "text", "message")];
            case "camera": return [N("camera_zone max_cam_yaw_rate fp_cam_el_rate camera_elastic max_cam_tether_angle", "n", "Native camera response setting."), N("mouse_push fp_cam_el_lim", "n n", "Camera axis response/limits.", "horizontal/minimum|vertical/maximum"), N("underwater_cam", "n n n n n n n n", "Native underwater camera settings use converting numeric accessors.", "distance|height|steps|far distance|red|green|blue|alpha")];
            case "fonts":
                if (root) return [N("FONTS WINDOWS_FONTS", "", "Bitmap and Windows named font declarations.")];
                return [N("face", "s", "Windows typeface name."), N("height width weight", "i", "Windows font size or weight."), N("color color2", "i i i", "Font RGB channels in the 0–255 scale.", "red|green|blue"), N("shadow italic", "x", "Windows font presentation switch.")];
            case "hud":
                if (root) return [N("V640x480 V800x600 V1024x768 V1152x864 V1280x1024 V1600x1200", "", "HUD layout for this video resolution.")];
                if (path.Count == 1) return [N("MFD TARGET WEAPONS EQUIPMENT COMPASS TIMER GAUGES", "", "Native HUD element layout.")];
                return [N("position tape frame speed_frame fuel_frame heat_bar heat_meter heat_text heat_arrow coolant_frame", "n n", "HUD element position in pixels; native resolution anchoring may move it with the display size.", "x (px)|y (px)"), N("size", "n n", "HUD element size in pixels.", "width (px)|height (px)"), N("width", "n", "HUD list width in pixels.", "width (px)")];
            case "maps": return [N("maps", "", "Map image declarations."), N("name", "s", "Map image identity."), N("bitmap", "s", "Map bitmap resource.", "image", "texture"), N("mpp", "n", "World units represented by one map pixel.", "units per pixel"), N("offset", "n n n", "World origin of the map image.", "x|y|z")];
            case "declient": return [N("VERSION", "n", "Terrain-deformation resource version."), N("CRATER QUICK_SAND DITCH TEXTURE_ANIM", "", "Terrain-deformation settings."), N("TEXTURE_PARMS", "n n", "Terrain texture scaling.", "u scale|v scale"), N("SLOPE RADIUS DEPTH WIDTH", "n", "Terrain-deformation geometry parameter."), N("POINTS", "i", "Deformation perimeter point count."), N("DEFAULT_ANIM", "s", "Default deformation animation.", "animation", "animation")];
            case "fmv": return [N("FMV_PATH IMAGE_PATH", "s", "Movie or image search folders."), N("WAIT", "n", "Pause for this many seconds.", "duration (s)"), N("BLUR", "n", "Blur strength."), N("PLAYAVI", "s", "Movie file.", "movie", "file"), N("LOADIMAGE", "s", "Image resource.", "image", "texture"), N("PLAYSOUND", "s", "Sound alias.", "sample", "sound"), N("FADEIN FADEOUT", "n n n", "Fade RGB color.", "red|green|blue")];
            case "intensity": return [N("low medium high", "n n n x x", "Difficulty multipliers and native presentation controls; the last flag disables heat haze. The first switch's consumer is unverified.", "AI action-delay scale|AI aim scale|damage scale||disable heat haze")];
            case "ui": return UiSpecs(parent, path);
            case "turrets": return TurretSpecs(parent, path);
            default:
                return (ResourceFields.GetValueOrDefault(family) ?? []).Select(f => new NativeSpec(f.Name, f.Shape, f.Description, [], f.Reference, f.Choices));
        }
    }

    private static readonly string[] DetailSettings = "VMode TextureMemory_SW TextureMemory_HW SoundAPI Transparency Lighting Perspective GlobalLight_HW GlobalLight_SW AllVideoBuffer SoundLOD ObjectLOD_HW ObjectLOD_SW EffectsLevel_HW EffectsLevel_SW ShadowParts_SW ShadowParts_HW CameraMode Invincible InfiniteAmmo HeatMgmt TorsoAutoRtn ThrottleConst UseJoystick MW2JJets FastMenu".Split(' ');

    private static readonly NativeSpec[] VehicleCommon = [
        N("position rotation", "f f f", "Vehicle initial pose.", "x|y|z"), N("tube", "f f", "Vehicle tube dimensions.", "length|radius"),
        N("health", "f", "Vehicle health."), N("motion_anim", "s", "Vehicle motion animation.", "animation", "animation"),
        N("name", "s", "Vehicle display name."), N("activation not_pursuit_dwell return_range", "f", "Vehicle AI activation or pursuit setting."),
        N("pursuit_range", "f f", "AI pursuit distances.", "minimum range|maximum range"), N("aimy", "f f", "Aim yaw response.", "yaw rate|maximum yaw"),
        N("camback weapons sounds", "", "Vehicle camera, weapon or sound configuration.")];
    private static readonly NativeSpec[] VehicleMode = [
        N("mode", "s", "Locomotion mode."), N("platform collision", "", "Local XYZ contact points."),
        N("rates", "f f", "Vehicle acceleration and speed.", "acceleration|maximum speed"), N("turns", "f f", "Vehicle yaw response.", "yaw acceleration|maximum yaw rate"),
        N("rate_damping collision_d", "f f", "Native movement damping pair."), N("turn_damping mode_alt mass a_damping", "f", "Native movement coefficient."), N("ok_to_amphib", "i", "Amphibious transition switch.")];
    private static readonly NativeSpec[] AivSpecs = [
        N("params", "f i f f f f f f f", "Native AI sight/attack settings. Ranges are stored unsquared in the source.", "sight range|may engage|attack range|home range|||||"),
        N("p2", "i i i i f f f f", "Native engagement strategy and break-off controls; unverified coefficients retain no invented labels.", "attack strategy|limit home range|limit attack time|test health floor|||attack duration (s)|health floor"),
        N("p3", "f f f f f f", "Legacy scan/movement parameters; p8 replaces this form."),
        N("p4", "x x x x x x x x x x x x x x x x x x x x x x x", "Legacy inline AI trigger and scan settings; p8/vlist replace this form."),
        N("p5", "f f i i i", "Legacy jump/order settings; p8 replaces this form."),
        N("p6", "i f i i", "Native activation controls.", "activation control||delayed engaging|"),
        N("p7", "i i f f f f f f", "Legacy scan/order settings; p8 replaces this form."),
        N("p8", "f f f f i f f f f i i f f i i i i", "Native scan, movement and combat controls.", "minimum idle wait (s)|maximum idle wait (s)|minimum manoeuvre time (s)|maximum manoeuvre time (s)|run own order|nearby vehicle range||||always sees target|select player contact|||scan aim|scan hit|scan time|manoeuvre mode"),
        N("p9", "f f f i f", "Native combat timers and health-loss limit.", "aim manoeuvre interval (s)||hit reaction interval (s)|limit health loss|health loss limit"),
        N("p10", "f i", "Native movement/AI settings."), N("p11", "f", "Native movement/AI coefficient."),
        N("p12", "s i i", "Mech definition and native AI state flags.", "mech|state flag 1|state flag 2", "mech"),
        N("p13 skin", "i", "Native actor selection/switch."), N("sound_tag", "s", "Actor radio voice prefix."),
        N("pilot", "s ...", "Pilot name and optional configuration settings.", "pilot", "message"), N("lancemate", "x", "Lancemate tristate selection."),
        N("mech3", "i", "Native actor switch."), N("vlist", "", "Ordered AI valve conditions and state controls.")];
    private static readonly NativeSpec[] ValveSpecs = [
        N("sound sound_once", "s i?", "Play a sound alias, with optional native playback selector.", "sample|selector", "sound"),
        N("animation animation_once", "s i", "Invoke an animation for the matching valve value.", "animation|value", "animation"),
        N("shutdown", "s i i", "Set an actor's powered state using the specified condition/value.", "actor|state|condition", "node"),
        N("delayupdate", "s f", "Schedule a valve update after a delay.", "valve|delay (s)", "valve"),
        N("update", "s", "Request an update of the named valve.", "valve", "valve"),
        N("destroy", "s i", "Destroy the actor for the matching valve value.", "actor|value", "node"), N("setteam", "s i i", "Set an actor's team for the matching valve value.", "actor|team|value", "node"),
        N("attack_strategy", "s i i", "Set an actor's native attack strategy for the matching valve value.", "actor|strategy|value", "node"),
        N("scan_aim scan_time scan_hit canleave", "s i i", "Set an actor AI control.", "actor|enabled|value", "node"),
        N("namelist", "s ...", "Valve names read by the selected compound condition.", "valve", "valve"),
        N("all_zero all_nonzero any_zero any_nonzero", "", "Native compound valve condition."),
        N("missioncomplete missionabort missionreset value", "i", "Native mission action or initial valve value."),
        N("teleport", "x a n i", "Teleport actor to XYZ and heading for the matching valve value.", "actor|position|heading (deg)|value", "node")];

    private static IEnumerable<NativeSpec> WeaponSpecs(string parent, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return [N("VERSION", "i", "Weapon-definition version."), N("BALLISTICS", "", "Weapon definitions."), N("MAX_CRATER_RADIUS", "n", "Maximum crater radius.")];
        if (parent == "BEAM") return [N("TIME_TO_MAX_RANGE ENERGY DAMAGE_AT_MAX_RANGE", "n", "Beam timing, energy or far-range damage coefficient."), N("BOUNCE TIMED_DAMAGE", "i", "Beam behavior switch."), N("PULSE", "f i f", "Beam pulse period, native switch and coefficient.", "pulse period (s)|pulse switch|pulse coefficient")];
        if (path.Count == 2 && path[0] == "BALLISTICS") return [
            N("ACCELERATION AMMO_LIMIT DAMAGE FIRE_RATE TURN_RATE TURN_SUSPEND_TIME PITCH_RATE MAX_CRATER_RADIUS", "n", "Native converting numeric weapon parameter."),
            N("RANGE VELOCITY GRAVITY IMPACT_PROXIMITY DETONATION_DISTANCE ANIMATION_MIN_RANGE", "f", "Native weapon distance, velocity or force parameter."),
            N("RANGE_MINIMUM", "f f", "Minimum range and native range coefficient.", "minimum range|coefficient"), N("U_LOOPED", "s", "Looping user-weapon sound alias.", "sample", "sound"),
            N("ISNARC", "x", "Native NARC marker boolean."), N("U_DESIGNATE_AIRSTRIKE", "s", "Weapon definition used by the designated airstrike.", "weapon", "weapon"),
            N("ZAPS_HUD", "n n", "HUD static strength and hold duration. Retail 1.2 reads the first value twice; the rebuild's bug fix uses the intended second hold value.", "static strength|hold (s)"),
            N("HEAT", "f f", "Weapon firing heat and cooling contribution.", "heat|cooling"), N("RECOIL", "n", "Weapon recoil magnitude, read with a converting float getter."),
            N("BEAM MASS FIRE FLYOUT IMPACT BURST", "", "Native weapon behavior configuration."),
            N("DETONATE_ON_WATER CATCHES_FIRE EXPIRES INSTANT RELOAD RANDOM_ROTATE TETHER_GUIDED SHOW_ON_RADAR ", "i", "Native weapon behavior switch."),
            N("DESC NAME MILITARY_NAME KILL_VERB", "s", "Localized weapon text.", reference: "message"), N("NAMESOUND U_RECHARGE_SND", "s", "Weapon sound alias.", "sample", "sound"),
            N("U_RECHARGE_TIME U_FIRE_TIME", "f", "User-controlled weapon timing in seconds."), N("LOCK_ON", "f ...", "Missile lock-on parameters.")];
        if (parent == "IMPACT") return [N("HEAT", "f f f f f f", "Impact heat feedback parameters."), N("CRATER", "", "Crater settings."), N("KILL_ANIMATION DAMAGE_ANIMATION DESTROY_ANIMATION SURFACE_ANIMATION", "s?", "Impact animation.", "animation", "animation")];
        return [N("MODEL ANIMATION EFFECT MODEL_ANIMATION", "s? x?", "Native weapon visual resource.", "resource", "model"), N("SOUND BOUNCE_SOUND", "s? s? s? s?", "Up to four native sound aliases; empty disables the sound.", "sample 1|sample 2|sample 3|sample 4", "sound"), N("RANDOM_ROTATE", "i", "Random effect rotation switch."), N("sphere clan", "n n", "Technology-specific weapon mass/critical slots.", "mass|critical slots")];
    }

    private static IEnumerable<NativeSpec> MechSpecs(string parent, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return [
            N("version", "i", "Native mech-definition version."), N("torso_limits", "n n n", "Torso yaw and pitch limits in degrees.", "maximum yaw (deg)|minimum pitch (deg)|maximum pitch (deg)"),
            N("recoil_bend recoil_slide recoil_twist lean_angle lean_factor aim_slerp_freq aim_min_pitch aim_max_pitch aim_min_yaw aim_max_yaw aim_arm_pitch_factor max_turn_rate accel_rate decel_rate crouch_height internal_mass max_mass max_speed", "n", "Native mech movement, aiming or mass setting."),
            N("aim_ignore_arms", "i", "Ignore arm motion while aiming."), N("chicken omnimech clan", "x", "Native mech class switch."), N("weight_class", "s", "Mech weight classification."),
            N("engine_minmax", "n n", "Minimum and maximum engine ratings.", "minimum rating|maximum rating"),
            N("torso_shake hud camera animations skins configs parts firepoints", "", "Native mech presentation, motion or loadout configuration.")];
        if (parent == "torso_shake") return [N("pitch_amp yaw_amp roll_amp freq damp_freq", "n", "Torso shake amplitude or frequency.")];
        if (parent == "motion" || parent == "canned") return [];
        return [N("anim", "s", "Sampled mech motion track.", "motion", "motion"), N("trans", "n n", "Motion transition thresholds.", "minimum|maximum"),
            N("node", "s", "Mech hierarchy node.", "node", "node"), N("ofs desired_pos", "n n n", "Local offset.", "x|y|z"),
            N("dist up down left right radius rating mass", "n", "Native mech part/camera parameter."), N("internal", "s", "Internal structure technology."), N("attach look_limits", "", "Camera attachment and look limits.")];
    }
    private static IEnumerable<NativeSpec> WorldSpecs(string parent, IReadOnlyList<string> path) => parent switch
    {
        "fog" or "underwater" => [N("node", "s", "Fog reference node.", "node", "node"), N("color", "n n n", "Fog color.", "red|green|blue"), N("range hwrange altitude", "n n", "Fog distance or altitude interval.", "minimum|maximum")],
        "global_wind" => [N("velocity", "n n n", "Global wind velocity.", "x|y|z"), N("max_speed random_accel random_angvel", "n", "Global wind variation parameter.")],
        "sun" => [N("color orientation", "n n n", "Sun RGB or XYZ orientation.", "x/red|y/green|z/blue"), N("ambient diffuse", "n", "Sun lighting intensity.")],
        "weather" => [N("type", "s", "Weather emitter type.", choices: ["RAIN", "SNOW"]), N("particles", "n", "Weather particle count."), N("color", "n n n", "Weather RGB.", "red|green|blue"), N("gravity", "n", "Weather particle gravity."), N("alpha_gradient", "n n", "Weather alpha endpoints.", "start alpha|end alpha"), N("wind", "", "Weather wind settings.")],
        "wind" => [N("dir vel", "n", "Weather wind direction or speed.")],
        _ => path.Count == 1 ? [N("fog global_wind underwater sun weather", "", "Mission environment settings."), N("ambient_temperature gravity jet_ceiling", "n", "Mission environment numeric setting.")] : []
    };
    private static readonly NativeSpec[] ObjectiveSpecs = [
        N("primary secondary final fail list obj_list node_change valve_change destroy_entity reach_area reach_velocity weapon_fire weapon_select target_object face_object aim_at_object", "", "Native objective group or condition."),
        N("desc", "s", "Localized objective text.", reference: "message"), N("nav", "s", "Navigation point label."), N("node", "s ...", "Objective node path.", "node", "node"),
        N("valve", "s", "Objective valve.", "valve", "valve"), N("value goal_count", "i", "Required objective count/value."),
        N("set_valve", "s i", "Objective valve assignment.", "valve|value", "valve"), N("time_limit", "", "Timed objective with duration and presentation settings."),
        N("state", "s", "Required node state.", choices: ["active", "inactive"]), N("radius velocity", "n", "Objective time/distance/velocity threshold."),
        N("point", "n n n", "Objective location.", "x|y|z"), N("sound", "s i?", "Objective narration and native selector.", "sample|selector", "sound"), N("silent simultaneous visible", "x", "Objective presentation/combination switch.")];
    private static IEnumerable<NativeSpec> UiSpecs(string parent, IReadOnlyList<string> path) => [
        N("IMAGE_PATH SHARED_IMAGE_PATH", "s", "Image search directory."), N("FONTS BUTTONS PRIMITIVES ROLLOVER", "", "Native dialog presentation group."),
        N("POSITION TEXT_OFFSET ENDP_ABS ENDP_REL WORDWRAP offset", "n n", "Native UI pixel coordinates.", "x (px)|y (px)"),
        N("CENTER", "i", "Center the cursor image."), N("LIST_OFFSET", "", "Nested list-item XY offsets."),
        N("MOUSE_RECT", "n n n n", "Pointer hit rectangle.", "top|left|height|width"), N("COLOR", "n n n", "UI RGB color.", "red|green|blue"),
        N("LABEL TEXT", "", "Text, optional direct XY/font index or named offset and font; nested lists declare several labels.", reference: "message"),
        N("BITMAP", "", "Image name with optional named offset, or an empty declaration.", reference: "texture"),
        N("FONT SELECTED_FONT LIST_FONT", "x", "Numeric font index or native named font alias.", "font style"), N("font", "s", "Native named font alias.", "font style"),
        N("SPACING", "n n?", "List spacing.", "horizontal spacing|vertical spacing"), N("SELECTED CAPTURE", "n", "Native UI switch."),
        N("LISTSIZE", "n n?", "List visible and total counts.", "total entries|visible entries"), N("SOUND", "s n? n?", "UI sound alias and playback parameters.", "sample|volume|parameter", "sound")];
    private static IEnumerable<NativeSpec> TurretSpecs(string parent, IReadOnlyList<string> path) => [
        N("TURRET WEAPON SOUNDS", "", "Native turret declaration group."), N("DESTROY_ANIM ONFIRE_ANIM", "s", "Turret animation.", "animation", "animation"),
        N("AUX_ANIM", "s x?", "Turret auxiliary animation and selector.", "animation|selector", "animation"),
        N("NAME", "s?", "Weapon name; an empty native weapon declaration is allowed.", "weapon", "weapon"), N("PARTS", "s s s? s?", "Turret hierarchy parts.", "base/barrel|barrel/firepoint|firepoint|second firepoint", "node"),
        N("HEALTH AMMO DETECTION_RANGE FIRE_RATE AMS_RANGE", "n", "Native turret numeric setting."), N("FIRE_LIMITS", "n n", "Native turret firing limits.", "minimum|maximum"),
        N("EFFECT", "s n", "Turret effect and duration.", "effect|duration (s)", "effect"), N("FIRE", "s", "Firing sound alias.", "sample", "sound"), N("LIMITS", "", "Grouped turret angular limits."), N("YAW", "n n", "Turret yaw limits in degrees.", "minimum yaw (deg)|maximum yaw (deg)"), N("IS_IDLE", "x", "Turret idle switch.")];
}
