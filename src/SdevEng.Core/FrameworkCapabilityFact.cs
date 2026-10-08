namespace SdevEng;

/// <summary>Static project evidence, never proof of runtime activation.</summary>
public sealed record FrameworkCapabilityFact(string Id, string Status, string Evidence, string? Version = null,
    FrameworkCapabilityLocation[]? Locations = null);

public sealed record FrameworkCapabilityLocation(string Path, int Line, int Column);
