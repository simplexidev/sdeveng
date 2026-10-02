namespace SdevEng;

public record GitState(string Root, string Head, string? Branch, bool Clean, List<string> Operations, string[] Entries, (string name, string url, string direction)[] Remotes, GitUpstream? Upstream);
public record GitUpstream(string Name, int? Ahead, int? Behind);
public record GitChangedPaths(string[] Paths);
public record GitMergeBase(string Commit);
public record GitDivergence(int Ahead, int Behind);
