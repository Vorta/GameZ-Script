using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GameZ.Script.Core.Animation;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Sources;

namespace GameZ.Script.Core.Language;

public sealed record ZanTrack(string Name, TextSpan Span, string Kind);
public sealed record ZanSourceIndex(string Kind, IReadOnlyList<ZanTrack> Tracks, IReadOnlyList<int> LineStarts);

/// <summary>Read-only source annotations over the three owned animation-script formats. Never compiles or rewrites a source.</summary>
public static class ZanLanguage
{
    private sealed record Word(string Text, TextSpan Span);
    private sealed record Line(int Start, int Length, int Number);
    private sealed class TrackOutline(string name, TextSpan selection, int start)
    {
        internal string Name = name;
        internal TextSpan Selection = selection;
        internal int Start = start, End = selection.End;
        internal List<LanguageSymbol> Frames = [];
    }
    private static readonly IReadOnlyDictionary<string, string> Help = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SI Animation Script"] = "Softimage SI scene animation. This syntax is shared by RECOIL and MW3; the owning workspace selects compiler arithmetic.",
        ["FRAMES"] = "Declared number of frame blocks, not the greatest Frame label. Sparse and reversed frame labels retain their authored order.",
        ["OBJECTS"] = "Declared number of distinct object tracks in the SI scene.",
        ["Frame"] = "SI frame label, starting at 1. Time is (label − 1) / SCRIPT_FRAME_RATE. Repeated and reversed authored frames are preserved.",
        ["Object"] = "Object track selected by NAME in OBJECT_MOTION_SI_SCRIPT. Each object pose supplies Scaling, Rotation and Translation.",
        ["Scaling"] = "XYZ scale factors; 1 is unchanged scale.",
        ["Rotation"] = "SI Euler XYZ angles in radians. These are three Euler values, not a quaternion.",
        ["Translation"] = "XYZ object translation in world units.",
        ["OBJECT"] = "Named legacy keyframe track. An unsectioned script supplies one track usable by any object.",
        ["FRAME"] = "Legacy frame label, starting at 0, at the definition's SCRIPT_FRAME_RATE. The last frame ends the motion; reversed segments are preserved.",
        ["POSITION"] = "XYZ translation in world units.",
        ["ROTATION"] = "Quaternion stored in W X Y Z order. Keep all four components; the quaternion must not be zero.",
        ["SCALE"] = "XYZ scale factors; 1 is unchanged scale.",
        ["VELOCITY"] = "XYZ translation rate in units per second. This key must also contain POSITION.",
        ["SPIN"] = "XYZ rotation vector in half-angle radians per second. This key must also contain ROTATION.",
        ["GROWTH"] = "XYZ scale change per second. This key must also contain SCALE.",
        ["MOTION"] = "MW3 version-4 sampled mech motion. Each PART has FRAME_COUNT + 1 samples, including a separate closing sample.",
        ["LOOP_SECONDS"] = "Positive finite playback loop duration in seconds.",
        ["FRAME_COUNT"] = "Number of playback frames. Each part retains one additional closing sample.",
        ["PART"] = "One named model-part track. Samples list translation XYZ followed by quaternion WXYZ.",
        ["NAME"] = "Model part matched by this sampled-motion track.",
        ["SAMPLES"] = "Ordered translation XYZ and quaternion WXYZ samples, including the separate closing sample."
    };

    public static LanguageDocument Analyze(string path, string text, CancellationToken token = default, GameVariant? game = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        token.ThrowIfCancellationRequested();
        var doc = new LanguageDocument(path, text);
        string kind = Kind(path, text, token);
        doc.Family = kind;
        doc.Game = game ?? (kind == "zan-motion" ? GameVariant.MechWarrior3 : GameVariant.Recoil);
        if (text.Length > SourceProject.MaximumSourceTextBytes)
        {
            doc.Report("GAMEZ_LIMIT", "information", "Document exceeds the 16 MiB analysis limit.", new(0, 0));
            return doc;
        }
        int invalid = text.AsSpan().IndexOfAnyExceptInRange('\0', '\xFF');
        if (invalid >= 0) doc.Report("GAMEZ_ENCODING", "error", "Animation source must be representable as Latin-1.", new(invalid, 1));
        int nul = text.IndexOf('\0');
        if (nul >= 0)
        {
            doc.Report("GAMEZ_BINARY", "information", "This appears to be binary data. Animation assistance requires text source.", new(nul, 1));
            return doc;
        }
        try
        {
            if (kind == "zan-motion") Motion(doc, token);
            else Lines(doc, kind == "zan-si", token);
        }
        catch (InvalidDataException ex)
        {
            doc.Report("GAMEZ_LIMIT", "information", ex.Message, new(0, 0));
        }
        doc.tokens.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return doc;
    }

    /// <summary>A bounded navigation index; no syntax trees, sample arrays or inlay annotations are retained.</summary>
    public static ZanSourceIndex Index(string path, string text, CancellationToken token = default, GameVariant? game = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        token.ThrowIfCancellationRequested();
        if (text.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException("Animation source exceeds its 16 MiB limit.");
        string kind = Kind(path, text, token);
        List<ZanTrack> tracks = [];
        List<int> starts = [];
        HashSet<string> siNames = new(StringComparer.Ordinal);
        TextSpan? implicitTrack = null;
        if (kind == "zan-motion")
        {
            Dictionary<Guid, TextSpan> spans = [];
            var tree = ZrdText.Parse(text, token, (node, span) =>
            {
                if (spans.Count >= RecoilLanguage.MaximumTokens) throw new InvalidDataException("Animation navigation token limit exceeded.");
                spans.Add(node.Id, span);
            });
            foreach (var (_, motion) in Pairs(Unwrap(tree)).Where(p => p.Key.Text == "MOTION"))
                foreach (var (_, part) in Pairs(motion).Where(p => p.Key.Text == "PART"))
                    foreach (var (_, value) in Pairs(part).Where(p => p.Key.Text == "NAME"))
                        if (value.Children.FirstOrDefault() is { Kind: ZrdKind.String } name)
                        {
                            token.ThrowIfCancellationRequested();
                            if (tracks.Count == RecoilLanguage.MaximumTokens) throw new InvalidDataException("Animation track index limit exceeded.");
                            tracks.Add(new(name.Text, spans[name.Id], "part"));
                        }
        }
        foreach (var line in TextLines(text, token))
        {
            if (starts.Count == RecoilLanguage.MaximumTokens) throw new InvalidDataException("Animation line index limit exceeded.");
            starts.Add(line.Start);
            if (kind == "zan-motion") continue;
            var words = kind == "zan-si" ? SiWords(text, line, token) : Words(text, line, token);
            if (kind == "zan-legacy" && words.Count > 0 && words[0].Text.Equals("FRAME", StringComparison.OrdinalIgnoreCase))
                implicitTrack ??= words[0].Span;
            if (words.Count < 2) continue;
            if (kind == "zan-si" && words[0].Text == "Object:" && siNames.Add(words[1].Text)
                || kind == "zan-legacy" && words[0].Text.Equals("OBJECT", StringComparison.OrdinalIgnoreCase))
            {
                if (tracks.Count == RecoilLanguage.MaximumTokens) throw new InvalidDataException("Animation track index limit exceeded.");
                tracks.Add(new(words[1].Text, words[1].Span, "object"));
            }
        }
        if (kind == "zan-legacy" && tracks.Count == 0 && implicitTrack is { } firstFrame)
            tracks.Add(new("", firstFrame, "object"));
        return new(kind, tracks.AsReadOnly(), starts.AsReadOnly());
    }

    public static bool InComment(LanguageDocument doc, int offset)
    {
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        int start = offset == 0 ? 0 : doc.Text.LastIndexOf('\n', offset - 1) + 1;
        if (doc.Family == "zan-si")
        {
            // SI accepts # in an object name. Only a line whose first nonblank is # is a comment.
            while (start < offset && char.IsWhiteSpace(doc.Text[start])) start++;
            return start < offset && doc.Text[start] == '#';
        }
        bool quoted = false, escaped = false;
        for (int at = start; at < offset; at++)
        {
            char c = doc.Text[at];
            if (escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '#') return true;
        }
        return false;
    }

    public static IReadOnlyList<LanguageCompletion> Complete(LanguageDocument doc, int offset)
    {
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        if (InComment(doc, offset)) return [];
        var word = doc.Tokens.FirstOrDefault(t => t.Span.Start <= offset && offset < t.Span.End && t.Kind != "delimiter")
            ?? doc.Tokens.LastOrDefault(t => t.Span.End == offset && t.Kind != "delimiter");
        var replacement = word?.Span ?? new TextSpan(offset, 0);
        string prefix = word == null ? "" : doc.Text.Substring(word.Span.Start, Math.Clamp(offset - word.Span.Start, 0, word.Span.Length));
        int lineStart = offset == 0 ? 0 : doc.Text.LastIndexOf('\n', offset - 1) + 1;
        string before = doc.Text[lineStart..Math.Max(lineStart, replacement.Start)].Trim();
        string[] names;
        if (doc.Family == "zan-si") names = ["Frame:", "Object:", "Scaling:", "Rotation:", "Translation:", "FRAMES:", "OBJECTS:"];
        else if (doc.Family == "zan-motion")
        {
            var container = doc.Arguments.Containers.Where(c => doc.spans[c.Node.Id].Start < offset && offset < doc.spans[c.Node.Id].End)
                .MinBy(c => doc.spans[c.Node.Id].Length);
            if (container?.Name is "sample" or "closing sample" or "NAME" or "LOOP_SECONDS" or "FRAME_COUNT") return [];
            string context = MotionContext(doc, offset);
            if (context == "SAMPLES")
                return prefix.Length == 0 ? [new("sample", "Translation XYZ and quaternion WXYZ",
                    "($" + "{1:0.0} $" + "{2:0.0} $" + "{3:0.0} $" + "{4:1.0} $" + "{5:0.0} $" + "{6:0.0} $" + "{7:0.0})",
                    Documentation: Help["SAMPLES"], ReplacementSpan: replacement, IsSnippet: true)] : [];
            names = context switch
            {
                "PART" => ["NAME", "SAMPLES"],
                "MOTION" => ["LOOP_SECONDS", "FRAME_COUNT", "PART"],
                _ => ["MOTION"]
            };
        }
        else
        {
            if (before.Equals("OBJECT", StringComparison.OrdinalIgnoreCase)) return [];
            names = before.Length == 0 ? ["OBJECT", "FRAME"] : ["POSITION", "ROTATION", "SCALE", "VELOCITY", "SPIN", "GROWTH"];
            // A partial first header is useful in an empty new ZAN.
            if (doc.Text.Trim().Length == 0) names = ["OBJECT", "FRAME", "SI Animation Script", "MOTION"];
        }
        if (doc.Family == "zan-si" && before.Length > 0) return [];
        return names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(RecoilLanguage.MaximumResults)
            .Select(n => new LanguageCompletion(n, Help.GetValueOrDefault(n.TrimEnd(':')) ?? "Animation source field.", n,
                Documentation: Help.GetValueOrDefault(n.TrimEnd(':')), ReplacementSpan: replacement)).ToArray();
    }

    public static LanguageHover? Hover(LanguageDocument doc, int offset)
    {
        if (InComment(doc, offset)) return null;
        var item = doc.Tokens.FirstOrDefault(t => t.Span.Start <= offset && offset < t.Span.End);
        if (item == null) return null;
        if (doc.Arguments.Hints.TryGetValue(item.Span.Start, out var hint)) return new(hint.Documentation, item.Span);
        return Help.TryGetValue(item.Text.TrimEnd(':'), out string? help) ? new(help, item.Span) : null;
    }

    private static string Kind(string path, string text, CancellationToken token)
    {
        int at = 0;
        // Follow the source reader's leading whitespace/comment/delimiter boundaries.
        // In particular an opening array may be followed immediately by a # comment.
        while (at < text.Length)
        {
            token.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(text[at]) || text[at] == '(') { at++; continue; }
            if (text[at] == '#')
            {
                while (at < text.Length && text[at] is not ('\r' or '\n'))
                { if ((at & 4095) == 0) token.ThrowIfCancellationRequested(); at++; }
                continue;
            }
            var value = text.AsSpan(at);
            if (value.StartsWith("SI Animation Script")) return "zan-si";
            if (value.StartsWith("MOTION") && (value.Length == 6 || char.IsWhiteSpace(value[6]) || value[6] is '(' or '#')) return "zan-motion";
            return "zan-legacy";
        }
        return path.Replace('\\', '/').Contains("/mechs/motions/", StringComparison.OrdinalIgnoreCase) ? "zan-motion" : "zan-legacy";
    }

    private static string MotionContext(LanguageDocument doc, int offset)
    {
        string name = ""; int length = int.MaxValue;
        if (doc.Root != null) Visit(doc.Root);
        return name;
        void Visit(ZrdNode node)
        {
            foreach (var (key, value) in Pairs(node))
            {
                var span = doc.spans[value.Id];
                if (offset > span.Start && offset < span.End && span.Length < length)
                { name = key.Text; length = span.Length; }
                Visit(value);
            }
        }
    }

    private static IEnumerable<Line> TextLines(string text, CancellationToken token)
    {
        int start = 0, number = 1;
        while (start <= text.Length)
        {
            token.ThrowIfCancellationRequested();
            int end = start;
            while (end < text.Length && text[end] != '\n')
            { if ((end & 4095) == 0) token.ThrowIfCancellationRequested(); end++; }
            yield return new(start, end - start, number++);
            if (end == text.Length) yield break;
            start = end + 1;
        }
    }

    private static List<Word> Words(string text, Line line, CancellationToken token, bool hashComments = true)
    {
        List<Word> words = [];
        for (int at = line.Start; at < line.Start + line.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (hashComments && text[at] == '#') break;
            if (char.IsWhiteSpace(text[at]) || text[at] == '\uFEFF') { at++; continue; }
            int start = at;
            while (at < line.Start + line.Length && !char.IsWhiteSpace(text[at]) && (!hashComments || text[at] != '#'))
            {
                if (at - start >= AnimationScriptLexing.MaximumTokenBytes) throw new InvalidDataException("An animation token exceeds 4,096 characters.");
                at++;
            }
            words.Add(new(text[start..at], new(start, at - start)));
        }
        return words;
    }

    private static List<Word> SiWords(string text, Line line, CancellationToken token)
    {
        // The owned SI reader splits a Name:value line at the colon, independently of spaces.
        // Preserve that boundary in both annotations and the closed-source navigation index.
        int start = line.Start, end = line.Start + line.Length;
        while (start < end && char.IsWhiteSpace(text[start])) { token.ThrowIfCancellationRequested(); start++; }
        if (start == end || text[start] == '#') return [];
        int colon = -1;
        for (int at = start; at < end; at++)
        {
            if ((at & 4095) == 0) token.ThrowIfCancellationRequested();
            if (text[at] == '#') break;
            if (text[at] == ':') { colon = at; break; }
        }
        if (colon < 0) return Words(text, line, token);
        if (colon - start >= AnimationScriptLexing.MaximumTokenBytes) throw new InvalidDataException("An animation token exceeds 4,096 characters.");
        List<Word> words = [new(text[start..(colon + 1)], new(start, colon + 1 - start))];
        words.AddRange(Words(text, new(colon + 1, end - colon - 1, line.Number), token, hashComments: false));
        return words;
    }

    private static void Lines(LanguageDocument doc, bool si, CancellationToken token)
    {
        Dictionary<string, TrackOutline> outlines = new(StringComparer.Ordinal);
        TrackOutline? track = null;
        string frame = si ? "1" : "0";
        foreach (var line in TextLines(doc.Text, token))
        {
            var words = si ? SiWords(doc.Text, line, token) : Words(doc.Text, line, token);
            if (words.Count == 0) continue;
            foreach (var word in words) AddToken(doc, word.Text, word.Span, "word");
            if (si)
            {
                string key = words[0].Text.TrimEnd(':');
                var values = words.Skip(1).ToArray();
                if (key is "Frame") frame = values.FirstOrDefault()?.Text ?? "?";
                if (key == "Object" && values.FirstOrDefault() is { } named)
                {
                    if (!outlines.TryGetValue(named.Text, out track))
                    {
                        track = new(named.Text, named.Span, line.Start);
                        if (outlines.Count < RecoilLanguage.MaximumResults) outlines.Add(named.Text, track);
                    }
                    FrameSymbol(track, frame, line, named.Span);
                }
                LanguageArgument[]? specs = key switch
                {
                    "Frame" => [A("frame (1-based)", "n", Help["Frame"])],
                    "Object" => [A("object track", "s", Help["Object"])],
                    "FRAMES" => [A("frame block count", "n", Help["FRAMES"])],
                    "OBJECTS" => [A("object track count", "n", Help["OBJECTS"])],
                    "Scaling" => XYZ("scale", "Scale factor; 1 is unchanged scale."),
                    "Rotation" => XYZ("rotation", "SI Euler angle in radians.", " (rad)"),
                    "Translation" => XYZ("translation", "Object translation in world units."),
                    _ => null
                };
                if (specs != null) Bind(doc, words[0].Text, words[0].Span.End - 1, line.Start + line.Length + 1, values, specs, token);
            }
            else
            {
                if (words[0].Text.Equals("OBJECT", StringComparison.OrdinalIgnoreCase) && words.Count >= 2)
                {
                    var named = words[1];
                    track = new(named.Text, named.Span, line.Start);
                    if (outlines.Count < RecoilLanguage.MaximumResults) outlines.TryAdd(named.Text, track);
                    Bind(doc, "OBJECT", words[0].Span.End, line.Start + line.Length + 1, [named], [A("object track", "s", Help["OBJECT"])], token);
                    continue;
                }
                if (words[0].Text.Equals("FRAME", StringComparison.OrdinalIgnoreCase))
                {
                    if (track == null)
                    {
                        track = new("Unsectioned track", words[0].Span, line.Start);
                        outlines.TryAdd("", track);
                    }
                    if (words.Count > 1) frame = words[1].Text;
                    FrameSymbol(track, frame, line, words[0].Span);
                    int at = 0;
                    while (at < words.Count)
                    {
                        var key = words[at++];
                        string name = key.Text.ToUpperInvariant();
                        int next = at;
                        while (next < words.Count && !IsLegacyKeyword(words[next].Text)) next++;
                        var values = words.Skip(at).Take(next - at).ToArray();
                        var specs = name switch
                        {
                            "FRAME" => [A("frame (0-based)", "n", Help["FRAME"])],
                            "POSITION" => XYZ("translation", Help["POSITION"]),
                            "SCALE" => XYZ("scale", Help["SCALE"]),
                            "ROTATION" => WXYZ(),
                            "VELOCITY" => XYZ("velocity", Help["VELOCITY"]),
                            "SPIN" => XYZ("spin", Help["SPIN"]),
                            "GROWTH" => XYZ("growth", Help["GROWTH"]),
                            _ => Array.Empty<LanguageArgument>()
                        };
                        if (specs.Length > 0) Bind(doc, name, key.Span.End,
                            next < words.Count ? words[next].Span.Start : line.Start + line.Length + 1, values, specs, token);
                        at = next;
                    }
                }
            }
            if (track != null) track.End = line.Start + line.Length;
        }
        doc.Symbols = outlines.Values.Select(t => new LanguageSymbol(RecoilLanguage.Shown(t.Name), "class",
            new(t.Start, Math.Max(t.Selection.Length, t.End - t.Start)), t.Selection, t.Frames)).ToArray();
        ValidateLineSource(doc, si, token);
    }

    private static bool IsLegacyKeyword(string text) => text.ToUpperInvariant() is "FRAME" or "POSITION" or "ROTATION" or "SCALE" or "VELOCITY" or "SPIN" or "GROWTH";
    private static void FrameSymbol(TrackOutline track, string frame, Line line, TextSpan selection)
    {
        if (track.Frames.Count < RecoilLanguage.MaximumResults)
            track.Frames.Add(new("Frame " + RecoilLanguage.Shown(frame), "property", new(line.Start, line.Length), selection, []));
    }

    private static void ValidateLineSource(LanguageDocument doc, bool si, CancellationToken token)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(doc.Text);
        try
        {
            if (si) _ = SiAnimationScript.Parse(bytes, doc.Path, token);
            else _ = AnimationScript.Parse(bytes, doc.Path, token);
            doc.Complete = true;
        }
        catch (InvalidDataException ex)
        {
            var match = Regex.Match(ex.Message, @", line (\d+):", RegexOptions.CultureInvariant);
            int lineNumber = match.Success && int.TryParse(match.Groups[1].Value, out int number) ? number : 1;
            var line = TextLines(doc.Text, token).FirstOrDefault(l => l.Number == lineNumber) ?? new Line(0, 0, 1);
            if (!doc.Diagnostics.Any(d => d.Severity == "error" && d.Span.Start >= line.Start && d.Span.Start <= line.Start + line.Length))
                doc.Report("ZAN_SYNTAX", "error", ex.Message, new(line.Start, Math.Min(line.Length, 1)));
        }
    }

    private static void Motion(LanguageDocument doc, CancellationToken token)
    {
        int depth = 0, errorAt = 0;
        string? message = null;
        ZrdNode? tree = null;
        void Span(ZrdNode node, TextSpan span)
        {
            if (doc.spans.Count >= RecoilLanguage.MaximumTokens) throw new InvalidDataException("Animation token limit exceeded; analysis is incomplete.");
            doc.spans[node.Id] = span;
            if (node.Kind != ZrdKind.Array) AddToken(doc, node.Kind == ZrdKind.String ? node.Text : node.Value, span, node.Kind == ZrdKind.String ? "string" : "number");
        }
        try
        {
            tree = ZrdText.Parse(doc.Text, token, Span, issue: (at, why) => { errorAt = at; message = why; },
                delimiter: (kind, at) => { depth += kind == '(' ? 1 : -1; AddToken(doc, kind.ToString(), new(at, 1), "delimiter"); });
            doc.Complete = true;
        }
        catch (InvalidDataException ex)
        {
            if (message == null) throw;
            doc.Report("ZAN_SYNTAX", "error", message ?? ex.Message, new(Math.Min(errorAt, doc.Text.Length), errorAt < doc.Text.Length ? 1 : 0));
            if (message == "Missing ')'." && depth is > 0 and <= 128)
            {
                doc.spans.Clear();
                try { tree = ZrdText.Parse(doc.Text + "\n" + new string(')', depth), token, (node, span) => doc.spans[node.Id] = span); }
                catch (InvalidDataException) { }
            }
        }
        if (tree == null) return;
        doc.Root = tree;
        foreach (var issue in MotionSourceRules.Validate(tree, token))
            doc.Report(issue.Code, "error", issue.Message, NodeSpan(doc, issue.Node));
        var entries = Pairs(Unwrap(tree)).Where(p => p.Key.Text == "MOTION").ToArray();
        var entry = entries.FirstOrDefault();
        if (entry.Value == null) return;
        var motion = entry.Value;
        var fields = Pairs(motion).ToArray();
        ZrdNode? Field(string name) => fields.FirstOrDefault(p => p.Key.Text == name).Value;
        var duration = Field("LOOP_SECONDS");
        var count = Field("FRAME_COUNT");
        Header(duration, "LOOP_SECONDS", "loop duration (s)", Help["LOOP_SECONDS"]);
        Header(count, "FRAME_COUNT", "playback frame count", Help["FRAME_COUNT"]);
        double? frames = OneNumber(count);
        var parts = fields.Where(p => p.Key.Text == "PART").ToArray();
        List<LanguageSymbol> symbols = [];
        foreach (var (partKey, part) in parts)
        {
            token.ThrowIfCancellationRequested();
            var partFields = Pairs(part).ToArray();
            var name = partFields.FirstOrDefault(p => p.Key.Text == "NAME").Value;
            var samples = partFields.FirstOrDefault(p => p.Key.Text == "SAMPLES").Value;
            var named = name?.Children.FirstOrDefault();
            if (name != null) BindNodes(doc, "NAME", name, name.Children, [A("model part", "s", Help["NAME"])], token);
            string shown = named?.Kind == ZrdKind.String ? RecoilLanguage.Shown(named.Text) : "(unnamed part)";
            if (symbols.Count < RecoilLanguage.MaximumResults) symbols.Add(new(shown, "class",
                new(NodeSpan(doc, partKey).Start, Math.Max(0, NodeSpan(doc, part).End - NodeSpan(doc, partKey).Start)),
                NodeSpan(doc, named ?? partKey), []));
            if (samples?.Kind != ZrdKind.Array) continue;
            for (int row = 0; row < samples.Children.Count; row++)
            {
                token.ThrowIfCancellationRequested();
                var sample = samples.Children[row];
                bool closing = frames == row;
                var specs = XYZ("translation", "Sampled translation in world units.").Concat(WXYZ())
                    .Select(p => closing ? p with { Documentation = p.Documentation + " This separately retained closing sample ends the loop." } : p).ToArray();
                if (sample.Kind == ZrdKind.Array)
                    BindNodes(doc, closing ? "closing sample" : "sample", sample, sample.Children, specs, token, validateNumbers: false);
            }
        }
        doc.Symbols = symbols;
        void Header(ZrdNode? value, string key, string label, string help)
        {
            if (value != null) BindNodes(doc, key, value, value.Children, [A(label, "n", help)], token, validateNumbers: false);
        }
    }

    private static IEnumerable<(ZrdNode Key, ZrdNode Value)> Pairs(ZrdNode node)
    {
        for (int i = 0; i + 1 < node.Children.Count; i++)
            if (node.Children[i].Kind == ZrdKind.String && node.Children[i + 1].Kind == ZrdKind.Array)
                yield return (node.Children[i], node.Children[++i]);
    }
    private static ZrdNode Unwrap(ZrdNode tree)
    {
        while (tree.Children.Count == 1 && tree.Children[0].Kind == ZrdKind.Array) tree = tree.Children[0];
        return tree;
    }
    private static double? OneNumber(ZrdNode? node) => node is { Children.Count: 1 } ? Number(node.Children[0]) : null;
    private static double? Number(ZrdNode node) => node.Kind == ZrdKind.Int ? unchecked((int)node.Bits)
        : node.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(node.Bits) : null;
    private static TextSpan NodeSpan(LanguageDocument doc, ZrdNode node) => doc.Span(node);
    private static LanguageArgument A(string name, string type, string help) => new(name, type, help);
    private static LanguageArgument[] XYZ(string prefix, string help, string suffix = "") =>
        new[] { "x", "y", "z" }.Select(axis => A(prefix + " " + axis + suffix, "n", help)).ToArray();
    private static LanguageArgument[] WXYZ() => new[] { "w", "x", "y", "z" }
        .Select(axis => A("quaternion " + axis, "n", "Rotation quaternion component; authored storage order is W X Y Z.")).ToArray();
    private static void AddToken(LanguageDocument doc, string text, TextSpan span, string kind)
    {
        if (doc.tokens.Count >= RecoilLanguage.MaximumTokens) throw new InvalidDataException("Animation token limit exceeded; analysis is incomplete.");
        doc.tokens.Add(new(span, text, kind));
    }
    private static void Bind(LanguageDocument doc, string name, int start, int end, IReadOnlyList<Word> words, LanguageArgument[] specs, CancellationToken token)
    {
        List<ZrdNode> children = [];
        foreach (var word in words)
        {
            bool number = float.TryParse(word.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value);
            var node = new ZrdNode(Guid.NewGuid(), number ? ZrdKind.Float : ZrdKind.String, number ? BitConverter.SingleToUInt32Bits(value) : 0, number ? "" : word.Text, []);
            doc.spans[node.Id] = word.Span;
            children.Add(node);
        }
        var container = new ZrdNode(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
        doc.spans[container.Id] = new(start, Math.Max(1, end - start));
        BindNodes(doc, name, container, children, specs, token);
    }
    private static void BindNodes(LanguageDocument doc, string name, ZrdNode container, IReadOnlyList<ZrdNode> values, LanguageArgument[] specs, CancellationToken token, bool validateNumbers = true)
    {
        doc.Arguments.Containers.Add(new(container, name.TrimEnd(':'), specs, false, Math.Min(values.Count, specs.Length), false, specs.Length));
        for (int i = 0; i < Math.Min(values.Count, specs.Length); i++)
        {
            token.ThrowIfCancellationRequested();
            var value = values[i]; var spec = specs[i]; var span = doc.Span(value);
            if (span.Length == 0) continue;
            string documentation = "**" + spec.Label + "**\n\n" + spec.Documentation;
            doc.Arguments.Hints.TryAdd(span.Start, new(span, spec.Label + ":", documentation));
            doc.Arguments.OwnedOffsets.Add(span.Start);
            if (validateNumbers && spec.Type != "s" && (Number(value) is not { } n || !double.IsFinite(n)))
                doc.Report("ZAN_NUMBER", "error", (spec.Label ?? name) + " needs a finite number.", span);
        }
    }
}
