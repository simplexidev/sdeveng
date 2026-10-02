using System.Text.Json.Serialization;

namespace SdevEng;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public record ToolkitSettings { public string[] OptionalTools { get; init; } = ["dotnet-trace", "dotnet-dump", "dotnet-counters", "dotnet-gcdump", "dotnet-monitor"]; public string[] EnabledIntegrations { get; init; } = []; public string Version { get; init; } = "0.0.0"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public record OutputSettings { public int MaxLines { get; init; } = 12; public int MaxLineLength { get; init; } = 240; public int MaxItems { get; init; } = 30; public int MaxOutputChars { get; init; } = 16000; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record HealthSettings
{
    public bool RequireNullable { get; init; } = true;
    public bool RequireCentralPackages { get; init; } = true;
    public bool RequireDeterministic { get; init; } = true;
    public bool RequireAnalyzers { get; init; } = true;
    public bool RequireLockFiles { get; init; }
    public string[] AllowedFrameworks { get; init; } = ["net10.0"];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record JevCapabilityPolicy
{
    public bool Allowed { get; init; }
    public string[] Purposes { get; init; } = [];
    public int ExpectedCalls { get; init; }
    public int MaxCalls { get; init; }
    public bool DeterministicFirst { get; init; } = true;
    public int MaxInputBytes { get; init; } = 1;
    public int MaxCandidates { get; init; }
    public string Privacy { get; init; } = "not-applicable";
    public double MinConfidence { get; init; } = 1;
    public string Uncertainty { get; init; } = "review";
    public string GptEscalation { get; init; } = "normal";
    public void Validate(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || Purposes.Length == 0 || Purposes.Any(string.IsNullOrWhiteSpace) || ExpectedCalls < 0 || MaxCalls is < 0 or > 100 || ExpectedCalls > MaxCalls || MaxInputBytes is < 1 or > 65536 || MaxCandidates is < 0 or > 100 || MaxCandidates > MaxCalls || string.IsNullOrWhiteSpace(Privacy) || !double.IsFinite(MinConfidence) || MinConfidence is < 0 or > 1 || Uncertainty != "review" || GptEscalation is not ("normal" or "stronger") || Allowed != (MaxCalls > 0))
            throw new ArgumentException($"Invalid JEV capability policy: {capability}.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record JevSettings
{
    public string Mode { get; init; } = "auto";
    public string ApiUrl { get; init; } = "https://api.typesafe.ai/v1/systemone";
    public string Model { get; init; } = "jev-latest";
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxInputBytes { get; init; } = 16384;
    public int MaxCandidates { get; init; } = 25;
    public double IncludeThreshold { get; init; } = .70;
    public double ExcludeThreshold { get; init; } = .10;
    public double MinConfidence { get; init; } = .80;
    public int CacheHours { get; init; } = 24;
    public Dictionary<string, JevCapabilityPolicy> Capabilities { get; init; } = new(StringComparer.Ordinal);
    public void Validate()
    {
        if (Mode is not ("off" or "auto" or "required") || TimeoutSeconds is < 1 or > 120 || MaxInputBytes is < 1 or > 65536 || MaxCandidates is < 1 or > 100 || CacheHours is < 0 or > 720 || !double.IsFinite(IncludeThreshold) || !double.IsFinite(ExcludeThreshold) || !double.IsFinite(MinConfidence) || ExcludeThreshold < 0 || IncludeThreshold > 1 || ExcludeThreshold >= IncludeThreshold || MinConfidence is < 0 or > 1) throw new ArgumentException("Invalid JEV configuration.");
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) throw new ArgumentException("JEV endpoint must use HTTPS without credentials, query or fragment.");
        foreach (var (capability, policy) in Capabilities) policy.Validate(capability);
    }
}
