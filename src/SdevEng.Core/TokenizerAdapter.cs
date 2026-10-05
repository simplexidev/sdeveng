namespace SdevEng;

/// <summary>A verified, loaded tokenizer asset format. Full-input counting is a separate operation.</summary>
public interface ITokenizerAdapter
{
    TokenizerManifest Manifest { get; }
}
