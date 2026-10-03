namespace SdevEng;

/// <summary>A deterministic project-reference graph. Each edge points from a project to its dependency.</summary>
public sealed record ProjectDependencyGraph(string[] Nodes, ProjectDependencyEdge[] Edges)
{
    public string[] AffectedProjects(IEnumerable<string> owners)
    {
        var selected = owners.ToHashSet(StringComparer.Ordinal);
        bool added;
        do
        {
            added = false;
            foreach (var edge in Edges)
                if (selected.Contains(edge.To)) added |= selected.Add(edge.From);
        } while (added);
        return selected.Order(StringComparer.Ordinal).ToArray();
    }

    public string[] AffectedTestProjects(IEnumerable<string> owners, IEnumerable<string> testProjects) =>
        AffectedProjects(owners).Intersect(testProjects, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public ProjectDependencyValidation Validate()
    {
        var nodes = Nodes.ToHashSet(StringComparer.Ordinal);
        var unresolved = Edges.Where(edge => !nodes.Contains(edge.To))
            .OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ToArray();
        var adjacency = Nodes.ToDictionary(node => node, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in Edges.Where(edge => nodes.Contains(edge.To))) adjacency[edge.From].Add(edge.To);
        foreach (var values in adjacency.Values) values.Sort(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cycles = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(string node)
        {
            state[node] = 1; stack.Add(node);
            foreach (var next in adjacency[node])
            {
                if (!state.TryGetValue(next, out var mark)) Visit(next);
                else if (mark == 1) cycles.Add(string.Join(" -> ", stack.Skip(stack.IndexOf(next)).Append(next)));
            }
            stack.RemoveAt(stack.Count - 1); state[node] = 2;
        }
        foreach (var node in Nodes.Order(StringComparer.Ordinal)) if (!state.ContainsKey(node)) Visit(node);
        return new(unresolved.Length == 0 && cycles.Count == 0, cycles.ToArray(), unresolved);
    }
}

public sealed record ProjectDependencyEdge(string From, string To);
public sealed record ProjectDependencyValidation(bool IsValid, string[] Cycles, ProjectDependencyEdge[] UnresolvedEdges);
