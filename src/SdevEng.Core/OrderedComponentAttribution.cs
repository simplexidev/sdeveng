namespace SdevEng;

/// <summary>Offsets and lengths are UTF-16 code units in the final rendering, never fragment offsets.</summary>
public sealed record RenderedComponentSpan(string ComponentId, string ContentReference, int Start, int Length)
{
    public const string Overhead = "chat-template-overhead";

    public void Validate()
    {
        if (ComponentId != Overhead && !Enum.GetValues<PromptComponentId>().Any(id => PromptComponentIdJsonConverter.ToWireValue(id) == ComponentId) ||
            string.IsNullOrWhiteSpace(ContentReference) || Start < 0 || Length < 0)
            throw new ArgumentException("Invalid rendered component span.");
    }
}

public sealed record ComponentPrefixDelta(RenderedComponentSpan Span, long PrefixTokens, long Tokens);

/// <summary>Signed cumulative-prefix differences, including explicit template overhead exactly once.</summary>
public sealed record OrderedComponentAttribution(int SchemaVersion, string Algorithm, int RenderedLength,
    long EmptyTokens, long TotalTokens, long AttributedTokens, bool Reconciled, IReadOnlyList<ComponentPrefixDelta> Components)
{
    public const string AlgorithmVersion = "ordered-cumulative-prefix/v1";

    public void Validate()
    {
        if (SchemaVersion != 1 || Algorithm != AlgorithmVersion || RenderedLength < 0 || EmptyTokens != 0 ||
            TotalTokens < 0 || !Reconciled || Components is not { Count: > 0 })
            throw new ArgumentException("Invalid ordered component attribution.");
        var end = 0;
        long previous = EmptyTokens, sum = 0;
        foreach (var component in Components)
        {
            if (component is null || component.Span is null) throw new ArgumentException("Missing component delta.");
            component.Span.Validate();
            if (component.Span.Start != end || component.Span.Length > RenderedLength - end ||
                component.PrefixTokens < 0 || component.Tokens != component.PrefixTokens - previous)
                throw new ArgumentException("Invalid prefix transition or incomplete span coverage.");
            end += component.Span.Length;
            previous = component.PrefixTokens;
            sum = checked(sum + component.Tokens);
        }
        if (end != RenderedLength || previous != TotalTokens || sum != TotalTokens || AttributedTokens != sum)
            throw new ArgumentException("Component attribution does not reconcile.");
    }
}

public static class OrderedComponentAttributionCalculator
{
    /// <summary>Counts every final-string prefix. The caller supplies the authoritative full-input count from the same counter.</summary>
    public static OrderedComponentAttribution Measure(string renderedInput, IReadOnlyList<RenderedComponentSpan> spans,
        ITokenizerAdapter counter, long authoritativeTotal)
    {
        ArgumentNullException.ThrowIfNull(renderedInput);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(counter);
        // Validate all boundaries before tokenization, including surrogate-pair boundaries.
        var end = 0;
        foreach (var span in spans)
        {
            ArgumentNullException.ThrowIfNull(span);
            span.Validate();
            if (span.Start != end || span.Length > renderedInput.Length - end)
                throw new ArgumentException("Spans must cover the final rendering in order.");
            end += span.Length;
            if (end > 0 && end < renderedInput.Length && char.IsHighSurrogate(renderedInput[end - 1]) && char.IsLowSurrogate(renderedInput[end]))
                throw new ArgumentException("A span boundary splits a Unicode scalar.");
        }
        if (spans.Count == 0 || end != renderedInput.Length) throw new ArgumentException("Spans must cover the entire final rendering.");
        var empty = counter.CountTokens("");
        if (empty != 0) throw new ArgumentException("Ordered attribution requires an empty input count of zero.");
        long previous = empty, sum = 0;
        var deltas = new List<ComponentPrefixDelta>();
        foreach (var span in spans)
        {
            var prefix = counter.CountTokens(renderedInput[..(span.Start + span.Length)]);
            var delta = checked(prefix - previous);
            deltas.Add(new(span, prefix, delta));
            sum = checked(sum + delta);
            previous = prefix;
        }
        var result = new OrderedComponentAttribution(1, OrderedComponentAttribution.AlgorithmVersion, renderedInput.Length,
            empty, authoritativeTotal, sum, sum == authoritativeTotal, deltas);
        result.Validate();
        return result;
    }
}
