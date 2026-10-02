namespace SdevEng;

/// <summary>A deterministic project-reference graph. Each edge points from a project to its dependency.</summary>
public sealed record ProjectDependencyGraph(string[] Nodes, ProjectDependencyEdge[] Edges);

public sealed record ProjectDependencyEdge(string From, string To);
