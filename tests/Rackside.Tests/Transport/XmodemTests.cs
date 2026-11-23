using System.Collections.Concurrent;
using System.Security.Cryptography;
using Rackside.Core.Localization;
using Rackside.Transport.Console;

namespace Rackside.Tests.Transport;

public class XmodemTests
{
    /// <summary>In-memory receiver: checks each packet (number, CRC or checksum) and keeps the data.</summary>
    private sealed class Receiver(bool crc = true, bool accept1K = true, int cancelAtBlock = 0, int corruptOnce = 0) : IXmodemPort
    {
        private readonly ConcurrentQueue<int> answers = new();
        private bool started;
        private bool corrupted;

        public MemoryStream Data { get; } = new();

        public int Expected { get; private set; } = 1;

        public bool Ended { get; private set; }

        public void DiscardInBuffer() => answers.Clear();

        public int ReadByte()
        {
            if (!started)
            {
                started = true;
                return crc ? 0x43 : 0x15;
            }
            if (answers.TryDequeue(out var b))
            {
                return b;
            }
            Thread.Sleep(1);
            return -1;
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            if (count == 1 && data[offset] == 0x04)
            {
                Ended = true;
                answers.Enqueue(0x06);
                return;
            }
            var size = data[offset] == 0x02 ? 1024 : 128;
            if (size == 1024 && !accept1K)
            {
                answers.Enqueue(0x15);
                return;
            }
            var block = data[offset + 1];
            Assert.Equal(0xFF - block, data[offset + 2]);
            if (cancelAtBlock > 0 && block == cancelAtBlock)
            {
                answers.Enqueue(0x18);
                return;
            }
            if (corruptOnce > 0 && block == corruptOnce && !corrupted)
            {
                corrupted = true;
                answers.Enqueue(0x15);
                return;
            }
            var payload = data.AsSpan(offset + 3, size).ToArray();
            var check = Xmodem.BuildPacket(payload, 0, size, block, crc);
            Assert.True(check.AsSpan(3 + size).SequenceEqual(data.AsSpan(offset + 3 + size, count - 3 - size)), "bad CRC/checksum");
            Assert.Equal((byte)(Expected & 0xFF), block);
            Expected++;
            Data.Write(payload);
            answers.Enqueue(0x06);
        }
    }

    private static byte[] Image(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void Sends_1K_blocks_with_crc_and_reports_the_padded_md5()
    {
        var data = Image(5000);
        var rx = new Receiver();
        var r = Xmodem.Send(rx, data, Translator.English);
        Assert.True(rx.Ended);
        Assert.Equal(1024, r.BlockSize);
        Assert.Equal(5, r.Blocks);
        var received = rx.Data.ToArray();
        Assert.Equal(data, received[..data.Length]);
        Assert.All(received[data.Length..], b => Assert.Equal(0x1A, b));
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(received)), r.Md5Padding);
    }

    [Fact]
    public void Drops_to_128_byte_blocks_when_1K_is_rejected()
    {
        var logs = new List<string>();
        var rx = new Receiver(accept1K: false);
        var r = Xmodem.Send(rx, Image(300), Translator.English, (level, msg) => logs.Add(level + " " + msg));
        Assert.Equal(128, r.BlockSize);
        Assert.Equal(3, r.Blocks);
        Assert.Contains("WARN The receiver does not accept 1 KB blocks; 128-byte blocks are used.", logs);
    }

    [Fact]
    public void Checksum_mode_when_the_receiver_sends_nak()
    {
        var r = Xmodem.Send(new Receiver(crc: false), Image(200), Translator.English);
        Assert.Equal(128, r.BlockSize);
    }

    [Fact]
    public void A_nak_resends_the_same_block()
    {
        var rx = new Receiver(corruptOnce: 2);
        var r = Xmodem.Send(rx, Image(4000), Translator.English);
        Assert.Equal(1, r.Retries);
        Assert.Equal(Image(4000), rx.Data.ToArray()[..4000]);
    }

    [Fact]
    public void A_cancel_from_the_switch_stops()
    {
        var ex = Assert.Throws<SwitchException>(() => Xmodem.Send(new Receiver(cancelAtBlock: 2), Image(4000), Translator.Spanish));
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Block_numbers_wrap_after_255()
    {
        var rx = new Receiver(accept1K: false);
        var r = Xmodem.Send(rx, Image(128 * 300), Translator.English);
        Assert.Equal(300, r.Blocks);
        Assert.Equal(301, rx.Expected);
    }
}
