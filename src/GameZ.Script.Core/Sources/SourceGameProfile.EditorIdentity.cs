using System.Text;
using GameZ.Script.Core.Language;

namespace GameZ.Script.Core.Sources;

public sealed partial record SourceGameProfile
{
    /// <summary>Bounded source-only identity discovery for a trusted partial project. Never performs semantic analysis.</summary>
    public static GameVariant? IdentifySources(string root, IReadOnlyDictionary<string, string>? overlays = null, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        if (!SourceProject.IsProject(root)) return null;
        long bytes = 0;
        bool Candidate(string p) => p.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".zad", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".zan", StringComparison.OrdinalIgnoreCase);
        foreach (string relative in SourceProject.Files(root, "data", Candidate, overlays?.Keys.ToArray(), token))
        {
            token.ThrowIfCancellationRequested();
            SourceProject.RejectNestedLinks(root, relative);
            string path = SourceProject.Resolve(root, relative);
            // A nested source project owns its own identity, even inside a broader workspace folder.
            if (!OwnsSource(root, path)) continue;
            string text;
            if (overlays?.TryGetValue(relative, out var buffer) == true) text = buffer;
            else text = Encoding.Latin1.GetString(SourceRead.All(path, Math.Min(SourceProject.MaximumSourceTextBytes, LanguageProject.MaximumTextBytes - bytes), token));
            bytes += text.Length;
            if (text.Length > SourceProject.MaximumSourceTextBytes || bytes > LanguageProject.MaximumTextBytes)
                throw new InvalidDataException("Source identity scan exceeds its text analysis limit.");
            if (RecoilLanguage.TryDetectDocumentGame(text, token) is { } game) return game;
        }
        return null;
    }

    internal static bool OwnsSource(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? parent = Path.GetDirectoryName(path); parent != null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            if (SourceProject.IsProject(parent)) return false;
        return true;
    }
}
