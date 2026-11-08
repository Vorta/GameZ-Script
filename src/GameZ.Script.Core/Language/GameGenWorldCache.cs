using System.Security.Cryptography;
using GameZ.Script.Core.Sources;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Language;

/// <summary>Bounded immutable world reuse. Every observed file and absent search candidate is revalidated.</summary>
internal static class GameGenWorldCache
{
    private sealed record Probe(string Path, bool Folder, bool Present, string? Hash, ProjectReadLimits? Limits);
    private sealed record Entry(SourceBuilder.Snapshot.AssembledWorld World, Probe[] Inputs, long Bytes);
    private static readonly Lock gate = new();
    private static readonly Dictionary<(string Root, string Mission, GameVariant Game), Entry> entries = [];
    private const long MaximumBytes = 192L * 1024 * 1024;
    internal static SourceBuilder.Snapshot.AssembledWorld World(string root, string mission, IProjectFiles files, CancellationToken token, GameVariant game = GameVariant.Recoil)
    {
        var key = (root.ToUpperInvariant(), mission, game);
        Entry? cached; lock (gate) entries.TryGetValue(key, out cached);
        if (cached != null)
        {
            bool valid = true;
            foreach (var probe in cached.Inputs)
            {
                token.ThrowIfCancellationRequested();
                if (probe.Folder ? files.FolderExists(probe.Path) != probe.Present : files.Exists(probe.Path) != probe.Present) { valid = false; break; }
                if (probe.Hash != null && Convert.ToHexString(SHA256.HashData(files.Read(probe.Path, token, probe.Limits!.Value))) != probe.Hash) { valid = false; break; }
            }
            if (valid) return cached.World;
        }
        var tracked = new TrackingFiles(files);
        var assembler = new WorldAssembler(tracked, token) { Game = game };
        var world = assembler.Assemble(mission + ".gs");
        var built = new SourceBuilder.Snapshot.AssembledWorld(world, assembler.Warnings, assembler.TextureFiles, assembler.TextureAddressing, assembler.LoadedRoots)
        {
            Provenance = assembler.Provenance, Executions = assembler.Executions, WriteInstruction = assembler.WriteInstruction,
            ReceiverIssues = assembler.ReceiverIssues, LookupTargets = assembler.LookupTargets, TextureCycles = assembler.TextureCycles
        };
        long bytes = SourceExtractor.Footprint(world) + 512L * assembler.Provenance.Count + 256L * assembler.Executions.Count
            + assembler.Provenance.Values.Sum(p => 128L * p.Applied.Count);
        lock (gate)
        {
            entries.Remove(key);
            while (entries.Count > 0 && (entries.Count >= 16 || entries.Values.Sum(e => e.Bytes) + bytes > MaximumBytes)) entries.Remove(entries.Keys.First());
            if (bytes <= MaximumBytes) entries[key] = new(built, tracked.Inputs.Values.ToArray(), bytes);
        }
        return built;
    }
    private sealed class TrackingFiles(IProjectFiles files) : IProjectFiles
    {
        internal Dictionary<(string Path, bool Folder), Probe> Inputs { get; } = [];
        private void Add(Probe probe)
        {
            if (Inputs.Count >= 50_000) throw new InvalidDataException("Mission dependency budget exceeded.");
            var key = (probe.Path, probe.Folder);
            if (probe.Hash == null && Inputs.TryGetValue(key, out var previous) && previous.Hash != null) return;
            Inputs[key] = probe;
        }
        public bool Exists(string relative) { bool present = files.Exists(relative); Add(new(relative, false, present, null, null)); return present; }
        public bool FolderExists(string relative) { bool present = files.FolderExists(relative); Add(new(relative, true, present, null, null)); return present; }
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            var bytes = files.Read(relative, token, limits);
            Add(new(relative, false, true, Convert.ToHexString(SHA256.HashData(bytes)), limits));
            return bytes;
        }
    }
}
