using System.Numerics;
using GameZ.Script.Core.Formats;

namespace GameZ.Script.Core.Sources;

/// <summary>The sampled-motion authoring contract, shared by editor diagnostics and native compilation.</summary>
internal static class MotionSourceRules
{
    internal sealed record Issue(ZrdNode Node, string Code, string Message);
    internal static ZrdNode Unwrap(ZrdNode root)
    {
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        return root;
    }

    internal static IReadOnlyList<Issue> Validate(ZrdNode source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        List<Issue> issues = [];
        void Report(Issue issue) { if (issues.Count < 201) issues.Add(issue); }
        var roots = Fields(Unwrap(source), ["MOTION"]);
        var motions = roots.Where(p => p.Key.Text == "MOTION").ToArray();
        if (motions.Length != 1)
        {
            Report(new(motions.Length > 1 ? motions[1].Key : source, motions.Length == 0 ? "ZAN_REQUIRED" : "ZAN_ARITY", "A sampled animation needs exactly one MOTION ( ... ) root."));
            return issues;
        }
        var motion = motions[0].Value;
        var fields = Fields(motion, ["LOOP_SECONDS", "FRAME_COUNT", "PART"]);
        var duration = One(fields, "LOOP_SECONDS", motion);
        var count = One(fields, "FRAME_COUNT", motion);
        double? seconds = SingleNumber(duration), frames = SingleNumber(count);
        if (duration != null && (seconds is not > 0 || !double.IsFinite(seconds.Value)))
            Report(new(duration, "ZAN_RANGE", "LOOP_SECONDS needs one positive finite number."));
        if (count != null && (frames is not >= 1 or > 100_000 || !double.IsFinite(frames.Value) || Math.Truncate(frames.Value) != frames.Value))
            Report(new(count, "ZAN_RANGE", "FRAME_COUNT needs one integer playback count from 1 through 100000."));
        var parts = fields.Where(p => p.Key.Text == "PART").ToArray();
        if (parts.Length is < 1 or > 4096) Report(new(motion, "ZAN_ARITY", "MOTION needs 1 through 4096 PART tracks."));
        if (frames is >= 1 and <= 100_000 && (long)parts.Length * (frames.Value + 1) > MotionClip.MaximumArchiveSamples)
            Report(new(motion, "ZAN_RANGE", "Sampled motion exceeds the native sample limit."));
        foreach (var (_, part) in parts)
        {
            token.ThrowIfCancellationRequested();
            var partFields = Fields(part, ["NAME", "SAMPLES"]);
            var name = One(partFields, "NAME", part);
            var samples = One(partFields, "SAMPLES", part);
            if (name != null && (name.Children.Count != 1 || name.Children[0].Kind != ZrdKind.String || name.Children[0].Text.Length is 0 or > 4096
                || name.Children[0].Text.Any(c => c == 0 || c > 255)))
                Report(new(name, "ZAN_TYPE", "PART needs one NAME string of 1 through 4096 Latin-1 characters without NUL."));
            if (samples == null) continue;
            if (frames is >= 1 and <= 100_000 && samples.Children.Count != frames.Value + 1)
                Report(new(samples, "ZAN_ARITY", "SAMPLES needs FRAME_COUNT + 1 rows including its closing sample."));
            foreach (var row in samples.Children)
            {
                token.ThrowIfCancellationRequested();
                if (row.Kind != ZrdKind.Array)
                { Report(new(row, "ZAN_TYPE", "A motion sample is a parenthesized XYZ / WXYZ tuple.")); continue; }
                if (row.Children.Count != 7)
                { Report(new(row, "ZAN_ARITY", "A motion sample needs seven translation XYZ and quaternion WXYZ numbers.")); continue; }
                bool valid = true;
                foreach (var value in row.Children)
                    if (Number(value) is not { } number || !double.IsFinite(number))
                    { Report(new(value, "ZAN_NUMBER", "A motion sample needs finite numeric components.")); valid = false; }
                if (!valid) continue;
                float N(int i) => (float)Number(row.Children[i])!.Value;
                var translation = new Vector3(N(0), N(1), N(2));
                var quaternion = new Quaternion(N(4), N(5), N(6), N(3));
                if (!float.IsFinite(translation.LengthSquared()) || !float.IsFinite(quaternion.LengthSquared()))
                    Report(new(row, "ZAN_NUMBER", "Motion transform magnitude exceeds finite native floats."));
                else if (quaternion.LengthSquared() < 1e-12f)
                    Report(new(row, "ZAN_QUATERNION", "A motion quaternion must have nonzero length."));
            }
        }
        return issues;

        List<(ZrdNode Key, ZrdNode Value)> Fields(ZrdNode node, string[] allowed)
        {
            List<(ZrdNode, ZrdNode)> result = [];
            if (node.Kind != ZrdKind.Array) { Report(new(node, "ZAN_TYPE", "Motion fields need a parenthesized array.")); return result; }
            for (int i = 0; i < node.Children.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var key = node.Children[i];
                if (key.Kind != ZrdKind.String || i + 1 == node.Children.Count || node.Children[i + 1].Kind != ZrdKind.Array)
                { Report(new(key, "ZAN_TYPE", "Expected a motion field name followed by its parenthesized values; stray values are not compiled.")); continue; }
                var value = node.Children[++i];
                if (!allowed.Contains(key.Text, StringComparer.Ordinal))
                    Report(new(key, "ZAN_TYPE", "Unsupported sampled-motion field " + JsonData.ShownText(key.Text, 128) + "."));
                result.Add((key, value));
            }
            return result;
        }
        ZrdNode? One(List<(ZrdNode Key, ZrdNode Value)> fields, string key, ZrdNode owner)
        {
            var matches = fields.Where(p => p.Key.Text == key).ToArray();
            if (matches.Length == 0) Report(new(owner, "ZAN_REQUIRED", key + " is required."));
            else if (matches.Length > 1) Report(new(matches[1].Key, "ZAN_ARITY", key + " must appear exactly once."));
            return matches.FirstOrDefault().Value;
        }
    }
    internal static double? Number(ZrdNode node) => node.Kind == ZrdKind.Int ? unchecked((int)node.Bits)
        : node.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(node.Bits) : null;
    private static double? SingleNumber(ZrdNode? node) => node is { Children.Count: 1 } ? Number(node.Children[0]) : null;
}
