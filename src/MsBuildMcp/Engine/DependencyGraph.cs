namespace MsBuildMcp.Engine;

/// <summary>
/// Builds and queries the project reference DAG from a solution.
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _edges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _reverseEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Build graph from solution info + evaluated project references + .sln ProjectDependencies.
    /// </summary>
    public static DependencyGraph Build(SolutionInfo solution, ProjectEngine engine,
        string configuration = "Debug", string platform = "x64")
    {
        var graph = new DependencyGraph();
        var projectPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var solutionDir = solution.Directory + Path.DirectorySeparatorChar;

        // Map project names to full paths
        foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
        {
            var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, proj.RelativePath));
            projectPaths[fullPath] = proj.Name;
            graph._nodes.Add(proj.Name);
        }

        // Source 1: Evaluate each project to find ProjectReference items
        foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
        {
            var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, proj.RelativePath));
            try
            {
                var snapshot = engine.Evaluate(fullPath, configuration, platform, solutionDir);
                foreach (var reference in snapshot.ProjectReferences)
                {
                    var refName = projectPaths.GetValueOrDefault(reference.FullPath, reference.Name);
                    graph.AddEdge(proj.Name, refName);
                }
            }
            catch
            {
                // Skip projects that can't be evaluated (cmake-generated, etc.)
            }
        }

        // Source 2: .sln ProjectSection(ProjectDependencies) — implicit/link-time deps
        foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
        {
            foreach (var depName in proj.SolutionDependencies)
            {
                graph.AddEdge(proj.Name, depName);
            }
        }

        return graph;
    }

    public void AddEdge(string from, string to)
    {
        _nodes.Add(from);
        _nodes.Add(to);

        if (!_edges.TryGetValue(from, out var deps))
        {
            deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _edges[from] = deps;
        }
        deps.Add(to);

        if (!_reverseEdges.TryGetValue(to, out var rdeps))
        {
            rdeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _reverseEdges[to] = rdeps;
        }
        rdeps.Add(from);
    }

    /// <summary>Direct dependencies of a project.</summary>
    public IReadOnlySet<string> DependenciesOf(string project) =>
        _edges.GetValueOrDefault(project) ?? (IReadOnlySet<string>)new HashSet<string>();

    /// <summary>Projects that directly depend on a project.</summary>
    public IReadOnlySet<string> DependentsOf(string project) =>
        _reverseEdges.GetValueOrDefault(project) ?? (IReadOnlySet<string>)new HashSet<string>();

    /// <summary>All transitive dependencies.</summary>
    public HashSet<string> TransitiveDependenciesOf(string project)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(project);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var dep in DependenciesOf(current))
            {
                if (result.Add(dep))
                    stack.Push(dep);
            }
        }
        return result;
    }

    /// <summary>All transitive dependents (reverse closure).</summary>
    public HashSet<string> TransitiveDependentsOf(string project)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(project);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var dep in DependentsOf(current))
            {
                if (result.Add(dep))
                    stack.Push(dep);
            }
        }
        return result;
    }

    /// <summary>Topological sort (Kahn's algorithm). Returns build order.</summary>
    public List<string> TopologicalSort()
    {
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _nodes) inDegree[node] = 0;
        foreach (var (_, deps) in _edges)
            foreach (var dep in deps)
                inDegree[dep] = inDegree.GetValueOrDefault(dep) + 1;

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x));
        var result = new List<string>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            result.Add(node);
            if (_edges.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps.OrderBy(x => x))
                {
                    inDegree[dep]--;
                    if (inDegree[dep] == 0)
                        queue.Enqueue(dep);
                }
            }
        }

        // Reverse: dependencies first, dependents last
        result.Reverse();
        return result;
    }

    /// <summary>All edges as (from, to) pairs.</summary>
    public IEnumerable<(string From, string To)> Edges =>
        _edges.SelectMany(kv => kv.Value.Select(to => (kv.Key, to)));

    public IReadOnlySet<string> Nodes => _nodes;
}
