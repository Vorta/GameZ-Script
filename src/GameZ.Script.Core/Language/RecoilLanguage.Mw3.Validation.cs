using System.Globalization;
using GameZ.Script.Core.Formats;

namespace GameZ.Script.Core.Language;

public static partial class RecoilLanguage
{
    private static readonly LanguageArgument[] NativeWeaponRow =
    [
        A("weapon", "s", "MW3 weapon definition.", role: "weapon"),
        A("availability", "i", "Native vehicle weapon availability control."),
        A("mount layout", "i", "Native weapon mount layout flag."),
        A("starting ammo", "i", "Initial ammunition or charge."),
        A("delay (s)", "f", "Native weapon dispatch delay."),
        A("AI minimum range", "f", "Minimum attack range."),
        A("AI maximum range", "f", "Maximum attack range."),
        A("recoil flags", "i", "Native vehicle recoil flags.")
    ];
    private static readonly LanguageArgument[] NativeTrigger =
    [
        A("assigned value", "i", "Value assigned to the target valve; an entry does nothing if it already holds this value."),
        A("test ammo", "i"), A("ammo at most", "f"), A("test recent hit", "i", "True when the pilot was hit within the last second."),
        A("test health", "i"), A("health at most", "f"), A("test heat", "i"), A("heat at least", "f"),
        A("when inside", "i"), A("region", "x", "Region name, or zero for no region.", role: "region"),
        A("test clear line", "i"), A("clear-line range", "f"), A("test nearby enemy", "i"), A("nearby range", "f"),
        A("unused", "i", "Stored bit not read by the native trigger evaluator."),
        A("valve", "x", "Valve name, or zero for no target.", role: "valve"), A("when outside", "i?")
    ];

    private static void NativeCheck(LanguageDocument doc, LanguageRecord record, string shape)
    {
        if (shape.Length == 0) return;
        CheckShape(doc, record, shape);
    }
    private static void ValidateMw3(LanguageDocument doc, CancellationToken token)
    {
        foreach (var record in doc.Records)
        {
            token.ThrowIfCancellationRequested();
            var field = Field(doc, record);
            if (field == null || field.Shape.Length == 0) continue;
            if (record.Value == null)
            {
                if (field.Shape.Split(' ').Any(t => t != "..." && !t.EndsWith('?')))
                    doc.Report("MW3_REQUIRED", "error", $"{record.Key} needs {ReadableShape(field.Shape)}.", doc.Span(record.KeyNode));
                continue;
            }
            NativeCheck(doc, record, field.Shape);
        }
        if (doc.Family is "network" or "region" or "waypoint-network") ValidateMw3Network(doc, token);
        if (doc.Family == "placements")
            foreach (var record in doc.Records.Where(r => r.Parent == null && r.Value != null))
            {
                token.ThrowIfCancellationRequested();
                NativeCheck(doc, record, "i a f x? ...");
                if (record.Values.Count > 1) NativeCheck(doc, record with { Value = record.Values[1] }, "f f f");
            }
        foreach (var record in doc.Records)
        {
            token.ThrowIfCancellationRequested();
            if (record.Value == null) continue;
            if (doc.Family == "vehicle" && record.Key == "weapons" && Ancestors(record.Parent).Contains("common_mode"))
                foreach (var row in record.Values)
                {
                    NativeCheck(doc, record with { Value = row }, "s i i i f f f i ...");
                    if (row.Kind == ZrdKind.Array && row.Children.Count > 8 && row.Children[8].Kind != ZrdKind.String)
                        doc.Report("MW3_WEAPON_TAIL", "error", "After the eight native weapon values, use named settings such as firepoint ( node ... ).", doc.Span(row.Children[8]));
                }
            if (doc.Family == "vehicle" && record.Key is "camback" or "platform" or "collision")
                foreach (var row in record.Values) NativeCheck(doc, record with { Value = row }, "f f f");
            if (record.Key == "vlist" && doc.Family == "placements")
                foreach (var row in record.Values) NativeCheck(doc, record with { Value = row }, "i i f i i f i f i x i f i f i x i?");
            if (doc.Family == "placements" && record.Key == "p8"
                && doc.Records.Any(r => ReferenceEquals(r.Parent, record.Parent) && r.Key is "p3" or "p4" or "p5" or "p7"))
                doc.Report("MW3_AIV_LEGACY", "warning", "p8 replaces p3, p4, p5 and p7; the native reader reports these forms as incompatible.", doc.Span(record.KeyNode));
            if (doc.Family == "landmines" && record.Parent == null && record.Key is not "weapon" and not "radius")
                foreach (var row in record.Values) NativeCheck(doc, record with { Value = row }, "f f f");
            if (doc.Family == "targets" && record.Key is "target" or "centroid" && record.Values.Count > 1)
                NativeCheck(doc, record with { Value = record.Values[1] }, "n n n");
            if (doc.Family == "targets" && record.Key == "health_below")
            {
                if (record.Values.Count % 3 != 0) doc.Report("GAMEZ_ARITY", "error", "health_below needs complete valve, value, health-threshold triples.", doc.Span(record.Value));
                for (int i = 0; i < record.Values.Count; i++) NativeCheck(doc, record with { Value = record.Values[i] }, i % 3 == 0 ? "s" : i % 3 == 1 ? "i" : "f");
            }
            if (doc.Family == "animation" && record.Key == "ANIMATION_DEFINITION")
                foreach (string required in new[] { "NAME" })
                    if (!doc.Records.Any(r => ReferenceEquals(r.Parent, record) && r.Key == required))
                        doc.Report("MW3_REQUIRED", "error", $"Animation definition needs {required}.", doc.Span(record.KeyNode));
            if (doc.Family == "animation" && record.Key is "PUFFER_STATE" or "DETONATE_WEAPON")
            {
                string required = record.Key == "PUFFER_STATE" ? "NAME" : "WEAPON";
                if (!doc.Records.Any(r => ReferenceEquals(r.Parent, record) && r.Key == required && r.FirstText != null))
                    doc.Report("MW3_REQUIRED", "error", $"{record.Key} needs {required}.", doc.Span(record.KeyNode));
            }
            if (doc.Family == "animation" && record.Key == "GRAVITY" && record.Parent?.Key == "OBJECT_MOTION" && record.FirstText == "LOCAL")
                if (record.Values.Count < 2) doc.Report("MW3_REQUIRED", "error", "LOCAL gravity needs its magnitude.", doc.Span(record.KeyNode));
                else NativeCheck(doc, record with { Value = record.Values[1] }, "n");
        }
    }

