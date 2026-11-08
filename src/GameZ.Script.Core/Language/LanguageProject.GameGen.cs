using System.Text.RegularExpressions;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Language;

public sealed partial class LanguageProject
{
    private static GameGenScriptLine? ScriptLine(LanguageDocument doc, int number) =>
        doc.Script is { } script && number > 0 && number <= script.Lines.Count ? script.Lines[number - 1] : null;
    private readonly Dictionary<(string Path, int Start), HashSet<string>> scriptChoices = [];
    private readonly Dictionary<(string Path, int Start), List<IReadOnlyList<string>>> sharedScriptChoices = [];
    private readonly Dictionary<int, List<IReadOnlyList<string>>> nativeChoiceSets = [];
    private long assistanceUnits;
    private bool assistanceLimited;
    private readonly Dictionary<(string Path, int Start), int> scriptReferenceLengths = [];
    private void ScriptLink(string path, TextSpan span, LanguageLocation location)
    {
        var key = (path, span.Start);
        if (definitions.TryGetValue(key, out var previous) && (previous.Count >= RecoilLanguage.MaximumResults || previous.Contains(location))) return;
        if (!AdmitAssistance(path, span, 128L + location.Path.Length)) return;
        scriptReferenceLengths[key] = span.Length;
        if (!definitions.TryGetValue(key, out var list)) definitions[key] = list = [];
        if (list.Count < RecoilLanguage.MaximumResults && !list.Contains(location)) list.Add(location);
    }
    private bool AdmitAssistance(string path, TextSpan span, long units)
    {
        if (assistanceLimited) return false;
        if ((assistanceUnits += units) <= 2_000_000) return true;
        assistanceLimited = true;
        (documents.GetValueOrDefault(path) ?? validationDocuments.GetValueOrDefault(path))?.Report("GAMEZ_LIMIT", "information", "Project assistance budget exceeded; further references and suggestions omitted.", span);
        return false;
    }
    private void Choices(string path, TextSpan span, IEnumerable<string> values)
    {
        if (assistanceLimited) return;
        if (Game == GameVariant.MechWarrior3)
        {
            // Common includes run in several campaigns. Preserve one immutable catalog for each
            // distinct model/include/macro candidate set instead of copying it into every operand.
            string[] candidates = values.Take(MaximumReferenceNames).Distinct(StringComparer.Ordinal).ToArray();
            var hash = new HashCode(); foreach (string candidate in candidates) hash.Add(candidate, StringComparer.Ordinal);
            int id = hash.ToHashCode();
            if (!nativeChoiceSets.TryGetValue(id, out var sets)) nativeChoiceSets[id] = sets = [];
            var match = sets.FirstOrDefault(s => s.SequenceEqual(candidates, StringComparer.Ordinal));
            if (match == null)
            {
                long units = 32L + candidates.Sum(c => 8L + c.Length);
                if (!AdmitAssistance(path, span, units)) return;
                sets.Add(match = candidates);
            }
            SharedChoices(path, span, match);
            return;
        }
        var key = (path, span.Start);
        if (!scriptChoices.TryGetValue(key, out var names))
        {
            if (!AdmitAssistance(path, span, 128L + path.Length)) return;
            scriptChoices[key] = names = new(StringComparer.Ordinal);
        }
        foreach (string name in values)
        {
            if (names.Contains(name)) continue;
            if (names.Count >= RecoilLanguage.MaximumResults) break;
            if (!AdmitAssistance(path, span, name.Length + 1L)) break;
            names.Add(name);
        }
    }
    private IReadOnlyList<LanguageCompletion> CompleteGameGen(LanguageDocument doc, int offset)
    {
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        if (GameGenLanguage.InComment(doc, offset)) return [];
        var (_, _, span) = GameGenLanguage.Context(doc, offset);
        var local = GameGenLanguage.Complete(doc, offset);
        string prefix = doc.Text.Substring(span.Start, Math.Clamp(offset - span.Start, 0, span.Length));
        scriptChoices.TryGetValue((doc.Path, span.Start), out var names);
        sharedScriptChoices.TryGetValue((doc.Path, span.Start), out var shared);
        if (names != null || shared != null)
            return local.Concat((names ?? Enumerable.Empty<string>()).Concat(shared?.SelectMany(s => s) ?? []).Distinct(StringComparer.Ordinal)
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(n => new LanguageCompletion(n, "Project reference", n, "reference", ReplacementSpan: span))).Take(RecoilLanguage.MaximumResults).ToArray();
        return local;
    }
    private IReadOnlyList<string> SharedCandidateSet(string path, IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (string name in values.Take(MaximumReferenceNames))
        {
            // Strings belong to the source/world snapshot already; retain each reference once per context.
            if (!AdmitAssistance(path, new(0, 0), 8)) break;
            result.Add(name);
        }
        return result;
    }
    private void SharedChoices(string path, TextSpan span, IReadOnlyList<string> candidates)
    {
        if (assistanceLimited) return;
        var key = (path, span.Start);
        if (!sharedScriptChoices.TryGetValue(key, out var sets))
        {
            if (!AdmitAssistance(path, span, 128L + path.Length)) return;
            sharedScriptChoices[key] = sets = [];
        }
        if (sets.Count < RecoilLanguage.MaximumResults && !sets.Contains(candidates)) sets.Add(candidates);
    }
    private void ResolveGameGen(SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        var files = snapshot.Files();
        var models = SourceProject.Files(Root, "data", p => p.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase), token: token)
            .Where(snapshot.IncludesSource).ToArray();
        // Literal includes remain navigable even in an orphan support script with no mission context.
        foreach (var doc in documents.Values.Where(d => d.Script != null))
            foreach (var line in doc.Script!.Lines.Where(l => l.Tokens.Count > 1 && ScriptConditions.IsSource(l.Tokens[0]) && !l.Tokens[1].Contains('%')))
            {
                string path = "gamegen/" + line.Tokens[1].Replace('\\', '/');
                if (documents.ContainsKey(path)) ScriptLink(doc.Path, line.Spans[1], new(path, new(0, 0)));
            }
        foreach (var entry in documents.Values.Where(d => Game == GameVariant.MechWarrior3
            ? d.Path.StartsWith("gamegen/", StringComparison.OrdinalIgnoreCase) && d.Path.EndsWith(".gs", StringComparison.OrdinalIgnoreCase)
                && d.Path.Count(c => c == '/') == 1 && documents.ContainsKey($"data/{Path.GetFileNameWithoutExtension(d.Path)}/zrdr/anim.zad")
            : Regex.IsMatch(d.Path, @"\Agamegen/m\d+\.gs\z", RegexOptions.IgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            string mission = Path.GetFileNameWithoutExtension(entry.Path).ToLowerInvariant();
            bool complete = true;
            var macros = new Dictionary<string, string>(StringComparer.Ordinal);
            var assignments = new Dictionary<string, LanguageLocation>(StringComparer.Ordinal);
            var conditions = new ScriptConditions();
            var dirs = new DirectorySearchList(); var textures = new DirectorySearchList(); var work = new DirectoryWorkBudget();
            int instructions = 0;
            var operands = new ScriptOperandBudget();
            // World assembly is cached within this immutable snapshot and never exports files.
            SourceBuilder.Snapshot.AssembledWorld? built = null;
            try { built = GameGenWorldCache.World(Root, mission, files, token, Game); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { Issue(entry, new(0, 0), "GAMEZ_CONTEXT_INCOMPLETE", "information", ex.Message); complete = false; }
            var names = new Dictionary<string, List<LanguageLocation>>(StringComparer.Ordinal);
            LanguageLocation Location(WorldNode node)
            {
                if (built != null && built.Provenance.TryGetValue(node, out var origin) && (origin.Created ?? origin.Load) is { } created
                    && documents.TryGetValue(created.Script, out var owner) && ScriptLine(owner, created.Line) is { } line)
                    return new(created.Script, line.Spans.Count > 2 && created.Command == "LoadGameGen" ? line.Spans[2] : line.Spans.Count > 1 ? line.Spans[1] : line.Spans[0], mission);
                return new(entry.Path, new(0, 0), mission);
            }
            var runtimeWork = new GameGenNodes.Work(token);
            GameGenNodes? runtimeNodes = built == null ? null : new(built.World, Location, runtimeWork);
            if (built != null)
            {
                foreach (var (at, message) in built.ReceiverIssues)
                    if (documents.TryGetValue(at.Script, out var receiverDoc) && ScriptLine(receiverDoc, at.Line) is { } receiverLine)
                        Issue(receiverDoc, receiverLine.Spans[0], "GAMEZ_RECEIVER", "warning", message);
                foreach (var node in built.World.Nodes)
                {
                    token.ThrowIfCancellationRequested();
                    LanguageLocation location = Location(node);
                    if (!names.TryGetValue(node.Name, out var locations)) names[node.Name] = locations = [];
                    if (locations.Count < RecoilLanguage.MaximumResults) locations.Add(location);
                }
                foreach (string warning in built.Warnings.Take(RecoilLanguage.MaximumDiagnostics))
                {
                    Issue(entry, new(0, 0), "GAMEZ_WORLD", "warning", warning);
                    if (Game != GameVariant.MechWarrior3 && warning.Contains("does not apply", StringComparison.Ordinal) || warning.Contains("does not exist", StringComparison.Ordinal)) complete = false;
                }
            }
            bool written = false;
            var nativeNodeChoices = Game == GameVariant.MechWarrior3 ? SharedCandidateSet(entry.Path, names.Keys) : [];
            var nativeChoiceSites = new HashSet<(string Path, TextSpan Span)>();
            try
            {
                Run(entry.Path, 0, false);
                // Runtime scripts have a distinct interpreter run; do not carry build macros into them.
                macros.Clear(); assignments.Clear(); dirs = new(); textures = new(); written = false;
                bool native = Game == GameVariant.MechWarrior3;
                conditions = new(native ? "true" : "TRUE");
                string init = native ? $"gamegen/support/{mission}size.gw" : $"gamegen/support/init{mission}.gw", runtime = native ? entry.Path : $"gamegen/{mission}_zbd.gs";
                if (native) { macros["USEZBD"] = "true"; macros["HARDWARE"] = "true"; }
                if (documents.ContainsKey(init) || documents.ContainsKey(runtime)) runtimeNodes = new(new GameZWorld(), Location, runtimeWork);
                if (documents.ContainsKey(init) && !documents.ContainsKey(runtime))
                { complete = false; Issue(entry, new(0, 0), "GAMEZ_CONTEXT_INCOMPLETE", "information", "Mission initialization exists but the runtime world-read companion is missing."); }
                if (documents.ContainsKey(init)) Run(init, 0, true);
                if (documents.ContainsKey(runtime)) Run(runtime, 0, true);
                if (runtimeNodes != null)
                {
                    names.Clear();
                    foreach (var item in runtimeNodes.Names)
                    {
                        if (!names.TryGetValue(item.Name, out var locations)) names[item.Name] = locations = [];
                        if (locations.Count < RecoilLanguage.MaximumResults) locations.Add(item.Origin);
                    }
                }
                if (Game == GameVariant.MechWarrior3 && !names.Keys.SequenceEqual(nativeNodeChoices))
                {
                    var runtimeChoices = SharedCandidateSet(entry.Path, names.Keys);
                    foreach (var site in nativeChoiceSites) SharedChoices(site.Path, site.Span, runtimeChoices);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { complete = false; Issue(entry, new(0, 0), "GAMEZ_CONTEXT_INCOMPLETE", "information", ex.Message); }
            string? activeNativeReader = null;
            foreach (var doc in (Game == GameVariant.MechWarrior3
                ? documents.Values.Where(d => Mission(d.Path).Equals(mission, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(d.Path).Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase))
                : new[] { "aiv.zrd", "aiv_easy.zrd", "aiv_hard.zrd" }.Select(Resource).OfType<LanguageDocument>()).Distinct())
            {
                activeNativeReader = NativeReader(doc.Path);
                if (Game == GameVariant.MechWarrior3)
                    foreach (var row in doc.Records.Where(r => r.Parent == null && r.Values.FirstOrDefault() is { Kind: ZrdKind.Int }))
                    {
                        var selected = row.Values[0]; int number = unchecked((int)selected.Bits);
                        if (number > 0 && Resource($"net_{number:00}.zrd") is { } nativeNet)
                            ScriptLink(doc.Path, doc.Span(selected), new(nativeNet.Path, new(0, 0), activeNativeReader ?? mission));
                    }
                if (!complete || !doc.Complete)
                {
                    Issue(doc, new(0, 0), "GAMEZ_CONTEXT_INCOMPLETE", "information", "Vehicle template checks are incomplete; no missing-template conclusions were made.");
                    continue;
                }
                foreach (var record in doc.Records.Where(r => r.Parent == null && r.Value?.Kind == ZrdKind.Array))
                {
                    string template = MissionSceneLoader.VehicleTemplateName(record.Key);
                    var vehicle = Resource("vehicle.zrd");
                    if (Game != GameVariant.MechWarrior3 && vehicle is { Complete: true } && !vehicle.Records.Any(r => r.Parent == null && r.Key == template))
                        Issue(doc, doc.Span(record.KeyNode), "GAMEZ_AIV_DEFINITION", "warning", $"Vehicle definition '{RecoilLanguage.Shown(template)}' is absent from {vehicle.Path}; that selection does not spawn this vehicle.");
                    bool found = names.TryGetValue(record.Key, out var target) || names.TryGetValue(template, out target);
                    if (!found && Game == GameVariant.MechWarrior3)
                    {
                        string library = $"data/common/mechs/models/mech_{template}.gltf";
                        if (!files.Exists(library)) library = $"data/common/mechs/models/mech_{template}.glb";
                        if (files.Exists(library)) { target = [new(library, new(0, 0), mission)]; found = true; }
                        else
                        {
                            Issue(doc, doc.Span(record.KeyNode), "GAMEZ_CONTEXT_INCOMPLETE", "information", "Native actor selection may be supplied by the mission roster or this may be an authored placeholder; an AIV name and vehicle setting alone do not establish a required GameGen template.");
                            continue;
                        }
                    }
                    if (found)
                    {
                        foreach (var location in target!) ScriptLink(doc.Path, doc.Span(record.KeyNode), location);
                    }
                    else Issue(doc, doc.Span(record.KeyNode), "GAMEZ_AIV_TEMPLATE_NOT_LOADED", "error",
                        $"GameGen does not load template '{RecoilLanguage.Shown(template)}' required by '{RecoilLanguage.Shown(record.Key)}'.");
                    if (Game != GameVariant.MechWarrior3 && record.Values.FirstOrDefault() is { Kind: ZrdKind.Int } network && unchecked((int)network.Bits) > 0)
                    {
                        var net = Resource($"net_{unchecked((int)network.Bits):00}.zrd");
                        if (net != null) ScriptLink(doc.Path, doc.Span(network), new(net.Path, new(0, 0), mission));
                        else if (Game != GameVariant.MechWarrior3) Issue(doc, doc.Span(network), "GAMEZ_AIV_NETWORK", "warning", $"AI network {unchecked((int)network.Bits)} is unavailable; the vehicle may remain inert.");
                    }
                }
            }
            if (complete)
            {
                foreach (var doc in documents.Values.Where(d => d.Complete && d.Family == "pickup-placements" && d.Path.StartsWith($"data/{mission}/", StringComparison.OrdinalIgnoreCase)))
                    foreach (var value in doc.Tokens.Where(t => t.Kind == "string"))
                        if (MissionPickupType.Catalog.FirstOrDefault(t => t.Name == value.Text) is { } type)
                            NeedNode(doc, value.Span, type.TemplateName, "GAMEZ_PICKUP_TEMPLATE");
                if (Resource("effects.zrd") is { Complete: true } effects)
                    foreach (var row in effects.Records.Where(r => r.Key == "EFFECTS").SelectMany(r => r.Values).Where(n => n.Kind == ZrdKind.Array))
                        if (row.Children.FirstOrDefault() is { Kind: ZrdKind.String } node) NeedNode(effects, effects.Span(node), node.Text, "GAMEZ_EFFECT_MODEL");
                if (Resource("weapons.zrd") is { Complete: true } weapons)
                    foreach (var record in weapons.Records.Where(r => r.Key == "MODEL" && r.Parent?.Key == "FLYOUT"))
                        if (!weapons.Records.Any(r => ReferenceEquals(r.Parent, record.Parent) && r.Key == "EFFECT") && record.Scalars.FirstOrDefault() is { Kind: ZrdKind.String } node)
                            NeedNode(weapons, weapons.Span(node), node.Text, "GAMEZ_WEAPON_MODEL");
            }
            LanguageDocument? Resource(string file)
            {
                if (Game == GameVariant.MechWarrior3) return NativeResources(mission, activeNativeReader, file).FirstOrDefault();
                var candidates = documents.Values.Where(d => Path.GetFileName(d.Path).Equals(file, StringComparison.OrdinalIgnoreCase)
                    && (d.Path.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase) || d.Path.StartsWith($"data/{mission}/", StringComparison.OrdinalIgnoreCase))).ToArray();
                var common = candidates.Where(d => d.Path.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase)).ToArray();
                return common.Length == 1 ? common[0] : common.Length > 1 ? null : candidates.Length == 1 ? candidates[0] : null;
            }
            void NeedNode(LanguageDocument doc, TextSpan span, string name, string code)
            {
                if (names.TryGetValue(name, out var locations)) foreach (var location in locations) ScriptLink(doc.Path, span, location);
                else Issue(doc, span, code, "warning", $"GameGen does not provide model '{RecoilLanguage.Shown(name)}' in this mission.");
            }
            void Issue(LanguageDocument doc, TextSpan span, string code, string severity, string message)
            {
                if (doc.diagnostics.Count >= RecoilLanguage.MaximumDiagnostics) { doc.Report(code, severity, message, span, mission); return; }
                doc.diagnostics.Add(new(code, severity, message.Length > 2048 ? message[..2048] : message, span, mission,
                    [new(SourceProject.Resolve(Root, entry.Path), new(0, 0), "Mission GameGen entry", entry.Text)]));
            }
            void Run(string path, int depth, bool runtime)
            {
                token.ThrowIfCancellationRequested();
                if (depth > WorldAssembler.MaximumScriptDepth) throw new InvalidDataException("Script include depth exceeded; analysis is incomplete.");
                if (!documents.TryGetValue(path, out var doc) || doc.Script == null) { complete = false; Issue(entry, new(0, 0), "GAMEZ_SOURCE_MISSING", "error", $"Cannot read sourced script {path}."); return; }
                if (!doc.Complete) complete = false;
                foreach (var line in doc.Script.Lines)
                {
                    token.ThrowIfCancellationRequested();
                    if (!line.IsInstruction) continue;
                    if (++instructions > WorldAssembler.MaximumInstructions) throw new InvalidDataException("Script instruction limit exceeded.");
                    operands.Inspect(line.Tokens, token);
                    string raw = line.Tokens[0];
                    if (!conditions.Runs(line.Tokens, macros)) continue;
                    if (ScriptConditions.IsQuit(raw)) return;
                    string[] args = operands.Expand(line.Tokens, macros, token);
                    for (int i = 1; i < line.Tokens.Count; i++)
                    {
                        int open = line.Tokens[i].IndexOf('%'), close = open < 0 ? -1 : line.Tokens[i].IndexOf('%', open + 1);
                        if (open >= 0)
                        {
                            int length = close < 0 ? line.Tokens[i].Length - open : close - open + 1;
                            Choices(path, new(line.Spans[i].Start, line.Spans[i].Length),
                                macros.Keys.Select(n => line.Tokens[i][..open] + "%" + n + "%" + (close < 0 ? "" : line.Tokens[i][(close + 1)..])));
                        }
                    }
                    for (int i = 1; i < line.Tokens.Count && !assistanceLimited; i++)
                        foreach (var match in Regex.EnumerateMatches(line.Tokens[i], "%([^%]+)%"))
                        {
                            token.ThrowIfCancellationRequested();
                            if (assistanceLimited) break;
                            if (assignments.TryGetValue(line.Tokens[i].Substring(match.Index + 1, match.Length - 2), out var assignment))
                                ScriptLink(doc.Path, new(line.Spans[i].Start + match.Index, match.Length), assignment);
                        }
                    if (ScriptConditions.IsSet(raw))
                    {
                        if (args.Length > 0) { macros[args[0]] = args.ElementAtOrDefault(1) ?? ""; assignments[args[0]] = new(path, line.Spans[1], mission); }
                        continue;
                    }
                    if (ScriptConditions.IsSource(raw))
                    {
                        Choices(path, line.Spans.Count > 1 ? line.Spans[1] : new(line.Start + line.Length, 0),
                            documents.Keys.Where(p => p.StartsWith("gamegen/", StringComparison.OrdinalIgnoreCase)).Select(p => p[8..]));
                        if (args.Length == 0) { complete = false; continue; }
                        string source = "gamegen/" + args[0].Replace('\\', '/');
                        SourceProject.RequireSource(source); _ = SourceProject.Resolve(Root, source);
                        if (documents.ContainsKey(source)) ScriptLink(path, line.Spans[1], new(source, new(0, 0), mission));
                        else Issue(doc, line.Spans[1], "GAMEZ_SOURCE_MISSING", "error", $"Cannot resolve source '{RecoilLanguage.Shown(args[0])}'.");
                        Run(source, depth + 1, runtime); continue;
                    }
                    var command = GameGenLanguage.Command(raw, Game);
                    if (command == null) continue; // An ignored unknown command does not change the established world.
                    string A(int i) => args.ElementAtOrDefault(i) ?? "";
                    if (command.Name == "SetModelDirectory") dirs.Add(A(0), work, files.FolderExists);
                    if (command.Name == "SetTextureDirectory") textures.Add(A(0), work, files.FolderExists);
                    if (WorldAssembler.ScriptTexture(command.Name, args) is { } texture)
                    {
                        int at = command.Name == "LensFlareTexture" ? 2 : 1;
                        string name = WorldAssembler.TextureName(texture);
                        int limit = Game == GameVariant.MechWarrior3 ? 31 : 19;
                        if (name.Length < 1 || name.Length > limit || texture.Contains('\\') || texture.Contains('/'))
                            Issue(doc, line.Spans[at], "GAMEZ_TEXTURE_NAME", "warning", $"Runtime textures require a basename of at most {limit} characters before the first dot.");
                        string png = name + ".png";
                        string? resolvedTexture = textures.Folders.Select(f => f + "/" + png).FirstOrDefault(files.Exists);
                        if (resolvedTexture != null) ScriptLink(path, line.Spans[at], new(resolvedTexture, new(0, 0), mission));
                        else Issue(doc, line.Spans[at], "GAMEZ_TEXTURE_MISSING", "warning", $"Texture '{RecoilLanguage.Shown(texture)}' is absent from the active texture search paths.");
                    }
                    if (command.Name == "LoadGameGen")
                        Choices(path, line.Spans.Count > 1 ? line.Spans[1] : new(line.Start + line.Length, 0),
                            models.Where(m => dirs.Folders.Contains(Path.GetDirectoryName(m)!.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).OfType<string>());
                    if (command.Name == "GameZWriteZBDFile") written = true;
                    if (runtime && command.Name == "GameZReadZBDFile")
                    {
                        string output = built?.WriteInstruction?.Args.FirstOrDefault() ?? "";
                        if (built == null || !A(0).Replace('\\', '/').Equals(output.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        { complete = false; Issue(doc, line.Spans[0], "GAMEZ_CONTEXT_INCOMPLETE", "information", "This runtime read cannot be matched to the mission's built world."); }
                        else runtimeNodes = new(built.World, Location, runtimeWork);
                    }
                    if (runtime && command.Name == "SetGameZNodeArraySize" && int.TryParse(A(0), out int capacity) && built != null && capacity < built.World.Nodes.Count)
                        Issue(doc, line.Spans[1], "GAMEZ_CAPACITY", "warning", $"Configured capacity {capacity} is smaller than the built world's {built.World.Nodes.Count} live nodes, before runtime clones.");
                    if (command.Name == "LoadGameGen" && !runtime && !written && args.Length >= 2)
                    {
                        string? model = ResolveModel(A(0), dirs.Folders);
                        if (model != null) ScriptLink(path, line.Spans[1], new(model, new(0, 0), mission));
                        else { complete = false; Issue(doc, line.Spans[1], "GAMEZ_MODEL_MISSING", "error", $"Model '{RecoilLanguage.Shown(A(0))}' is absent from the active model search paths."); }
                    }
                    if (command.Name is "FindNode" or "FindSubNode" or "AddChild" or "CameraSetWorld" or "CameraSetWindow" or "WorldAddLight")
                    {
                        var span = line.Spans.Count > 1 ? line.Spans[1] : new TextSpan(line.Start + line.Length, 0);
                        if (Game == GameVariant.MechWarrior3)
                        {
                            SharedChoices(path, span, nativeNodeChoices);
                            nativeChoiceSites.Add((path, span));
                        }
                        else Choices(path, span, names.Keys);
                        if (!runtime && built?.LookupTargets.TryGetValue((path, line.Number), out var resolved) == true)
                            foreach (var node in resolved) ScriptLink(path, span, Location(node));
                        else if (runtime && command.Name is "FindNode" or "FindSubNode" && runtimeNodes?.Run(command.Name, args, new(path, span, mission)) is { } target)
                            ScriptLink(path, span, target);
                    }
                    if (!runtime && command.Name == "GenericTerrainMorph" && !written) complete = false;
                    if (runtime)
                    {
                        if (command.Name is not ("FindNode" or "FindSubNode"))
                            runtimeNodes?.Run(command.Name, args, new(path, line.Spans.Count > 1 ? line.Spans[1] : line.Spans[0], mission));
                        if (command.Name is "NewNode" or "FreeNode" or "LoadGameGen" or "GenericTerrainMorph" or "SEQAddChild" or "NewSEQ" or "SEQNew")
                        { complete = false; Issue(doc, line.Spans[0], "GAMEZ_CONTEXT_INCOMPLETE", "information", "This runtime operation's name effects cannot be established."); }
                    }
                }
            }
            string? ResolveModel(string operand, IReadOnlyList<string> folders)
            {
                return WorldAssembler.ResolveModel(operand, folders, files.Exists);
            }
        }
        foreach (var doc in documents.Values.Where(d => d.Family == "placements"))
        {
            string mission = Mission(doc.Path);
            if (!IsCommon(doc.Path) && !documents.ContainsKey($"gamegen/{mission}.gs"))
                doc.Report("GAMEZ_CONTEXT_INCOMPLETE", "information", "The mission GameGen entry is missing; template availability cannot be established.", new(0, 0), mission);
        }
    }
}
