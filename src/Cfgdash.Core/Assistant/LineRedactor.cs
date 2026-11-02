using System.Text;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Assistant;

/// <summary>
/// Buffers streamed model text and releases it redacted, one complete line at a time, so a secret split across deltas is still
/// caught. Lines inside a PEM private key block stay redacted until its END line. Past 64 KiB without a line break the buffer is released.
/// </summary>
public sealed class LineRedactor
{
    public const int MaxBufferChars = 64 * 1024;

    private static readonly Redactor Redactor = new();
    private readonly StringBuilder buffer = new();
    private bool inPrivateKey;

    /// <summary>Adds <paramref name="text"/>; returns the redacted complete lines it finished (with their line breaks), or an empty string.</summary>
    public string Push(string text)
    {
        buffer.Append(text);
        var content = buffer.ToString();
        var end = content.LastIndexOf('\n');
        if (end < 0)
        {
            return buffer.Length > MaxBufferChars ? Flush() : string.Empty;
        }

        buffer.Remove(0, end + 1);
        var output = new StringBuilder();
        foreach (var line in content[..end].Split('\n'))
        {
            output.Append(Redact(line)).Append('\n');
        }

        return buffer.Length > MaxBufferChars ? output.Append(Flush()).ToString() : output.ToString();
    }

    /// <summary>Releases the rest of the buffer, redacted.</summary>
    public string Flush()
    {
        var rest = buffer.ToString();
        buffer.Clear();
        return Redact(rest);
    }

    private string Redact(string line)
    {
        if (inPrivateKey)
        {
            if (line.Contains("-----END ", StringComparison.Ordinal))
            {
                inPrivateKey = false;
            }

            return line.Length == 0 ? line : Redactor.Marker;
        }

        if (line.Contains("-----BEGIN ", StringComparison.Ordinal) && line.Contains("PRIVATE KEY", StringComparison.Ordinal)
            && !line.Contains("-----END ", StringComparison.Ordinal))
        {
            inPrivateKey = true;
        }

        return Redactor.RedactText(line);
    }
}
