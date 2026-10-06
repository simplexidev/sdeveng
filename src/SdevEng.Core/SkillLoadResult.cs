namespace SdevEng;

/// <summary>One explicitly requested instruction body or declared reference; omissions contain no content.</summary>
public sealed record SkillLoadResult(string SkillId, string? ReferencePath, string Status,
    string? Content, string? OmissionReason, int Utf8Bytes, int Characters);
