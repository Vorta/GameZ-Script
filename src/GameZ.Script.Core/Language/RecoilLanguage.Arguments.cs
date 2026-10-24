using System.Globalization;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;

namespace GameZ.Script.Core.Language;

public static partial class RecoilLanguage
{
    public const int MaximumInlayHints = 4096;
    private static string FileFamily(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (!path.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase)) return "resource";
        if (name.Length == 6 && name.StartsWith("net_", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(4), out int net) && net is >= 1 and <= 99) return "network";
        return name switch
        {
            "vehicle" or "vehicle_easy" or "vehicle_hard" => "vehicle",
            "aiv" or "aiv_easy" or "aiv_hard" => "placements",
            "puppies" or "puppies_easy" or "puppies_hard" => "pickup-placements",
            "player" => "player", "weapons" => "weapons", "weather" => "weather",
            "objectives" => "objectives", "sounds" => "sounds", "effects" => "effects",
            "pickups" => "pickups", "ai" => "turrets", "hud" => "hud", "dialog" => "ui",
            "fonts" => "fonts", "startanims" => "startanims", _ => "resource"
        };
    }

    public static LanguageInlayHints InlayHints(LanguageDocument document, TextSpan range, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        List<LanguageInlayHint> items = [];
        foreach (var hint in document.Arguments.Hints.Values.OrderBy(h => h.Span.Start))
        {
            token.ThrowIfCancellationRequested();
            if (document.Arguments.HiddenHints.Contains(hint.Span.Start)) continue;
            if (hint.Span.Start < range.Start || hint.Span.Start >= range.End) continue;
            if (items.Count == MaximumInlayHints) return new(items, true);
            items.Add(hint);
        }
        return new(items, false);
    }

    private static (LanguageArgumentContainer Container, int Index)? ArgumentContext(LanguageDocument doc, int offset)
    {
        LanguageArgumentContainer? found = null;
        foreach (var container in doc.Arguments.Containers)
        {
            var raw = doc.spans[container.Node.Id];
            if (container.Node.Kind != ZrdKind.Array || offset <= raw.Start || offset >= raw.End) continue;
            if (found == null || raw.Length < doc.spans[found.Node.Id].Length) found = container;
        }
        if (found == null) return null;
        int index = 0;
        foreach (var child in found.Node.Children.Take(found.Count))
        {
            var span = doc.Span(child);
            if (offset < span.End || offset == span.End && child.Kind != ZrdKind.Array) break;
            index++;
        }
        return (found, index);
    }

    private static LanguageArgument? Parameter(LanguageArgumentContainer container, int index) =>
        index >= container.MaximumCount ? null : index < container.Parameters.Length ? container.Parameters[index]
        : container.Repeat && container.Parameters.Length > 0 ? container.Parameters[^1] : null;

    internal static bool AcceptsReferenceCompletion(LanguageDocument document, int offset) =>
        ArgumentContext(document, offset) is not { } context
        || Parameter(context.Container, context.Index) is { Type: "s" or "s?", InactiveReason: null };

    public static LanguageSignature? SignatureHelp(LanguageDocument document, int offset)
    {
        if (InComment(document, offset)) return null;
        if (ArgumentContext(document, offset) is not { } context) return null;
        var (container, index) = context;
        if (Parameter(container, index) == null || container.Parameters.All(p => p.Label == null)) return null;
        var parameters = container.Parameters.Select(p => new LanguageParameter(
            (p.Label ?? "unverified") + (p.Type.EndsWith('?') ? "?" : ""),
            p.Label == null ? "This argument's meaning has not been verified." : ArgumentDocumentation(p))).ToArray();
        string label = container.Name + " (" + string.Join(", ", parameters.Select(p => p.Label))
            + (container.Repeat ? ", …" : "") + ")";
        return new(label, "Argument roles; labels are editor annotations and are not written to the source.",
            parameters, Math.Min(index, parameters.Length - 1));
    }

    private static string ArgumentDocumentation(LanguageArgument spec) =>
        "**" + spec.Label + "**\n\n" + spec.Documentation
        + (spec.Type.Length > 0 ? "\n\nExpected: " + ReadableShape(spec.Type) + "." : "")
        + (spec.Children != null ? "\n\nTuple: " + string.Join(", ", spec.Children.Select(p => p.Label ?? "unverified")) + "." : "");

    private static void BindArguments(LanguageDocument doc, ZrdNode node, string name, LanguageArgument[] specs,
        CancellationToken token, bool repeat = false, int count = int.MaxValue, bool claimRemainder = true, string? fieldName = null)
    {
        token.ThrowIfCancellationRequested();
        var children = node.Kind == ZrdKind.Array ? node.Children : new[] { node };
        if (children.Count != 5 && specs.Any(s => s.Role == "image-flag"))
            specs = specs.Select(s => s.Role == "image-flag" ? s with { Label = "unused", Choices = null,
                Documentation = "Image visibility and centering flags are consumed only in exactly five-element image tuples." } : s).ToArray();
        int take = Math.Min(children.Count, count);
        if (node.Kind == ZrdKind.Array) doc.Arguments.Containers.Add(new(node, name, specs, repeat, take, !claimRemainder, count));
        void Claim(ZrdNode item)
        {
            token.ThrowIfCancellationRequested();
            doc.Arguments.Owned.Add(item.Id);
            doc.Arguments.OwnedOffsets.Add(doc.Span(item).Start);
            foreach (var child in item.Children) Claim(child);
        }
        for (int i = 0; i < (claimRemainder ? children.Count : take); i++)
        {
            var value = children[i];
            Claim(value);
            if (i >= take) continue;
            var spec = i < specs.Length ? specs[i] : repeat && specs.Length > 0 ? specs[^1] : null;
            if (spec == null) continue;
            if (value.Kind == ZrdKind.Array)
            {
                if (spec.Children != null) BindArguments(doc, value, spec.Label ?? name, spec.Children, token, spec.Repeat, fieldName: fieldName);
                continue;
            }
            if (spec.Children != null || spec.Label == null) continue;
            var span = doc.Span(value);
            if (span.Length == 0) continue;
            if (!doc.Arguments.Documentation.TryGetValue(spec, out string? description))
                doc.Arguments.Documentation.Add(spec, description = ArgumentDocumentation(spec));
            string label = spec.Label;
            if (spec.InactiveReason != null)
            {
                label = "unused";
                description += "\n\n" + spec.InactiveReason;
            }
            if (spec.Role == "network-link" && value.Kind == ZrdKind.Int && Numeric(value) is double link)
            {
                string target = link < 0 ? "no link" : "node_" + link.ToString("00", CultureInfo.InvariantCulture);
                label += " (" + target + ")";
                description += "\n\n" + (link < 0 ? "Negative values mean no outgoing link." : "Links to " + target + " in this network.");
            }
            if (spec.Role == "callback" && Numeric(value) is double id && id >= int.MinValue && id <= int.MaxValue)
                description += "\n\n" + CallbackHelp((int)id);
            doc.Arguments.Hints.TryAdd(span.Start, new(span, label + ":", description));
            if (fieldName != null && EquivalentHintLabel(label, fieldName)) doc.Arguments.HiddenHints.Add(span.Start);
        }
    }

    private static bool EquivalentHintLabel(string label, string fieldName)
    {
        var left = label.AsSpan().Trim().TrimEnd(':');
        var right = fieldName.AsSpan();
        int i = 0, j = 0;
        while (true)
        {
            while (i < left.Length && (left[i] == '_' || char.IsWhiteSpace(left[i]))) i++;
            while (j < right.Length && (right[j] == '_' || char.IsWhiteSpace(right[j]))) j++;
            if (i == left.Length || j == right.Length) return i == left.Length && j == right.Length;
            if (char.ToUpperInvariant(left[i++]) != char.ToUpperInvariant(right[j++])) return false;
        }
    }

    private static IReadOnlyList<LanguageCompletion>? ArgumentCompletions(LanguageDocument doc, int offset)
    {
        if (ArgumentContext(doc, offset) is { } context)
        {
            var (container, index) = context;
            if (container.TaggedTail && index >= container.Parameters.Length) return null;
            var spec = Parameter(container, index);
            if (spec == null) return [];
            if (spec.Role == "network-link")
                return new[] { new LanguageCompletion("-1", "No outgoing link", "-1", "enumMember", ArgumentDocumentation(spec)) }
                    .Concat(doc.Records.Where(r => r.Parent == null && NetworkNodeIndex(r.Key) is not null && r.Value?.Kind == ZrdKind.Array)
                        .Select(r => NetworkNodeIndex(r.Key)!.Value).Distinct().Order()
                        .Select(n => new LanguageCompletion(n.ToString(CultureInfo.InvariantCulture), $"Link to node_{n:00}",
                            n.ToString(CultureInfo.InvariantCulture), "enumMember", ArgumentDocumentation(spec)))).ToArray();
            if (spec.Role == "callback") return CallbackIds.Select(p => new LanguageCompletion(p.Key.ToString(CultureInfo.InvariantCulture),
                p.Value, p.Key.ToString(CultureInfo.InvariantCulture), "enumMember", CallbackHelp(p.Key))).ToArray();
            if (spec.Choices != null) return spec.Choices.Select(choice => new LanguageCompletion(choice,
                spec.Label ?? container.Name, choice, "enumMember", ArgumentDocumentation(spec))).ToArray();
            if (container.Name.StartsWith("node_", StringComparison.Ordinal) && index == 0 && container.Node.Children.Count == 0)
                return [new("node body", "Unused integer, XYZ position and three links", "$" + "{1:0} (" + "$" + "{2:0.0} " + "$" + "{3:0.0} " + "$" + "{4:0.0}) (" + "$" + "{5:-1} " + "$" + "{6:-1} " + "$" + "{7:-1})",
                    Documentation: "The first integer is unused. The node index comes from its name. Negative links mean no link.", IsSnippet: true)];
            if (spec.Children != null && index >= container.Node.Children.Count)
            {
                int tab = 0;
                string Snippet(LanguageArgument p) => p.Children != null
                    ? "(" + string.Join(" ", p.Children.Select(Snippet)) + ")"
                    : "$" + "{" + ++tab + ":" + (p.Role == "network-link" ? "-1"
                        : p.Choices?.FirstOrDefault() ?? (p.Type.StartsWith('s') ? "name" : p.Type.StartsWith('f') || p.Type.StartsWith('n') ? "0.0" : "0")) + "}";
                string snippet = Snippet(spec);
                return [new(spec.Label ?? "tuple", "Insert " + (spec.Label ?? "tuple"), snippet,
                    Documentation: ArgumentDocumentation(spec), IsSnippet: true)];
            }
            return [];
        }
        if (doc.Family == "network" && Context(doc, offset).All(p => p.Length == 0))
        {
            var items = Fields("network", "").Where(f => f.Suggested)
                .Select(f => new LanguageCompletion(f.Name, f.Description, f.Name, Documentation: FieldDocumentation(f))).ToList();
            var replacement = CompletionSpan(doc, offset);
            var editing = doc.Records.FirstOrDefault(r => r.Parent == null && doc.Span(r.KeyNode) == replacement);
            var used = doc.Records.Where(r => r.Parent == null && !ReferenceEquals(r, editing)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < 99; i++)
            {
                string name = $"node_{i:00}";
                if (!used.Contains(name) && editing?.Value?.Kind == ZrdKind.Array)
                {
                    items.Add(new(name, "Spatial network node", name,
                        Documentation: "Rename this node while preserving its existing body. The node index comes from the name."));
                    continue;
                }
                if (!used.Contains(name)) items.Add(new(name, "Spatial network node", name + " (" + "$" + "{1:0} (" + "$" + "{2:0.0} " + "$" + "{3:0.0} " + "$" + "{4:0.0}) (" + "$" + "{5:-1} " + "$" + "{6:-1} " + "$" + "{7:-1}))",
                    Documentation: "Unused integer, XYZ position, three integer link indices. The node index comes from the name.", IsSnippet: true));
            }
            return items;
        }
        return null;
    }

    private static int? NetworkNodeIndex(string name) => name.Length == 7 && name.StartsWith("node_", StringComparison.Ordinal)
        && char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6]) && int.TryParse(name.AsSpan(5), out int n) && n <= 98 ? n : null;
}
