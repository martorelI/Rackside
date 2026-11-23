using System.IO.Ports;
using System.Text;
using Rackside.Core.Localization;

namespace Rackside.Transport.Console;

/// <summary>COM port (8N1, ASCII).</summary>
public sealed class SerialChannel : IConsoleChannel, IXmodemPort
{
    private readonly SerialPort port;

    private SerialChannel(SerialPort port) => this.port = port;

    /// <param name="pty">
    /// For the simulators' pty on Linux: no handshake, DTR or RTS (a pty does not accept those ioctls).
    /// </param>
    public static SerialChannel Open(string name, int baudRate, Translator t, bool pty = false)
    {
        ArgumentNullException.ThrowIfNull(t);
        var p = new SerialPort(name, baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            ReadTimeout = 500,
            WriteTimeout = 5000,
        };
        if (!pty)
        {
            p.Handshake = Handshake.None;
            p.DtrEnable = true;
            p.RtsEnable = true;
        }
        try
        {
            p.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            p.Dispose();
            throw new SwitchException(string.Format(System.Globalization.CultureInfo.InvariantCulture, t.T("Could not open {0}: {1}. Close PuTTY or any other program using it."), name, ex.Message), ex);
        }
        return new SerialChannel(p);
    }

    /// <summary>The COM ports of the computer, sorted.</summary>
    public static IReadOnlyList<string> PortNames() => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToList();

    public string ReadExisting() => port.ReadExisting();

    public void Write(string text) => port.Write(text);

    public void WriteBytes(byte[] data, int offset, int count) => port.Write(data, offset, count);

    /// <summary>One byte, or -1 after the read timeout (500 ms).</summary>
    public int ReadByte()
    {
        try
        {
            return port.ReadByte();
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }

    public void DiscardInBuffer() => port.DiscardInBuffer();

    public bool IsOpen => port.IsOpen;

    public bool IsSsh => false;

    public SerialPort Port => port;

    /// <summary>Console speed (Xmodem raises it in the boot loader and lowers it afterwards).</summary>
    public int BaudRate
    {
        get => port.BaudRate;
        set => port.BaudRate = value;
    }

    public void Dispose()
    {
        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (IOException)
        {
        }
        port.Dispose();
    }
}
