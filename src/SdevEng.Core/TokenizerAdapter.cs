namespace SdevEng;

/// <summary>A verified, loaded tokenizer asset format. Full-input counting is a separate operation.</summary>
public interface ITokenizerAdapter
{
    TokenizerManifest Manifest { get; }

    /// <summary>Tokenizes one complete rendered input. Loading vocabulary alone does not imply counting support.</summary>
    long CountTokens(string renderedInput) => throw new NotSupportedException("Exact tokenizer encoding is unavailable.");
}
