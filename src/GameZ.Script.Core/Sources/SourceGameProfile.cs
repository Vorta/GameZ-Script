using System.Text.Json;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Sources;

/// <summary>Native source-format identity used by read-only analysis and source compilation.</summary>
public sealed partial record SourceGameProfile(GameVariant Game)
{
    public string Name => Game == GameVariant.MechWarrior3 ? "mw3" : "recoil";
    public uint WorldVersion => Game == GameVariant.MechWarrior3 ? 27u : 15u;
    public uint AnimationVersion => Game == GameVariant.MechWarrior3 ? 39u : 28u;
    public bool NativeMissions => Game == GameVariant.MechWarrior3;
    public static SourceGameProfile For(string root, CancellationToken token = default) => new(SourceProject.Game(root, token));
    /// <summary>Returns an authoritative source-format identity, or null when a partial project has none.</summary>
    public static GameVariant? Identify(string root, CancellationToken token = default) => DetectIdentity(root, null, token);

    internal static GameVariant Detect(string root, SourceBuilder.Snapshot? snapshot, CancellationToken token)
        => DetectIdentity(root, snapshot, token) ?? GameVariant.Recoil;

    internal static GameVariant? DetectIdentity(string root, SourceBuilder.Snapshot? snapshot, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        bool recoil = false;
        foreach (string path in SourceProject.Files(root, "data", p => p.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase), snapshot?.Added, token, snapshot?.Inventory))
        {
            token.ThrowIfCancellationRequested();
            SourceProject.RejectNestedLinks(root, path);
            if (!OwnsSource(root, SourceProject.Resolve(root, path))) continue;
            var bytes = snapshot == null ? SourceRead.All(SourceProject.Resolve(root, path), ProjectReadLimits.Model(), token) : snapshot.Read(path, token, ProjectReadLimits.Model());
            try
            {
                var identity = WorldGltf.ReadSourceIdentity(bytes, token);
                if (identity.Game == "mw3") return GameVariant.MechWarrior3;
                // An ordinary glTF without source metadata does not identify its owning game.
                if (identity.SourceFormat == 4) recoil = true;
            }
            catch (JsonException) { /* An unrelated unfinished source does not choose the project's game. */ }
        }
        return recoil ? GameVariant.Recoil : null;
    }


}

public static partial class SourceProject
{
    public static GameVariant Game(string root, CancellationToken token = default) => SourceGameProfile.Detect(root, null, token);
}
