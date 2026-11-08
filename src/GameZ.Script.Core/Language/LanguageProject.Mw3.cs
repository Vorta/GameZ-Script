using System.Text;
using System.Text.Json;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Language;

public sealed partial class LanguageProject
{
    private readonly Dictionary<(Guid Node, int Slot), IReadOnlyList<string>> tupleReferenceNames = [];
    private readonly Dictionary<string, IReadOnlyList<int>> modelLineStarts = new(StringComparer.OrdinalIgnoreCase);
    private TextSpan AnimationTarget(string path, string? name) => animationIndexes.TryGetValue(path, out var index)
        ? (index.Tracks.FirstOrDefault(t => t.Name == name) ?? (index.Tracks.Count == 1 ? index.Tracks[0] : null))?.Span ?? new(0, 0)
        : new(0, 0);

    private static string? NativeReader(string path)
    {
        string[] parts = path.Split('/');
        return parts.Length >= 6 && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("zrdr", StringComparison.OrdinalIgnoreCase) && parts[3].Equals("aipath", StringComparison.OrdinalIgnoreCase)
            ? string.Join('/', parts.Take(5)) : null;
    }
    private bool VisibleFrom(string origin, string target, IReadOnlySet<string> commonMembers)
    {
        if (target.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase)) return true;
        if (commonMembers.Contains(Path.GetFileName(target))) return false;
        if (Game != GameVariant.MechWarrior3) return Mission(origin).Equals(Mission(target), StringComparison.OrdinalIgnoreCase);
        if (!IsCommon(origin) && !Mission(origin).Equals(Mission(target), StringComparison.OrdinalIgnoreCase)) return false;
        return NativeReader(origin) is not { } reader || NativeReader(target) is not { } other || reader.Equals(other, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<LanguageDocument> NativeResources(string campaign, string? reader, string member)
    {
        // Archives are searched in open order: common, campaign, then the selected mission reader.
        var candidates = documents.Values.Where(d => Path.GetFileName(d.Path).Equals(member, StringComparison.OrdinalIgnoreCase)
            && (IsCommon(d.Path) || Mission(d.Path).Equals(campaign, StringComparison.OrdinalIgnoreCase)))
            .Where(d => NativeReader(d.Path) is not { } scope || reader != null && scope.Equals(reader, StringComparison.OrdinalIgnoreCase));
        return candidates.OrderBy(d => IsCommon(d.Path) ? 0 : NativeReader(d.Path) == null ? 1 : 2).ThenBy(d => d.Path, StringComparer.Ordinal);
    }

    private void ResolveNativeReferences(CancellationToken token)
    {
        if (Game != GameVariant.MechWarrior3) return;
        List<(string Kind, string Name, LanguageLocation Location)> declarations = [];
        void Declare(string kind, string name, LanguageDocument doc, TextSpan span)
        {
            if (declarations.Count < MaximumReferenceNames) declarations.Add((kind, name, new(doc.Path, span, NativeReader(doc.Path) ?? Mission(doc.Path))));
        }
        foreach (var doc in documents.Values)
        {
            token.ThrowIfCancellationRequested();
            string? kind = doc.Family switch { "network" or "waypoint-network" => "network", "region" => "region", "valves" => "valve", "targets" => "target", "mech-definition" => "mech", _ => null };
            if (kind is "network" or "region")
            {
                string member = Path.GetFileNameWithoutExtension(doc.Path);
                var named = doc.Records.FirstOrDefault(r => r.Parent == null && r.Key == "name");
                Declare(kind, member, doc, named?.FirstText == member ? doc.Span(named.Scalars.First()) : new(0, 0));
                if (named?.FirstText is { } name && name != member) Declare(kind, name, doc, doc.Span(named.Scalars.First()));
            }
            else if (kind == "mech") Declare(kind, Path.GetFileNameWithoutExtension(doc.Path).Replace("dfn_", "", StringComparison.Ordinal), doc, new(0, 0));
            else if (kind != null)
                foreach (var row in doc.Records.Where(r => r.Parent == null && r.Value != null)) Declare(kind, row.Key, doc, doc.Span(row.KeyNode));
        }
        foreach (var (path, index) in animationIndexes)
            if (index.Kind == "zan-motion" && declarations.Count < MaximumReferenceNames)
                declarations.Add(("motion", Path.GetFileNameWithoutExtension(path), new(path, index.Tracks.FirstOrDefault()?.Span ?? new(0, 0))));
        var commonMembers = documents.Keys.Where(IsCommon).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents.Values)
            foreach (var record in doc.Records)
            {
                token.ThrowIfCancellationRequested();
                string? kind = RecoilLanguage.Field(doc, record)?.Reference;
                if (kind is not ("network" or "region" or "valve" or "target" or "mech" or "motion")) continue;
                var available = declarations.Where(d => d.Kind == kind && VisibleFrom(doc.Path, d.Location.Path, commonMembers)).ToArray();
                MergeNames(doc, record, available.Select(d => d.Name));
                foreach (var value in record.Scalars.Where(n => n.Kind == ZrdKind.String))
                    Link(doc, value, available.Where(d => d.Name == value.Text).Select(d => d.Location), missing: false);
            }
        var scoped = new Dictionary<(string Kind, string Scope), (string Name, LanguageLocation Location)[]>();
        var scopedNames = new Dictionary<(string Kind, string Scope), IReadOnlyList<string>>();
        foreach (var doc in documents.Values)
            foreach (var container in doc.Arguments.Containers)
                for (int i = 0; i < container.Parameters.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string? role = container.Parameters[i].Role;
                    if (role is not ("network" or "region" or "valve" or "target" or "mech" or "motion")) continue;
                    string scope = (NativeReader(doc.Path) ?? Mission(doc.Path)).ToUpperInvariant();
                    if (!scoped.TryGetValue((role, scope), out var available))
                    {
                        scoped[(role, scope)] = available = declarations.Where(d => d.Kind == role && VisibleFrom(doc.Path, d.Location.Path, commonMembers)).Select(d => (d.Name, d.Location)).ToArray();
                        scopedNames[(role, scope)] = available.Select(d => d.Name).Distinct(StringComparer.Ordinal).Take(MaximumReferenceNames).ToArray();
                    }
                    tupleReferenceNames[(container.Node.Id, i)] = scopedNames[(role, scope)];
                    var values = container.Node.Kind == ZrdKind.Array ? container.Node.Children : [container.Node];
                    if (i >= values.Count) continue;
                    var value = values[i];
                    string? name = value.Kind == ZrdKind.String ? value.Text : role == "network" && value.Kind == ZrdKind.Int && unchecked((int)value.Bits) >= 0
                        ? (doc.Family == "waypoint-network" ? $"waynet_{unchecked((int)value.Bits)}" : $"net_{unchecked((int)value.Bits):00}") : null;
                    if (name != null) Link(doc, value, available.Where(d => d.Name == name).Select(d => d.Location), missing: false);
                }
    }

