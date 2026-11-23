namespace Rackside.Transport.Console;

/// <summary>
/// A text channel to the switch: the serial port or an SSH shell (both behind the same interface, so the
/// console engine is shared).
/// </summary>
public interface IConsoleChannel : IDisposable
{
    /// <summary>Whatever has arrived since the last read, or an empty string (never blocks).</summary>
    string ReadExisting();

    void Write(string text);

    bool IsOpen { get; }

    /// <summary>True for SSH (the console-only questions do not apply).</summary>
    bool IsSsh { get; }
}

/// <summary>The SSH connection was closed by the other end.</summary>
public sealed class ConnectionClosedException() : Exception("CONNECTION_CLOSED");
