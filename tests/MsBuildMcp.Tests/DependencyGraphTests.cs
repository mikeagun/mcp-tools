using MsBuildMcp.Engine;

namespace MsBuildMcp.Tests;

public class DependencyGraphTests
{
    [Fact]
    public void AddEdgeCreatesNodes()
    {
        var g = new DependencyGraph();
        g.AddEdge("A", "B");

        Assert.Contains("A", g.Nodes);
        Assert.Contains("B", g.Nodes);
        Assert.Equal(2, g.Nodes.Count);
    }

    [Fact]
    public void DependenciesOfReturnsDirectDeps()
    {
        var g = new DependencyGraph();
        g.AddEdge("app", "lib");
        g.AddEdge("app", "core");

        var deps = g.DependenciesOf("app");
        Assert.Equal(2, deps.Count);
        Assert.Contains("lib", deps);
        Assert.Contains("core", deps);
        Assert.Empty(g.DependenciesOf("lib"));
    }

    [Fact]
    public void DependentsOfReturnsReverseDeps()
    {
        var g = new DependencyGraph();
        g.AddEdge("app", "lib");
        g.AddEdge("test", "lib");

        var deps = g.DependentsOf("lib");
        Assert.Equal(2, deps.Count);
        Assert.Contains("app", deps);
        Assert.Contains("test", deps);
    }

    [Fact]
    public void TransitiveDependencies()
    {
        var g = new DependencyGraph();
        g.AddEdge("app", "api");
        g.AddEdge("api", "core");
        g.AddEdge("core", "shared");

        var deps = g.TransitiveDependenciesOf("app");
        Assert.Equal(3, deps.Count);
        Assert.Contains("api", deps);
        Assert.Contains("core", deps);
        Assert.Contains("shared", deps);
    }

    [Fact]
    public void TransitiveDependents()
    {
        var g = new DependencyGraph();
        g.AddEdge("app", "api");
        g.AddEdge("api", "core");
        g.AddEdge("test", "core");

        var deps = g.TransitiveDependentsOf("core");
        Assert.Equal(3, deps.Count);
        Assert.Contains("api", deps);
        Assert.Contains("app", deps);
        Assert.Contains("test", deps);
    }

    [Fact]
    public void TopologicalSortPutsDepsFirst()
    {
        var g = new DependencyGraph();
        g.AddEdge("app", "api");
        g.AddEdge("api", "core");
        g.AddEdge("app", "core");

        var order = g.TopologicalSort();
        Assert.Equal(3, order.Count);
        // core must come before api, api before app
        Assert.True(order.IndexOf("core") < order.IndexOf("api"));
        Assert.True(order.IndexOf("api") < order.IndexOf("app"));
    }

    [Fact]
    public void TopologicalSortHandlesDisconnected()
    {
        var g = new DependencyGraph();
        g.AddEdge("a", "b");
        g.AddEdge("c", "d");

        var order = g.TopologicalSort();
        Assert.Equal(4, order.Count);
        Assert.True(order.IndexOf("b") < order.IndexOf("a"));
        Assert.True(order.IndexOf("d") < order.IndexOf("c"));
    }

    [Fact]
    public void EdgesEnumeration()
    {
        var g = new DependencyGraph();
        g.AddEdge("a", "b");
        g.AddEdge("a", "c");
        g.AddEdge("b", "c");

        var edges = g.Edges.ToList();
        Assert.Equal(3, edges.Count);
        Assert.Contains(("a", "b"), edges);
        Assert.Contains(("a", "c"), edges);
        Assert.Contains(("b", "c"), edges);
    }

    [Fact]
    public void EmptyGraph_TopologicalSort_ReturnsEmpty()
    {
        var g = new DependencyGraph();
        var order = g.TopologicalSort();
        Assert.Empty(order);
    }

    [Fact]
    public void DependenciesOf_NodeNotInGraph_ReturnsEmpty()
    {
        var g = new DependencyGraph();
        g.AddEdge("A", "B");

        // "C" is not in the graph at all
        Assert.Empty(g.DependenciesOf("C"));
    }

    [Fact]
    public void DuplicateEdge_NotDuplicated()
    {
        var g = new DependencyGraph();
        g.AddEdge("A", "B");
        g.AddEdge("A", "B");

        var edges = g.Edges.ToList();
        Assert.Single(edges);
        Assert.Contains(("A", "B"), edges);
    }

    [Fact]
    public void DependenciesOf_UnknownNode_ReturnsEmpty()
    {
        var g = new DependencyGraph();
        g.AddEdge("X", "Y");

        Assert.Empty(g.DependenciesOf("unknown"));
    }

    [Fact]
    public void TransitiveDependencies_DiamondShape()
    {
        var g = new DependencyGraph();
        g.AddEdge("A", "B");
        g.AddEdge("A", "C");
        g.AddEdge("B", "D");
        g.AddEdge("C", "D");

        var deps = g.TransitiveDependenciesOf("A");
        Assert.Equal(3, deps.Count);
        Assert.Contains("B", deps);
        Assert.Contains("C", deps);
        Assert.Contains("D", deps);
    }
}
