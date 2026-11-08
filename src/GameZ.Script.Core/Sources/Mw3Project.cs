using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Sources;

/// <summary>Native source outputs used by MW3's read-only compiler checks.</summary>
internal static class Mw3Project
{
    internal static bool Model(string path) => path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
    internal static IReadOnlyList<SourceOutputPlan> Plan(string root, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        var files = SourceProject.Files(root, "data", _ => true, snapshot.Added, token, snapshot.Inventory);
        List<SourceOutputPlan> plans = [];
        foreach (var group in files.Where(p => p.Contains("/zrdr/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase)).GroupBy(ResourceOutput, StringComparer.OrdinalIgnoreCase))
            plans.Add(new(group.Key, "archive", group.Order(StringComparer.Ordinal).ToArray()));
        var scripts = SourceProject.Files(root, "gamegen", p => p.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), snapshot.Added, token, snapshot.Inventory);
        if (scripts.Count > 0) plans.Add(new("interp.zbd", "scripts", scripts));
        var models = files.Where(Model).ToArray();
        var library = models.Where(p => p.StartsWith("data/common/mechs/models/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("data/lab/models/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (library.Length > 0) plans.Add(new("mechlib.zbd", "mw3-mechs", library));
        var motions = files.Where(p => p.StartsWith(Mw3LibrarySources.MotionFolder + "/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".zan", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (motions.Length > 0) plans.Add(new("motion.zbd", "mw3-motion", motions));
        var tracks = files.Where(p => p.EndsWith(".zan", StringComparison.OrdinalIgnoreCase) && !p.StartsWith(Mw3LibrarySources.MotionFolder + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (string path in files.Where(p => p.EndsWith("/zrdr/anim.zad", StringComparison.OrdinalIgnoreCase)))
        {
            string campaign = path.Split('/')[1]; if (campaign.Equals("common", StringComparison.OrdinalIgnoreCase)) continue;
            if (snapshot.Exists(SourceBuilder.WorldScript(campaign))) plans.Add(new($"{campaign}/gamez.zbd", "world", [SourceBuilder.WorldScript(campaign), .. models]));
            plans.Add(new($"{campaign}/anim.zbd", "animations", [path, .. tracks]));
        }
        if (plans.Sum(p => (long)p.Inputs.Count) > SourceBuilder.MaximumPlanInputs) throw new InvalidDataException("The native source plan exceeds its input limit.");
        return plans;
    }
    private static string ResourceOutput(string path)
    {
        var parts = path.Split('/');
        if (parts[1].Equals("common", StringComparison.OrdinalIgnoreCase)) return "reader.zbd";
        int ai = Array.FindIndex(parts, p => p.Equals("aipath", StringComparison.OrdinalIgnoreCase));
        return ai >= 0 && ai + 2 < parts.Length ? $"{parts[1]}/reader{parts[ai + 1]}.zbd" : $"{parts[1]}/reader.zbd";
    }
    internal static SourceBuilder.Built Build(SourceOutputPlan plan, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        if (plan.Family == "mw3-mechs")
        {
            var library = Mw3LibrarySources.CompileModels(plan.Inputs, snapshot, token);
            return new(library.Bytes, library.Members, library.Warnings);
        }
        List<ArchiveSources.Entry> entries = []; HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in plan.Inputs)
        {
            token.ThrowIfCancellationRequested(); string name = Path.GetFileNameWithoutExtension(path);
            if (!names.Add(name)) throw new InvalidDataException($"Duplicate native motion member {name}.");
            entries.Add(new(name, SourceBuilder.SourceField(path), Mw3LibrarySources.BuildMotion(snapshot.Read(path, token, SourceProject.MaximumSourceTextBytes), token)));
        }
        return new(ArchiveSources.Write(entries), entries.Count, []);
    }
}