    private static void ValidateMw3Network(LanguageDocument doc, CancellationToken token)
    {
        var nodes = doc.Records.Where(r => r.Parent == null && r.Key.StartsWith("node_", StringComparison.Ordinal)).ToArray();
        var known = nodes.Where(r => int.TryParse(r.Key.AsSpan(5), out int n) && n >= 0).Select(r => int.Parse(r.Key.AsSpan(5), CultureInfo.InvariantCulture)).ToHashSet();
        foreach (var record in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(record.Key.AsSpan(5), out int index) || index < 0)
            { doc.Report("MW3_NODE_ID", "error", "A native node name needs a nonnegative numeric suffix.", doc.Span(record.KeyNode)); continue; }
            var children = record.Values;
            int links;
            if (children.Count > 0 && children[0].Kind == ZrdKind.Int)
            {
                if (children.Count < 3) { doc.Report("MW3_NODE_SHAPE", "error", "A spatial node needs a record marker, XYZ tuple and link list.", doc.Span(record.Value!)); continue; }
                NativeCheck(doc, record with { Value = children[1] }, "f f f"); links = 2;
            }
            else if (children.Count > 0 && children[0].Kind == ZrdKind.Array) links = 0;
            else { doc.Report("MW3_NODE_SHAPE", "error", "A native node starts with a spatial record marker or a non-spatial link list.", doc.Span(record.Value ?? record.KeyNode)); continue; }
            var list = children[links];
            if (list.Kind != ZrdKind.Array) { doc.Report("MW3_NODE_SHAPE", "error", "Native links require an array.", doc.Span(list)); continue; }
            foreach (var link in list.Children)
            {
                token.ThrowIfCancellationRequested();
                if (link.Kind != ZrdKind.Int) doc.Report("GAMEZ_TYPE", "error", "A native link must be a stored integer node index.", doc.Span(link));
                else if (doc.Family != "waypoint-network" && Numeric(link) is double target && target >= 0 && !known.Contains((int)target))
                    doc.Report("MW3_NODE_LINK", "error", $"Link names missing node_{target:00} in this file.", doc.Span(link));
            }
        }
    }

    private static void BuildMw3Arguments(LanguageDocument doc, CancellationToken token)
    {
        if (doc.Family is "mechs" or "soils" && doc.Root != null)
        {
            var list = doc.Root;
            while (list.Children.Count == 1 && list.Children[0].Kind == ZrdKind.Array) list = list.Children[0];
            BindArguments(doc, list, "catalog", [A(doc.Family == "mechs" ? "mech" : "material", "s", "Names in native catalog order.", role: doc.Family == "mechs" ? "mech" : null)], token, repeat: true);
        }
        foreach (var record in doc.Records)
        {
            token.ThrowIfCancellationRequested();
            if (record.Value == null || doc.Arguments.Owned.Contains(record.KeyNode.Id)) continue;
            string parent = record.Parent?.Key ?? "";
            var path = Ancestors(record.Parent);
            void Bind(LanguageArgument[] args, bool repeat = false, int count = int.MaxValue, bool claim = true) =>
                BindArguments(doc, record.Value, record.Key, args, token, repeat, count, claim, record.Key);
            void Rows(LanguageArgument[] args, bool repeat = false) =>
                Bind([new("row", "a", Children: args, Repeat: repeat)], true);
            if (doc.Family is "network" or "region" or "waypoint-network" && record.Parent == null && record.Key.StartsWith("node_", StringComparison.Ordinal))
            {
                bool spatial = record.Values.FirstOrDefault()?.Kind == ZrdKind.Int;
                int at = spatial ? 2 : 0;
                int count = record.Values.Count > at && record.Values[at].Kind == ZrdKind.Array ? record.Values[at].Children.Count : 0;
                var links = Enumerable.Range(1, Math.Min(100000, count + 1)).Select(i => doc.Family == "waypoint-network"
                    ? A("waypoint network " + i, "i", "Index of another waypoint network.", role: "network")
                    : A("link " + i, "i", "Node index in this native network; negative values are empty links.", role: "network-link")).ToArray();
                Bind(spatial ? [A("record marker", "i", "The integer type selects a spatial record; its conventional value is 12."), Tuple("position", XYZ()), Tuple("links", links)]
                    : [Tuple("links", links)], count: spatial ? 3 : 1, claim: false);
                continue;
            }
            if (doc.Family == "placements" && record.Parent == null)
            {
                int head = record.Values.Count > 3 && record.Values[3].Kind == ZrdKind.Int ? 4 : 3;
                Bind([A("AI network", "i", "Native AI network ID; an unavailable compiled network uses the engine's built-in network."), Tuple("position", XYZ()),
                    A("heading (deg)", "f"), A("team", "i", "When absent, the native reader chooses a team from the record name.")], count: head, claim: false);
                continue;
            }
            if (doc.Family == "placements" && record.Key == "vlist") { Rows(NativeTrigger); continue; }
            if (doc.Family == "vehicle" && record.Key == "weapons" && path.Contains("common_mode"))
            {
                foreach (var row in record.Values.Where(n => n.Kind == ZrdKind.Array))
                    BindArguments(doc, row, "weapon", NativeWeaponRow, token, count: 8, claimRemainder: false, fieldName: record.Key);
                continue;
            }
            if (doc.Family == "vehicle" && record.Key is "camback" or "platform" or "collision")
            {
                Rows(XYZ()); continue;
            }
            if (doc.Family == "network" || doc.Family == "region")
            {
                if (record.Key == "valveunion") { Rows([A("enabled", "i"), A("valve", "s", role: "valve"), A("count", "i?")]); continue; }
            }
            if (doc.Family == "sounds" && record.Key == "SETS" && record.Parent == null)
            {
                foreach (var set in record.Values.Where(v => v.Kind == ZrdKind.Array))
                    foreach (var row in set.Children.Where(v => v.Kind == ZrdKind.Array))
                        BindArguments(doc, row, "sound", [A("sound ID", "s"), A("WAV file", "s", role: "file")], token, count: 2, claimRemainder: false);
                continue;
            }
            if (doc.Family == "sounds" && record.Key == "CD_TRACKS") { Rows([A("context", "s"), A("CD track", "i")]); continue; }
            if (doc.Family == "ui" && record.Key is "LABEL" or "TEXT" or "BITMAP")
            {
                IEnumerable<ZrdNode> bodies = record.Values.FirstOrDefault()?.Kind == ZrdKind.Array ? record.Values : [record.Value];
                foreach (var body in bodies.Where(v => v.Kind == ZrdKind.Array))
                {
                    int count = record.Key != "BITMAP" && body.Children.Count >= 3 && body.Children[1].Kind is ZrdKind.Int or ZrdKind.Float
                        && body.Children[2].Kind is ZrdKind.Int or ZrdKind.Float ? body.Children.Count > 3 && body.Children[3].Kind == ZrdKind.Int ? 4 : 3 : 1;
                    BindArguments(doc, body, record.Key, [A(record.Key == "BITMAP" ? "image" : "text", "s", role: record.Key == "BITMAP" ? "texture" : "message"),
                        A("x (px)", "n"), A("y (px)", "n"), A("font index", "i")], token, count: count, claimRemainder: false);
                }
                continue;
            }
            if (doc.Family == "ui" && record.Key == "LIST_OFFSET") { Rows([A("x (px)", "n"), A("y (px)", "n")]); continue; }
            if (doc.Family == "effects" && record.Key == "EFFECTS")
            {
                foreach (var row in record.Values.Where(v => v.Kind == ZrdKind.Array))
                    BindArguments(doc, row, "effect", [A("model node", "s", role: "model")], token, count: 1, claimRemainder: false);
                continue;
            }
            if (doc.Family == "targets" && record.Key == "health_below")
            {
                var triple = new[] { A("valve", "s", role: "valve"), A("assigned value", "i"), A("health at most", "f") };
                Bind(Enumerable.Range(0, record.Values.Count).Select(i => triple[i % 3]).ToArray()); continue;
            }
            if (doc.Family == "targets" && record.Key is "target" or "centroid")
            { Bind([A("node", "s", role: "node"), Tuple("offset", XYZ())]); continue; }
            if (doc.Family == "location" && record.Parent == null)
            { Bind([Tuple("position", XYZ()), A("heading (deg)", "n")]); continue; }
            if (doc.Family == "landmines" && record.Parent == null && record.Key is not "weapon" and not "radius")
            { Rows(XYZ()); continue; }
            if (doc.Family == "detail" && record.Parent == null)
            {
                foreach (var row in record.Values.Where(v => v.Kind == ZrdKind.Array))
                    BindArguments(doc, row, record.Key, [row.Children.FirstOrDefault()?.Kind == ZrdKind.Array
                        ? Tuple("condition", [A("hardware property", "s", choices: ["CPU_CLASS", "CPU_MHZ", "RAM_KB"]), A("comparison", "s", choices: ["<", "<=", "==", ">=", ">", "!="]), A("threshold", "x")])
                        : A("fallback", "s", choices: ["DEFAULT"]), A("selected value", "x")], token, fieldName: record.Key);
                continue;
            }
            if (doc.Family == "multiplayer" && record.Key == "SOUND" && path.Contains("briefing"))
            { Bind([A("sample", "s", role: "sound")], count: 1, claim: false); continue; }
            var field = Field(doc, record);
            if (field == null || field.Shape.Length == 0) continue;
            var args = NativeArguments(doc, record, field);
            if (args.Length > 0) Bind(args, field.Shape.EndsWith("...", StringComparison.Ordinal));
        }
    }

    private static LanguageArgument[] NativeArguments(LanguageDocument doc, LanguageRecord record, LanguageField field)
    {
        string[] shape = field.Shape.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "...").ToArray();
        string[] labels = field.Arguments?.Split(", ", StringSplitOptions.None) ?? [];
        if (field.Arguments == null && (doc.Family == "animation" || ResourceFields.ContainsKey(doc.Family))) return FieldArguments(doc, record, field);
        string? Role(int i) => doc.Family is "network" or "region" or "waypoint-network" && record.Key == "valve" ? i == 1 ? "valve" : null
            : doc.Family is "network" or "region" or "waypoint-network" && record.Key == "deploy" ? i is 0 or 2 ? "valve" : i == 4 ? "network-link" : null
            : (field.Shape.EndsWith("...", StringComparison.Ordinal) || typeIsString(i)) ? field.Reference : null;
        bool typeIsString(int i) => shape[i] is "s" or "s?" || i == 0 && shape[i] == "x";
        return shape.Select((type, i) => A(i < labels.Length && labels[i].Length > 0 ? labels[i] : shape.Length == 1 ? field.Name.Replace('_', ' ').ToLowerInvariant() : null,
            type, FieldDocumentation(field), i == 0 ? field.Choices : null, Role(i))).ToArray();
    }

    private static IReadOnlyList<LanguageCompletion> Mw3Complete(LanguageDocument doc, int offset)
    {
        string prefix = CompletionPrefix(doc, offset);
        var replacement = CompletionSpan(doc, offset);
        if (ArgumentCompletions(doc, offset) is { } argumentItems)
            return argumentItems.Where(c => c.IsSnippet && prefix.Length == 0 || c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(MaximumResults).Select(c => c with { ReplacementSpan = replacement }).ToArray();
        var context = Context(doc, offset).Where(s => s.Length > 0).ToArray();
        string parent = context.LastOrDefault() ?? "";
        var owner = Field(doc, parent, context.Length > 1 ? context[^2] : "", context.Length > 0 ? context[..^1] : []);
        if (owner?.Choices is { Length: > 0 } choices)
            return choices.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(c => new LanguageCompletion(c, owner.Description, c, "enumMember", FieldDocumentation(owner), replacement)).ToArray();
        var fields = Mw3Fields(doc, parent, context).Select(f => new LanguageCompletion(f.Name, f.Description, f.Name, Documentation: FieldDocumentation(f), ReplacementSpan: replacement));
        if (doc.Family is "network" or "region" && context.Length == 0)
        {
            var editing = doc.Records.FirstOrDefault(r => r.Parent == null && doc.Span(r.KeyNode) == replacement);
            var used = doc.Records.Where(r => !ReferenceEquals(r, editing)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            var nodeNames = NativeNodeCandidates(prefix, used.Count + MaximumResults).Where(n => !used.Contains(n)).Take(MaximumResults)
                .Select(n => new LanguageCompletion(n, "Native spatial node", editing?.Value?.Kind == ZrdKind.Array ? n
                    : n + " (\n  12\n  ( 0.0 0.0 0.0 )\n  ( )\n)", "property", ReplacementSpan: replacement));
            fields = fields.Concat(nodeNames);
        }
        return fields.Where(c => c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(MaximumResults).ToArray();
    }

    private static IEnumerable<string> NativeNodeCandidates(string prefix, int maximumCandidates)
    {
        if (!"node_".StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !prefix.StartsWith("node_", StringComparison.OrdinalIgnoreCase)) yield break;
        string digits = prefix.StartsWith("node_", StringComparison.OrdinalIgnoreCase) ? prefix[5..] : "";
        int remaining = Math.Min(MaximumTokens + MaximumResults, maximumCandidates);
        if (digits.Length is > 0 and <= 10 && digits.All(char.IsAsciiDigit) && long.TryParse(digits, out long first) && first <= int.MaxValue)
        {
            if (digits[0] == '0')
            {
                if (digits.Length <= 2)
                    for (int i = 0; i < 10; i++)
                    { string name = "node_" + i.ToString("00", CultureInfo.InvariantCulture); if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return name; }
                yield break;
            }
            for (int width = Math.Max(2, digits.Length); width <= 10; width++)
            {
                long scale = (long)Math.Pow(10, width - digits.Length), start = first * scale, end = Math.Min(int.MaxValue, (first + 1) * scale - 1);
                for (long i = start; i <= end && remaining-- > 0; i++) yield return "node_" + i.ToString("00", CultureInfo.InvariantCulture);
                if (remaining <= 0) yield break;
            }
        }
        else if (digits.Length == 0)
            for (long i = 0; i <= int.MaxValue && remaining-- > 0; i++) yield return "node_" + i.ToString("00", CultureInfo.InvariantCulture);
    }
}
