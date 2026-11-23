using System.Collections.Concurrent;
using System.Text;
using Rackside.Transport.Console;

namespace Rackside.Tests.Transport;

/// <summary>
/// In-memory switch console: echoes what is typed and, on each Enter, asks <see cref="OnLine"/> for the
/// answer. Raw keys (Ctrl+C, a single "n" for [confirm], the space for --More--) go to <see cref="OnKey"/>.
/// </summary>
internal sealed class FakeChannel : IConsoleChannel
{
    private readonly ConcurrentQueue<string> output = new();
    private readonly StringBuilder line = new();

    public Func<string, string> OnLine { get; set; } = _ => string.Empty;

    public Func<char, string?> OnKey { get; set; } = _ => null;

    public List<string> Lines { get; } = [];

    public List<char> Keys { get; } = [];

    public bool Echo { get; set; } = true;

    public void Emit(string text) => output.Enqueue(text);

    public string ReadExisting()
    {
        var sb = new StringBuilder();
        while (output.TryDequeue(out var s))
        {
            sb.Append(s);
        }
        return sb.ToString();
    }

    public void Write(string text)
    {
        foreach (var c in text)
        {
            if (c == '\r')
            {
                var l = line.ToString();
                line.Clear();
                Lines.Add(l);
                Emit((Echo ? l : string.Empty) + "\r\n" + OnLine(l));
            }
            else if (c < ' ' || text.Length == 1)
            {
                Keys.Add(c);
                var r = OnKey(c);
                if (r is not null)
                {
                    Emit(r);
                }
                else if (c >= ' ')
                {
                    line.Append(c);
                }
            }
            else
            {
                line.Append(c);
            }
        }
    }

    public bool IsOpen => true;

    public bool IsSsh => false;

    public void Dispose()
    {
    }
}
