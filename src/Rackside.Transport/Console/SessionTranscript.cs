using System.Text;
using Rackside.Core.Security;

namespace Rackside.Transport.Console;

/// <summary>
/// Full session transcript (console_session.txt / ssh_session.txt) with the secrets masked. It keeps
/// text until a complete line arrives, because a secret can arrive split across two reads.
/// </summary>
public sealed class SessionTranscript(TextWriter writer, SecretMasker masker) : IDisposable
{
    private readonly StringBuilder pending = new();

    public static SessionTranscript Create(string path, SecretMasker masker) =>
        new(new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true }, masker);

    public void Write(string text, bool final = false)
    {
        pending.Append(text);
        var s = pending.ToString();
        var cut = s.LastIndexOf('\n') + 1;
        if (final || s.Length > 16384)
        {
            cut = s.Length;
        }
        if (cut > 0)
        {
            writer.Write(masker.Protect(s[..cut]));
            pending.Remove(0, cut);
        }
    }

    public void Dispose()
    {
        Write(string.Empty, final: true);
        writer.Dispose();
    }
}
