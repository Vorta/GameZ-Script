using GameZ.Script.Core.Formats;

namespace GameZ.Script.Core.Sources;

/// <summary>Resource-specific rules shared by editor diagnostics and resource compilation. Never rewrites nodes.</summary>
internal static class VehicleWeaponRules
{
    internal sealed record Issue(ZrdNode Node, string Code, string Message, bool Warning = false);
    internal sealed record WeaponList(string Vehicle, ZrdNode Key, ZrdNode Value);
    internal static bool IsVehicleFile(string path) => Path.GetFileName(path).ToLowerInvariant() is
        "vehicle.zrd" or "vehicle_easy.zrd" or "vehicle_hard.zrd";

    internal static IEnumerable<Issue> ValidateList(string vehicle, ZrdNode list, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string owner = "Vehicle " + JsonData.ShownText(vehicle, 256) + " weapons";
        if (list.Kind != ZrdKind.Array)
        {
            yield return new(list, "GAMEZ_TYPE", owner + " requires an array of weapon entries.");
            yield break;
        }
        for (int i = 0; i < list.Children.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var row = list.Children[i];
            string context = owner + " row " + (i + 1);
            if (row.Kind != ZrdKind.Array || row.Children.Count is < 9 or > 10)
            {
                yield return new(row, "GAMEZ_ARITY", context + " requires 9 or 10 values.");
                continue;
            }
            if (row.Children.Count == 9) continue;
            var pause = row.Children[9];
            if (pause.Kind != ZrdKind.Float)
            {
                string help = pause.Kind == ZrdKind.Int
                    ? " Use " + unchecked((int)pause.Bits).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0 instead."
                    : " Use a float such as 4.0.";
                yield return new(pause, "GAMEZ_TYPE", context + " beam pause (value 10) requires a FLOAT reader node (type 2)." + help);
                continue;
            }
            float seconds = BitConverter.UInt32BitsToSingle(pause.Bits);
            if (!float.IsFinite(seconds))
            {
                yield return new(pause, "GAMEZ_FINITE", context + " beam pause must be finite.");
                continue;
            }
            if (seconds < 0)
            {
                yield return new(pause, "RECOIL_RANGE", context + " beam pause must be nonnegative; 0.0 means use the delay (value 5).");
                continue;
            }
            bool player = vehicle == "bft";
            var weapon = row.Children[0];
            bool other = weapon.Kind == ZrdKind.String && weapon.Text is not ("wep_6.0" or "wep_6.1" or "wep_7.1");
            if (player || other)
                yield return new(pause, "RECOIL_IGNORED", context + " beam pause is ignored "
                    + (player && other ? "on the player's bft vehicle and on this non-beam weapon."
                        : player ? "on the player's bft vehicle." : "on this non-beam weapon; it applies only to wep_6.0, wep_6.1 and wep_7.1."), true);
        }
    }

    internal static void ValidateForCompilation(string path, ZrdNode root, CancellationToken token, Action<string>? warning = null)
    {
        token.ThrowIfCancellationRequested();
        if (!IsVehicleFile(path)) return;
        foreach (var list in Lists(root, token))
            foreach (var issue in ValidateList(list.Vehicle, list.Value, token))
                if (issue.Warning) warning?.Invoke(issue.Message);
                else throw new InvalidDataException(issue.Message);
    }

    // Reader key lookup descends through named and anonymous arrays (engine-evidence.md, Common rules).
    // Share discovery with the editor so grouping arrays cannot hide a positional weapon operand.
    internal static IEnumerable<WeaponList> Lists(ZrdNode root, CancellationToken token) => CommonModes(root, "", false, token);

    private static IEnumerable<WeaponList> CommonModes(ZrdNode node, string owner, bool player, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (int i = 0; i < node.Children.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var key = node.Children[i];
            if (key.Kind == ZrdKind.String && key.Text == "common_mode" && i + 1 < node.Children.Count)
            {
                foreach (var (tag, value) in Weapons(node.Children[++i], token))
                    yield return new(player ? "bft" : owner, tag, value);
            }
            else if (key.Kind == ZrdKind.Array)
            {
                string name = i > 0 && node.Children[i - 1].Kind == ZrdKind.String ? node.Children[i - 1].Text : owner;
                foreach (var list in CommonModes(key, name, player || name == "bft", token)) yield return list;
            }
        }
    }

    private static IEnumerable<(ZrdNode Key, ZrdNode Value)> Weapons(ZrdNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (int i = 0; i < node.Children.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var key = node.Children[i];
            if (key.Kind == ZrdKind.String && key.Text == "weapons" && i + 1 < node.Children.Count)
                yield return (key, node.Children[++i]);
            else if (key.Kind == ZrdKind.Array)
                foreach (var pair in Weapons(key, token)) yield return pair;
        }
    }
}