    private IReadOnlyList<LanguageCompletion>? TupleCompletions(LanguageDocument doc, int offset)
    {
        foreach (var container in doc.Arguments.Containers.OrderBy(c => doc.Span(c.Node).Length))
        {
            var span = doc.Span(container.Node);
            if (offset < span.Start || offset > span.End) continue;
            var values = container.Node.Kind == ZrdKind.Array ? container.Node.Children : [container.Node];
            int slot = 0; while (slot < values.Count && doc.Span(values[slot]).End < offset) slot++;
            if (!tupleReferenceNames.TryGetValue((container.Node.Id, slot), out var names)) continue;
            var replacement = RecoilLanguage.CompletionSpan(doc, offset); string prefix = RecoilLanguage.CompletionPrefix(doc, offset);
            return names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(RecoilLanguage.MaximumResults)
                .Select(n => new LanguageCompletion(n, "Project reference", ZrdText.Scalar(new(Guid.Empty, ZrdKind.String, 0, n, [])), "reference", ReplacementSpan: replacement)).ToArray();
        }
        return null;
    }

    private void ResolveMotionParts(SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        if (Game != GameVariant.MechWarrior3) return;
        var files = snapshot.Files();
        var models = SourceProject.Files(Root, "data/common/mechs/models", p => p.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase), snapshot.Added, token)
            .Where(snapshot.IncludesSource).ToArray();
        foreach (var (path, index) in animationIndexes.Where(p => p.Value.Kind == "zan-motion"))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(path);
            string? model = models.Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("mech_", StringComparison.Ordinal))
                .Where(p => name.StartsWith(Path.GetFileNameWithoutExtension(p)[5..] + "_", StringComparison.Ordinal))
                .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Length).FirstOrDefault();
            if (model == null) continue;
            IReadOnlyDictionary<string, TextSpan[]> nodes;
            try { nodes = ModelNodes(model, files, token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { continue; }
            foreach (var track in index.Tracks)
            {
                if (!nodes.TryGetValue(track.Name, out var targets)) continue;
                foreach (var target in targets) ScriptLink(path, track.Span, new(model, target));
            }
        }
    }
    private readonly Dictionary<string, IReadOnlyDictionary<string, TextSpan[]>> modelNodes = new(StringComparer.OrdinalIgnoreCase);
    private long modelNavigationBytes;
    private int modelNavigationNames;
    private IReadOnlyDictionary<string, TextSpan[]> ModelNodes(string path, IProjectFiles files, CancellationToken token)
    {
        if (modelNodes.TryGetValue(path, out var cached)) return cached;
        byte[] raw = files.Read(path, token, ProjectReadLimits.Model(MaximumTextBytes - modelNavigationBytes));
        modelNavigationBytes += raw.Length;
        var reader = new Utf8JsonReader(raw); Dictionary<string, List<TextSpan>> names = new(StringComparer.Ordinal);
        bool nextNodes = false, nextName = false; int nodesDepth = -1, previousByte = 0, characters = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("nodes")) nextNodes = true;
            else if (nextNodes && reader.TokenType == JsonTokenType.StartArray) { nodesDepth = reader.CurrentDepth; nextNodes = false; }
            else if (nodesDepth >= 0 && reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == nodesDepth) nodesDepth = -1;
            else if (nodesDepth >= 0 && reader.CurrentDepth == nodesDepth + 2 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("name")) nextName = true;
            else if (nextName)
            {
                nextName = false;
                if (reader.TokenType != JsonTokenType.String) continue;
                string name = reader.GetString()!;
                if (++modelNavigationNames > MaximumReferenceNames) throw new InvalidDataException("Model navigation name limit exceeded.");
                int at = checked((int)reader.TokenStartIndex), end = checked((int)reader.BytesConsumed);
                characters += Encoding.UTF8.GetCharCount(raw.AsSpan(previousByte, at - previousByte));
                int width = Encoding.UTF8.GetCharCount(raw.AsSpan(at, end - at));
                var span = new TextSpan(characters, width);
                characters += width; previousByte = end;
                if (!names.TryGetValue(name, out var locations)) names[name] = locations = [];
                if (locations.Count < RecoilLanguage.MaximumResults) locations.Add(span);
            }
        }
        string text = Encoding.UTF8.GetString(raw); List<int> lines = [0];
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines.Add(i + 1);
        modelLineStarts[path] = lines;
        return modelNodes[path] = names.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }
}
