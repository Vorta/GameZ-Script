using System.Text.Json.Nodes;
using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using GameZ.Script.Core.Sources;
using StreamJsonRpc;

namespace GameZ.Script.LanguageServer;

public sealed partial class GameZLanguageServer
{
    private sealed record EditorProfile(GameVariant? Game, string Source, string? Root, string? Reason);
    private readonly Dictionary<string, (long Generation, GameVariant? Game)> sourceGames = new(StringComparer.OrdinalIgnoreCase);

    private GameVariant? Game(string path, CancellationToken token) => Profile(path, token).Game;

    // The profile used for the UI is the same decision used by every analysis path.
    private EditorProfile Profile(string path, CancellationToken token)
    {
        string setting; long revision; Buffer? document;
        lock (gate)
        {
            var scope = dialects.Keys.Where(p => Under(path, p)).MaxBy(p => p.Length);
            setting = scope == null ? defaultDialect : dialects[scope];
            revision = identityRevision;
            document = buffers.GetValueOrDefault(new Uri(path).AbsoluteUri);
        }
        string? root = Root(path);
        if (setting is "recoil" or "mw3")
            return new(setting == "mw3" ? GameVariant.MechWarrior3 : GameVariant.Recoil, "configuration", root, null);
        string? reason = null;
        if (root != null)
        {
            GameVariant? identified;
            try
            {
                bool cached;
                lock (gate) { cached = games.TryGetValue(root, out var known) && known.Revision == revision; identified = cached ? known.Game : null; }
                if (!cached)
                {
                    identified = SourceGameProfile.Identify(root, token);
                    token.ThrowIfCancellationRequested();
                    lock (gate) if (identityRevision == revision) games[root] = (revision, identified);
                }
                if (identified != null) return new(identified, "workspace", root, null);
                if (DiscoverSourceGame(root, token) is { } inferred) return new(inferred, "project-source", root, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { reason = "Project identity is unavailable: " + ex.Message; }
        }
        if (document != null && !pathIsScript(path) && RecoilLanguage.TryDetectDocumentGame(document.Text, token) is { } local)
            return new(local, "document", root, reason);
        return new(null, "unknown", root, reason ?? (!trusted
            ? "Restricted Mode: automatic project detection is unavailable. Select MW3 or RECOIL for document-local assistance."
            : root == null ? "No owning source project was found. Select a game or open its source-project folder."
            : "No decisive game metadata or source markers were found. Select MW3 or RECOIL."));
    }

    // This pass establishes identity only. It does not resolve resources, build worlds, or execute scripts.
    private GameVariant? DiscoverSourceGame(string root, CancellationToken token)
    {
        long revision; Dictionary<string, Buffer> overlay;
        lock (gate)
        {
            revision = generation;
            if (sourceGames.TryGetValue(root, out var cached) && cached.Generation == revision) return cached.Game;
            overlay = buffers.Values.Where(b => Root(b.Path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true)
                .ToDictionary(b => SourceProject.Relative(root, b.Path), StringComparer.OrdinalIgnoreCase);
        }
        var found = SourceGameProfile.IdentifySources(root, overlay.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.OrdinalIgnoreCase), token);
        lock (gate) if (generation == revision) sourceGames[root] = (revision, found);
        return found;
    }

    [JsonRpcMethod("gamez/getGameProfile", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> GetGameProfile(JsonObject args, CancellationToken token)
    {
        string path = Local(args["textDocument"]?["uri"]?.GetValue<string>())
            ?? throw new InvalidOperationException("Choose a local GameZ source document.");
        long revision; CancellationToken current;
        lock (gate) { revision = generation; current = pending.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, current);
        var profile = await Task.Run(() => Profile(path, linked.Token), linked.Token);
        lock (gate) if (generation != revision) throw new OperationCanceledException("Game profile changed during request.");
        return new { game = profile.Game == GameVariant.MechWarrior3 ? "mw3" : profile.Game == GameVariant.Recoil ? "recoil" : null,
            source = profile.Source, rootUri = profile.Root == null ? null : new Uri(profile.Root).AbsoluteUri,
            reason = profile.Reason, restricted = !trusted, generation = revision };
    }

    private async Task NotifyProfileChanged()
    {
        try { if (Rpc != null) await Rpc.NotifyWithParameterObjectAsync("gamez/gameProfileChanged", new { }); }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException) { }
    }
}
