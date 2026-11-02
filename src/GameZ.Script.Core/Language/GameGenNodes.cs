using GameZ.Script.Core.Worlds;

namespace GameZ.Script.Core.Language;

/// <summary>Geometry-free runtime name state. No mutation reaches the assembled world.</summary>
internal sealed class GameGenNodes
{
    internal sealed class Work(CancellationToken token)
    {
        private long used;
        internal void Spend(long count = 1) { token.ThrowIfCancellationRequested(); if ((used += count) > 4_000_000) throw new InvalidDataException("Runtime name lookup work budget exceeded."); }
    }
    private readonly Work work;
    internal sealed class Node(string name, LanguageLocation origin)
    {
        internal string Name = name;
        internal LanguageLocation Origin = origin;
        internal bool Live = true;
        internal List<Node> Children = [];
        internal List<Node> Parents = [];
    }
    private readonly List<Node> nodes = [];
    private Node? current;
    internal GameGenNodes(GameZWorld world, Func<WorldNode, LanguageLocation> location, Work work)
    {
        this.work = work;
        work.Spend(world.Nodes.Count);
        var map = world.Nodes.ToDictionary(n => n, n => new Node(n.Name, location(n)));
        nodes.AddRange(world.Nodes.Select(n => map[n]));
        foreach (var (source, node) in map)
            foreach (var child in source.Children)
                if (map.TryGetValue(child, out var target)) { work.Spend(); node.Children.Add(target); target.Parents.Add(node); }
    }
    internal IEnumerable<(string Name, LanguageLocation Origin)> Names => nodes.Where(n => n.Live).Select(n => (n.Name, n.Origin));
    private Node? Find(string name) { work.Spend(nodes.Count); return nodes.LastOrDefault(n => n.Live && n.Name == name); }
    private IEnumerable<Node> Subtree(Node root)
    {
        var stack = new Stack<Node>(); var seen = new HashSet<Node>(); stack.Push(root);
        while (stack.TryPop(out var node))
        {
            work.Spend(node.Children.Count + 1L);
            if (!seen.Add(node) || !node.Live) continue;
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }
    internal LanguageLocation? Run(string command, string[] args, LanguageLocation origin)
    {
        string name = args.ElementAtOrDefault(0) ?? "";
        switch (command)
        {
            case "FindNode": current = Find(name); return current?.Origin;
            case "FindSubNode": current = current == null ? null : Subtree(current).FirstOrDefault(n => n.Name == name); return current?.Origin;
            case "NewObject3D": case "NewLOD": case "NewWorld": case "NewCamera": case "NewDisplay": case "NewWindow": case "LightNew":
                if (nodes.Count >= 50_000) throw new InvalidDataException("Runtime name analysis exceeds 50,000 nodes.");
                current = new(name, origin); nodes.Add(current); break;
            case "NodeSetDescription": if (current != null) { current.Name = name; current.Origin = origin; } break;
            case "AddChild": case "Object3DAddChild": case "LODAddChild":
                if (current != null && Find(name) is { } child && !Subtree(child).Contains(current))
                { current.Children.Add(child); child.Parents.Add(current); }
                break;
            case "DeleteChild":
                if (current != null && Subtree(current).FirstOrDefault(n => n.Name == name) is { } removed && removed != current)
                { work.Spend(current.Children.Count + removed.Parents.Count); current.Children.Remove(removed); removed.Parents.Remove(current); }
                break;
            case "DeleteTree":
                if (Find(name) is { } root) Delete(root);
                break;
        }
        return null;
    }
    private void Delete(Node root)
    {
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            work.Spend(node.Children.Count + 1L);
            if (node.Parents.Count != 0 || !node.Live) continue;
            node.Live = false;
            if (current == node) current = null;
            foreach (var child in node.Children) { work.Spend(child.Parents.Count); child.Parents.Remove(node); pending.Push(child); }
            node.Children.Clear();
        }
    }
}
