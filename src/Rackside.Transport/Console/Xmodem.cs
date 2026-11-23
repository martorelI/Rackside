using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Rackside.Core.Localization;

namespace Rackside.Transport.Console;

/// <summary>The byte-level side of the serial port that Xmodem needs.</summary>
public interface IXmodemPort
{
    void DiscardInBuffer();

    /// <summary>One byte, or -1 if none arrived within the port's read timeout.</summary>
    int ReadByte();

    void WriteBytes(byte[] data, int offset, int count);
}

/// <summary>Result of a transfer. Md5Padding: MD5 including the 0x1A padding of the last block.</summary>
public sealed record XmodemResult(long Bytes, int Blocks, int BlockSize, int Retries, TimeSpan Duration, string Md5Padding);

/// <summary>Xmodem-1K (CRC) sender; drops to 128-byte blocks if the receiver rejects 1 KB.</summary>
public static class Xmodem
{
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const byte Can = 0x18;
    private const byte Eot = 0x04;
    private const byte CrcRequest = 0x43;

    private static readonly ushort[] CrcTable = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (ushort)(i << 8);
            for (var j = 0; j < 8; j++)
            {
                c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1);
            }
            t[i] = c;
        }
        return t;
    }

    /// <summary>An Xmodem packet (SOH = 128 / STX = 1024 bytes) with CRC-16 or checksum.</summary>
    public static byte[] BuildPacket(byte[] data, int offset, int size, int block, bool crc)
    {
        ArgumentNullException.ThrowIfNull(data);
        var p = new byte[3 + size + (crc ? 2 : 1)];
        p[0] = (byte)(size == 1024 ? 0x02 : 0x01);
        p[1] = (byte)(block & 0xFF);
        p[2] = (byte)(0xFF - (block & 0xFF));
        var n = Math.Min(size, data.Length - offset);
        Buffer.BlockCopy(data, offset, p, 3, n);
        for (var i = n; i < size; i++)
        {
            p[3 + i] = 0x1A;
        }
        if (crc)
        {
            ushort c = 0;
            for (var i = 0; i < size; i++)
            {
                c = (ushort)((c << 8) ^ CrcTable[((c >> 8) ^ p[3 + i]) & 0xFF]);
            }
            p[3 + size] = (byte)(c >> 8);
            p[4 + size] = (byte)(c & 0xFF);
        }
        else
        {
            var s = 0;
            for (var i = 0; i < size; i++)
            {
                s += p[3 + i];
            }
            p[3 + size] = (byte)(s & 0xFF);
        }
        return p;
    }

    /// <param name="log">(level, translated message): INFO / WARN, as in the operation log.</param>
    /// <param name="status">Translated progress text and percentage.</param>
    public static XmodemResult Send(IXmodemPort port, byte[] data, Translator t, Action<string, string>? log = null, Action<string, int>? status = null,
        CancellationToken cancel = default, int startSeconds = 120)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(t);
        port.DiscardInBuffer();
        bool? crc = null;
        var wait = Stopwatch.StartNew();
        while (crc is null)
        {
            if (wait.Elapsed.TotalSeconds > startSeconds)
            {
                throw new SwitchException(t.T("The switch did not start the Xmodem reception (no \"C\" or NAK arrived)."));
            }
            Stopped(cancel, t);
            var b = port.ReadByte();
            if (b == CrcRequest)
            {
                crc = true;
            }
            else if (b == Nak)
            {
                crc = false;
            }
        }
        log?.Invoke("INFO", F(t.T("Xmodem receiver ready ({0})."), crc.Value ? t.T("CRC") : t.T("checksum")));

        var size = crc.Value ? 1024 : 128;
        var block = 1;
        var offset = 0;
        var retries = 0;
        var clock = Stopwatch.StartNew();
        var lastStatus = clock.Elapsed;
        while (offset < data.Length)
        {
            var p = BuildPacket(data, offset, size, block, crc.Value);
            var attempts = 0;
            while (true)
            {
                port.WriteBytes(p, 0, p.Length);
                var r = WaitAnswer(port, 15, cancel, t);
                if (r == Ack)
                {
                    break;
                }
                if (r == Can)
                {
                    throw new SwitchException(F(t.T("The switch cancelled the transfer at block {0}."), block));
                }
                attempts++;
                retries++;
                if (block == 1 && size == 1024 && attempts >= 3)
                {
                    log?.Invoke("WARN", t.T("The receiver does not accept 1 KB blocks; 128-byte blocks are used."));
                    size = 128;
                    p = BuildPacket(data, offset, size, block, crc.Value);
                    attempts = 0;
                    continue;
                }
                if (attempts >= 10)
                {
                    throw new SwitchException(F(t.T("Block {0}: too many retries."), block));
                }
            }
            offset += size;
            block++;
            if ((clock.Elapsed - lastStatus).TotalMilliseconds >= 700 && status is not null)
            {
                lastStatus = clock.Elapsed;
                var sent = Math.Min(offset, data.Length);
                var speed = sent / Math.Max(1, clock.Elapsed.TotalSeconds);
                var left = TimeSpan.FromSeconds(Math.Max(0, (data.Length - offset) / Math.Max(1, speed)));
                var pct = (int)(100L * sent / data.Length);
                status(string.Format(CultureInfo.CurrentCulture, t.T("Xmodem: {0:N1} of {1:N1} MB ({2}%) at {3:N1} KB/s, {4:hh\\:mm\\:ss} left"),
                    sent / 1048576.0, data.Length / 1048576.0, pct, speed / 1024, left), pct);
            }
        }

        var end = false;
        for (var i = 0; i < 10 && !end; i++)
        {
            port.WriteBytes([Eot], 0, 1);
            end = WaitAnswer(port, 10, cancel, t) == Ack;
        }
        if (!end)
        {
            throw new SwitchException(t.T("The switch did not confirm the end of the transfer (EOT)."));
        }

        // MD5 with the Xmodem padding (0x1A up to the end of the last block), in case the switch keeps it
        var padding = (size - (data.Length % size)) % size;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(data);
        md5.AppendData(Enumerable.Repeat((byte)0x1A, padding).ToArray());
        var hash = Convert.ToHexStringLower(md5.GetHashAndReset());
        return new XmodemResult(data.Length, block - 1, size, retries, clock.Elapsed, hash);
    }

    /// <summary>ACK, NAK, CAN or -1 if there was no answer.</summary>
    private static int WaitAnswer(IXmodemPort port, int seconds, CancellationToken cancel, Translator t)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            Stopped(cancel, t);
            var b = port.ReadByte();
            if (b is Ack or Nak or Can)
            {
                return b;
            }
        }
        return -1;
    }

    private static void Stopped(CancellationToken cancel, Translator t)
    {
        if (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(t.T("Operation stopped by the operator."), cancel);
        }
    }

    private static string F(string format, params object?[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
}
