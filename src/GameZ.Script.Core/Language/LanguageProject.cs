using System.Text;
using GameZ.Script.Core.Animation;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Language;

/// <summary>A bounded, read-only project analysis. Unsaved inputs overlay disk without publishing or exporting.</summary>
public sealed partial class LanguageProject
{
    public const long MaximumTextBytes = 64L * 1024 * 1024;
    private const int MaximumReferenceNames = 50_000;
    public string Root { get; }
    public GameVariant Game { get; private set; } = GameVariant.Recoil;
    public IReadOnlyDictionary<string, LanguageDocument> Documents => documents;
    /// <summary>All checked text sources, including slim closed animation documents during exhaustive validation.</summary>
    public IEnumerable<LanguageDocument> ValidationDocuments => documents.Values.Concat(validationDocuments.Values);
    public int CheckedFileCount { get; private set; }
    private readonly Dictionary<string, LanguageDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LanguageDocument> validationDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZanSourceIndex> animationIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, int Start), List<LanguageLocation>> definitions = [];
    private readonly Dictionary<(string Path, int Start), string[]> referenceNames = [];
    private LanguageProject(string root) => Root = root;

    public static LanguageProject Analyze(string root, IReadOnlyDictionary<string, string>? overlays = null, bool deep = false, CancellationToken token = default,
        IReadOnlyDictionary<string, byte[]>? sourceOverlay = null, bool missionAnalysis = true, GameVariant? game = null)
    {
        if (!SourceProject.IsProject(root)) throw new InvalidDataException("Open a source project containing data/ and gamegen/.");
        root = Path.GetFullPath(root);
        var result = new LanguageProject(root);
        GameVariant? resolvedGame = game ?? SourceGameProfile.Identify(root, token);
        result.Game = resolvedGame ?? GameVariant.Recoil;
        bool Owns(string path) => SourceGameProfile.OwnsSource(root, SourceProject.Resolve(root, path));
        var pending = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (sourceOverlay != null)
            foreach (var (path, content) in sourceOverlay)
            {
                token.ThrowIfCancellationRequested();
                SourceProject.RequireSource(path);
                if (Owns(path)) pending[path] = content;
            }
        long bytes = 0;
        if (overlays != null)
            foreach (var (path, text) in overlays)
            {
                token.ThrowIfCancellationRequested();
                _ = SourceProject.Resolve(root, path); SourceProject.RequireSource(path);
                if (!Owns(path)) continue;
                if (text.Length > SourceProject.MaximumSourceTextBytes || text.Length > MaximumTextBytes - bytes) throw new InvalidDataException("Unsaved source buffers exceed the language analysis limit.");
                bytes += text.Length;
                if (text.AsSpan().IndexOfAnyExceptInRange('\0', '\xFF') < 0) pending[path] = ZrdTextSyntax.Encode(text);
                // Invalid encodings must not be hidden by an older valid disk version.
                else pending[path] = [];
            }
        var snapshot = new SourceBuilder.Snapshot(root, pending, inventoryToken: token, game: result.Game, sourceFilter: Owns);
        var inputs = SourceProject.Files(root, "data", IsLanguageFile, pending.Keys.ToArray(), token)
            .Concat(SourceProject.Files(root, "gamegen", GameGenLanguage.IsScript, pending.Keys.ToArray(), token)).Distinct(StringComparer.OrdinalIgnoreCase).Where(Owns);
        long tokens = 0;
        foreach (string path in inputs)
        {
            token.ThrowIfCancellationRequested();
            string text;
            if (overlays?.TryGetValue(path, out var buffer) == true) text = buffer;
            else
            {
                var raw = snapshot.Read(path, token, GameGenLanguage.IsScript(path) || IsAnimationScript(path)
                    ? ProjectReadLimits.Text(Math.Min(SourceProject.MaximumSourceTextBytes, MaximumTextBytes - bytes))
                    : ProjectReadLimits.Resource(Math.Min(SourceProject.MaximumSourceTextBytes, MaximumTextBytes - bytes)));
                bytes += raw.Length;
                if (!GameGenLanguage.IsScript(path) && !IsAnimationScript(path) && !ZrdText.LooksLikeText(raw)) continue;
                text = Encoding.Latin1.GetString(raw);
            }
            result.CheckedFileCount++;
            if (IsAnimationScript(path))
            {
                result.IndexAnimation(path, text, token);
                if (overlays?.ContainsKey(path) != true)
                {
                    if (deep)
                    {
                        var checkedAnimation = RecoilLanguage.Analyze(path, text, token, result.Game);
                        // Keep diagnostics and their source text, releasing dense sample tokens and syntax graphs.
                        var slim = new LanguageDocument(path, text) { Game = result.Game, Family = checkedAnimation.Family, Complete = checkedAnimation.Complete };
                        slim.diagnostics.AddRange(checkedAnimation.Diagnostics);
                        result.validationDocuments.Add(path, slim);
                    }
                    continue;
                }
            }
            var doc = RecoilLanguage.Analyze(path, text, token, resolvedGame);
            if (resolvedGame == null && doc.Game == GameVariant.MechWarrior3) result.Game = GameVariant.MechWarrior3;
            tokens += doc.Tokens.Count;
            if (tokens > 2_000_000) throw new InvalidDataException("Project analysis exceeds its two-million-token limit.");
            result.documents.Add(path, doc);
        }
        if (resolvedGame == null && result.animationIndexes.Values.Any(i => i.Kind == "zan-motion")) result.Game = GameVariant.MechWarrior3;
        if (resolvedGame == null && result.Game == GameVariant.MechWarrior3)
        {
            foreach (var path in result.documents.Keys.ToArray())
            {
                token.ThrowIfCancellationRequested();
                var previous = result.documents[path];
                if (previous.Game != result.Game) result.documents[path] = RecoilLanguage.Analyze(path, previous.Text, token, result.Game);
            }
            foreach (var animation in result.validationDocuments.Values) animation.Game = result.Game;
            snapshot.CheckUnchanged(token);
            snapshot = new SourceBuilder.Snapshot(root, pending, inventoryToken: token, game: result.Game, sourceFilter: Owns);
        }
        result.Resolve(snapshot, deep, token, missionAnalysis);
        snapshot.CheckUnchanged(token);
        return result;
    }
    public static bool IsLanguageFile(string path) => GameGenLanguage.IsScript(path) || IsAnimationScript(path) || path.EndsWith(".zad", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase);
    private static bool IsAnimationScript(string path) => path.EndsWith(".zan", StringComparison.OrdinalIgnoreCase);
    private void IndexAnimation(string path, string text, CancellationToken token)
    {
        try { animationIndexes[path] = ZanLanguage.Index(path, text, token, Game); }
        catch (InvalidDataException) { /* Full buffer/deep analysis owns malformed-source diagnostics. */ }
    }
    /// <summary>Captured line starts make closed-track locations precise without retaining dense animation syntax.</summary>
    public IReadOnlyList<int> TargetLineStarts(string path)
    {
        if (animationIndexes.TryGetValue(path, out var index)) return index.LineStarts;
        if (modelLineStarts.TryGetValue(path, out var lines)) return lines;
        string text = TargetText(path);
        List<int> starts = [0];
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }
    public string TargetText(string path) => documents.GetValueOrDefault(path)?.Text ?? validationDocuments.GetValueOrDefault(path)?.Text ?? "";

    public IReadOnlyList<LanguageLocation> Definition(string path, int offset)
    {
        if (!documents.TryGetValue(path, out var doc)) return [];
        if (doc.Script != null)
        {
            var key = scriptReferenceLengths.Keys.Where(k => k.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && k.Start <= offset && offset < k.Start + scriptReferenceLengths[k])
                .OrderByDescending(k => k.Start).FirstOrDefault();
            if (key.Path != null) return definitions[key].Take(RecoilLanguage.MaximumResults).ToArray();
        }
        var item = doc.Tokens.FirstOrDefault(t => t.Span.Start <= offset && t.Span.End > offset);
        if (item == null) return [];
        return definitions.GetValueOrDefault((doc.Path, item.Span.Start))?.Distinct().Take(RecoilLanguage.MaximumResults).ToArray() ?? [];
    }
    public IReadOnlyList<LanguageCompletion> Complete(string path, int offset)
    {
        if (!documents.TryGetValue(path, out var doc)) return [];
        if (doc.Script != null) return CompleteGameGen(doc, offset);
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        if (RecoilLanguage.InComment(doc, offset)) return [];
        if (TupleCompletions(doc, offset) is { } tuple) return tuple;
        var record = doc.Records.Where(r => r.Value != null && doc.Span(r.Value).Start <= offset &&
            (doc.Span(r.Value).End > offset || offset == doc.Text.Length && doc.CompletionText != null && doc.spans[r.Value.Id].End > offset))
            .MinBy(r => doc.Span(r.Value!).Length);
        if (record != null && RecoilLanguage.AcceptsReferenceCompletion(doc, offset)
            && referenceNames.TryGetValue((doc.Path, doc.Span(record.KeyNode).Start), out var names))
        {
            string prefix = RecoilLanguage.CompletionPrefix(doc, offset);
            var span = RecoilLanguage.CompletionSpan(doc, offset);
            return names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(RecoilLanguage.MaximumResults)
                .Select(n => new LanguageCompletion(n, "Project reference", ZrdText.Scalar(new(Guid.Empty, ZrdKind.String, 0, n, [])), "reference", ReplacementSpan: span)).ToArray();
        }
        return RecoilLanguage.Complete(doc, offset);
    }

    private void Resolve(SourceBuilder.Snapshot snapshot, bool deep, CancellationToken token, bool missionAnalysis)
    {
        if (missionAnalysis || deep) ResolveGameGen(snapshot, token);
        // Resource declaration indices are project-local; common resource precedence is applied at use sites below.
        var resources = new Dictionary<string, List<(string Name, LanguageLocation Location)>>(StringComparer.Ordinal);
        void Declare(string kind, string name, LanguageDocument doc, ZrdNode node)
        {
            if (!resources.TryGetValue(kind, out var list)) resources.Add(kind, list = []);
            if (list.Count < 50_000) list.Add((name, new(doc.Path, doc.Span(node))));
        }
        foreach (var doc in documents.Values)
        {
            foreach (var r in doc.Records)
            {
                token.ThrowIfCancellationRequested();
                if (doc.Family == "weapons" && r.Parent?.Key == "BALLISTICS") Declare("weapon", r.Key, doc, r.KeyNode);
                if (doc.Family == "effects" && r.Key == "NAME" && r.Scalars.FirstOrDefault() is { Kind: ZrdKind.String } effect) Declare("effect", effect.Text, doc, effect);
                if (r.Key.StartsWith("MSG_", StringComparison.Ordinal) && r.Value != null) Declare("message", r.Key, doc, r.KeyNode);
            }
            if (doc.Family == "sounds" && doc.Root != null) SoundRows(doc.Root);
            void SoundRows(ZrdNode node)
            {
                token.ThrowIfCancellationRequested();
                if (node.Kind != ZrdKind.Array) return;
                if (node.Children.Count >= 2 && node.Children[0] is { Kind: ZrdKind.String } alias && node.Children[1] is { Kind: ZrdKind.String } wave && wave.Text.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) Declare("sound", alias.Text, doc, alias);
                foreach (var child in node.Children) if (child.Kind == ZrdKind.Array) SoundRows(child);
            }
        }
        var commonMembers = documents.Keys.Where(p => p.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents.Values)
            foreach (var r in doc.Records)
            {
                var field = RecoilLanguage.Field(doc, r);
                if (field?.Reference is not ("sound" or "effect" or "weapon" or "message") || !resources.TryGetValue(field.Reference, out var all)) continue;
                var available = all.Where(x => VisibleFrom(doc.Path, x.Location.Path, commonMembers)).ToArray();
                referenceNames[(doc.Path, doc.Span(r.KeyNode).Start)] = available.Select(x => x.Name).Distinct(StringComparer.Ordinal).Take(MaximumReferenceNames).ToArray();
                if (r.Scalars.FirstOrDefault() is { Kind: ZrdKind.String } value)
                    Link(doc, value, available.Where(x => x.Name == value.Text).Select(x => x.Location), missing: false);
            }

        var files = snapshot.Files();
        var completionFiles = new CompletionFiles(files, documents);
        ResolveNativeReferences(token);
        ResolveMotionParts(snapshot, token);
        foreach (var rootDoc in documents.Values.Where(d => d.Path.EndsWith("/zrdr/anim.zad", StringComparison.OrdinalIgnoreCase) && !IsCommon(d.Path)))
        {
            token.ThrowIfCancellationRequested();
            string mission = Mission(rootDoc.Path);
            AnimationDefinitionSet set;
            try { set = AnimationDefinitionSet.Load(completionFiles, rootDoc.Path, Game, token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { rootDoc.Report("GAMEZ_PROJECT", "warning", "Animation context is incomplete: " + ex.Message, new(0, 0), mission); continue; }
            var entries = new List<(string Name, LanguageLocation Location)>();
            foreach (var def in set.Definitions)
            {
                if (!documents.TryGetValue(def.File, out var doc)) continue;
                var r = doc.Records.Where(r => r.Key == "ANIMATION_DEFINITION").ElementAtOrDefault(def.Ordinal);
                if (r == null) continue;
                var names = doc.Records.Where(x => ReferenceEquals(x.Parent, r)).ToArray();
                var name = names.FirstOrDefault(x => x.Key == "ANIMATION_NAME") ?? names.FirstOrDefault(x => x.Key == "NAME");
                if (name != null)
                    foreach (var value in (name.Key == "ANIMATION_NAME" ? name.Scalars.Take(1) : name.Scalars).Where(v => v.Kind == ZrdKind.String))
                        entries.Add((value.Text, new(doc.Path, doc.Span(value), mission)));
            }
            foreach (string path in set.Files.Concat(documents.Keys.Where(p => (Mission(p).Equals(mission, StringComparison.OrdinalIgnoreCase) || IsCommon(p)) && documents[p].Family is "startanims" or "player" or "turrets")).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!documents.TryGetValue(path, out var doc)) continue;
                foreach (var r in doc.Records)
                {
                    token.ThrowIfCancellationRequested();
                    var field = RecoilLanguage.Field(doc, r);
                    if (field?.Reference == "animation")
                    {
                        MergeNames(doc, r, entries.Select(e => e.Name));
                        foreach (var value in (doc.Family == "startanims" ? r.Values.SelectMany(v => v.Kind == ZrdKind.Array ? v.Children.Take(1) : [v]) : r.Scalars).Where(v => v.Kind == ZrdKind.String))
                            Link(doc, value, entries.Where(e => Wildcard(e.Name, value.Text)).Select(e => e.Location), missing: true, mission);
                    }
                    else if (field?.Reference == "file")
                    {
                        try
                        {
                            if (r.FirstText is not { } name || r.Scalars.FirstOrDefault() is not { } fileNode) continue;
                            var resolved = FileNames(name).Select(n => set.Resolve(n, path)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
                            Link(doc, fileNode, resolved.Select(p => new LanguageLocation(p, AnimationTarget(p, r.Parent?.FirstText), mission)), missing: true, mission);
                        }
                        catch (InvalidDataException ex) { doc.Report("GAMEZ_REFERENCE", "warning", ex.Message, doc.Span(r.KeyNode), mission); }
                    }
                    if (r.Key == "NAME" && r.Parent?.Key == "OBJECT_MOTION_SI_SCRIPT" && r.Scalars.FirstOrDefault() is { Kind: ZrdKind.String } track
                        && doc.Records.FirstOrDefault(s => ReferenceEquals(s.Parent, r.Parent) && s.Key == "SCRIPT_FILENAME")?.FirstText is { } scriptName)
                    {
                        var targets = FileNames(scriptName).Select(n => set.Resolve(n, path)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                            .SelectMany(p => animationIndexes.TryGetValue(p, out var index) ? index.Tracks.Where(t => t.Name == track.Text || index.Kind == "zan-legacy" && index.Tracks.Count == 1 && t.Name.Length == 0).Select(t => new LanguageLocation(p, t.Span, mission)) : []);
                        Link(doc, track, targets, missing: false, mission);
                    }
                    else if (field?.Reference == "sequence")
                    {
                        var owner = r.Parent; while (owner != null && owner.Key != "ANIMATION_DEFINITION") owner = owner.Parent;
                        var sequences = doc.Records.Where(x => x.Key == "SEQUENCE_DEFINITION" && ReferenceEquals(x.Parent, owner));
                        var names = sequences.Select(s => doc.Records.LastOrDefault(x => ReferenceEquals(x.Parent, s) && x.Key == "NAME")).Where(n => n?.FirstText != null).ToArray();
                        MergeNames(doc, r, names.Select(n => n!.FirstText!));
                        if (r.Scalars.FirstOrDefault() is { Kind: ZrdKind.String } seq)
                            Link(doc, seq, names.Where(n => n!.FirstText == seq.Text).Select(n => new LanguageLocation(path, doc.Span(n!.Scalars.First()), mission)), true, mission);
                    }
                }
            }
            if (!deep && !(missionAnalysis && documents.ContainsKey($"gamegen/{mission}.gs"))) continue;
            try
            {
                // Reuse the source assembler and compiler, never run gamegen executables or export archives.
                var world = GameGenWorldCache.World(Root, mission, files, token, Game);
                var evaluated = AnimationCompiler.Evaluate(files, rootDoc.Path, world.World.Nodes.Select(n => n.Name).ToArray(), Game == GameVariant.MechWarrior3 ? null : snapshot.Effects(mission, token), token, game: Game);
                foreach (string warning in evaluated.Warnings.Take(RecoilLanguage.MaximumDiagnostics)) rootDoc.Report("GAMEZ_COMPILER", "warning", warning, new(0, 0), mission);
                if (evaluated.EngineRejection is { } rejection) rootDoc.Report("GAMEZ_ENGINE", "error", rejection, new(0, 0), mission);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { rootDoc.Report("GAMEZ_PROJECT", "error", "World binding validation: " + ex.Message, new(0, 0), mission); }
        }
    }
    private void MergeNames(LanguageDocument doc, LanguageRecord record, IEnumerable<string> names)
    {
        var key = (doc.Path, doc.Span(record.KeyNode).Start);
        referenceNames[key] = (referenceNames.GetValueOrDefault(key) ?? []).Concat(names).Distinct(StringComparer.Ordinal).Take(MaximumReferenceNames).ToArray();
    }
    private void Link(LanguageDocument doc, ZrdNode node, IEnumerable<LanguageLocation> targets, bool missing, string? mission = null)
    {
        var key = (doc.Path, doc.Span(node).Start);
        if (!definitions.TryGetValue(key, out var found)) definitions.Add(key, found = []);
        var next = targets.Take(RecoilLanguage.MaximumResults).ToArray();
        foreach (var location in next) if (found.Count < RecoilLanguage.MaximumResults && !found.Contains(location)) found.Add(location);
        if (doc.Complete && missing && next.Length == 0 && !node.Text.Contains('*')) doc.Report("GAMEZ_REFERENCE", "warning", $"Cannot resolve '{RecoilLanguage.Shown(node.Text)}'" + (mission == null ? "." : $" in {mission}."), doc.Span(node), mission);
    }
    private static bool Wildcard(string pattern, string name)
    {
        if (pattern.Length != name.Length) return false;
        for (int i = 0; i < name.Length; i++) if (pattern[i] != name[i] && !(pattern[i] == '*' && char.IsAsciiDigit(name[i]))) return false;
        return true;
    }
    private static IEnumerable<string> FileNames(string pattern)
    {
        if (pattern.Count(c => c == '*') > 3) throw new InvalidDataException("Template filename has more than three wildcard digits; use project validation for binding checks.");
        int at = pattern.IndexOf('*');
        if (at < 0) { yield return pattern; yield break; }
        for (char digit = '0'; digit <= '9'; digit++)
            foreach (string candidate in FileNames(pattern[..at] + digit + pattern[(at + 1)..])) yield return candidate;
    }
    private static string Mission(string path) => path.Split('/').ElementAtOrDefault(1) ?? "";
    private static bool IsCommon(string path) => Mission(path).Equals("common", StringComparison.OrdinalIgnoreCase);
    private sealed class CompletionFiles(IProjectFiles source, IReadOnlyDictionary<string, LanguageDocument> docs) : IProjectFiles
    {
        public GameVariant Game => source.Game;
        public bool Exists(string relative) => source.Exists(relative);
        public bool FolderExists(string relative) => source.FolderExists(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            if (docs.TryGetValue(relative, out var doc) && doc.CompletionText is { } text)
            {
                limits.CheckPrefix(System.Text.Encoding.Latin1.GetBytes(text.AsSpan(0, Math.Min(20, text.Length)).ToString()), text.Length);
                return ZrdTextSyntax.Encode(text);
            }
            return source.Read(relative, token, limits);
        }
    }
}
