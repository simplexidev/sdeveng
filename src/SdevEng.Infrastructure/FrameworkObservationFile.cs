using System.Text;

namespace SdevEng;

/// <summary>Bounded local input only; no network capture or persistence.</summary>
public static class FrameworkObservationFile
{
    public static string Read(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[FrameworkProvenance.MaxObservationBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > FrameworkProvenance.MaxObservationBytes) throw new FormatException("Observations exceed 16384 bytes.");
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, count); }
        catch (DecoderFallbackException error) { throw new FormatException("Observations must be UTF-8.", error); }
    }
}
