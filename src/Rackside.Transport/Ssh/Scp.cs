using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Transport.Console;
using Renci.SshNet;

namespace Rackside.Transport.Ssh;

/// <summary>
/// Upload to flash over SCP. SSH.NET's ScpClient always asks for "scp -t -d &lt;folder&gt;" and
/// the IOS/IOS-XE SCP server rejects -d ("-p -r -d options not supported"), so the classic protocol
/// ("scp -t flash:&lt;file&gt;") is spoken over a session pinned to the verified fingerprint.
/// </summary>
public static class Scp
{
    public static TimeSpan Upload(SshTrust trust, string user, string password, string localPath, string name, Translator t,
        Action<string, string>? log = null, Action<string>? status = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(t);
        log?.Invoke("INFO", Ci.F(t.T("SCP to {0} against fingerprint {1} {2}"), trust.Device, trust.Algorithm, trust.Normal));
        SshClient client;
        try
        {
            client = SshConnector.Connect(trust, user, password);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new SwitchException(Ci.F(t.T("SCP: could not open the SSH session: {0}. If it mentions the key or fingerprint, the device presented a different one from the verified one: do not continue without checking."), ex.Message), ex);
        }
        var clock = Stopwatch.StartNew();
        SshCommand? cmd = null;
        Stream? input = null;
        try
        {
            cmd = client.CreateCommand("scp -t flash:" + name);
            var running = cmd.BeginExecute();
            input = cmd.CreateInputStream();
            var output = cmd.OutputStream;
            ReadAck(output, 60, t.T("start"), t, cancel);

            var size = new FileInfo(localPath).Length;
            var hdr = Encoding.ASCII.GetBytes(Ci.F("C0644 {0} {1}", size, name) + "\n");
            input.Write(hdr, 0, hdr.Length);
            input.Flush();
            ReadAck(output, 60, t.T("file header"), t, cancel);

            using (var fs = File.OpenRead(localPath))
            {
                var buf = new byte[262144];
                long sent = 0;
                var last = clock.Elapsed;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    Stopped(cancel, t);
                    input.Write(buf, 0, n);
                    sent += n;
                    if ((clock.Elapsed - last).TotalMilliseconds >= 500)
                    {
                        last = clock.Elapsed;
                        var seconds = Math.Max(1, clock.Elapsed.TotalSeconds);
                        status?.Invoke(string.Format(CultureInfo.CurrentCulture, t.T("Sending {0} via SCP: {1:N1} of {2:N1} MB ({3:N0}%) at {4:N1} MB/s"),
                            name, sent / 1048576.0, size / 1048576.0, 100.0 * sent / Math.Max(1, size), sent / 1048576.0 / seconds));
                    }
                }
            }
            input.Write([0], 0, 1);
            input.Flush();
            status?.Invoke(Ci.F(t.T("{0} sent; waiting for the switch to finish writing it to flash..."), name));
            ReadAck(output, 1800, t.T("flash write"), t, cancel);
            // The switch already confirmed the write: if it closed the channel before us, closing here can
            // fail ("The channel is closed") with nothing wrong with the file (the MD5 is verified afterwards).
            try
            {
                input.Dispose();
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or IOException)
            {
            }
            input = null;
            try
            {
                WaitHandle(running.AsyncWaitHandle, 120, t.T("SCP close"), t, cancel);
                cmd.EndExecute(running);
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or SwitchException or ObjectDisposedException or InvalidOperationException)
            {
                log?.Invoke("WARN", Ci.F(t.T("SCP: the switch confirmed the write; closing the channel reported: {0}"), ex.Message));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SwitchException(Ci.F(t.T("SCP failed: {0}. Check that the switch has \"ip scp server enable\", \"aaa new-model\" and \"aaa authorization exec default local\", or use the USB."), ex.Message), ex);
        }
        finally
        {
            if (input is not null)
            {
                try
                {
                    input.Dispose();
                }
                catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or IOException)
                {
                }
            }
            cmd?.Dispose();
            client.Dispose();
        }
        return clock.Elapsed;
    }

    /// <summary>SCP receiver answer: 0 = ok; 1 or 2 = error followed by a message up to the end of the line.</summary>
    private static void ReadAck(Stream output, int seconds, string step, Translator t, CancellationToken cancel)
    {
        var b = new byte[1];
        var read = output.ReadAsync(b, 0, 1, CancellationToken.None);
        WaitHandle(((IAsyncResult)read).AsyncWaitHandle, seconds, t.T("SCP, ") + step, t, cancel);
        if (read.Result == 0)
        {
            throw new SwitchException(Ci.F(t.T("The switch closed the SCP connection ({0})."), step));
        }
        if (b[0] == 0)
        {
            return;
        }
        var msg = new StringBuilder();
        for (var i = 0; i < 512; i++)
        {
            var r = output.ReadAsync(b, 0, 1, CancellationToken.None);
            if (!r.Wait(10000) || r.Result == 0 || b[0] == 10)
            {
                break;
            }
            msg.Append((char)b[0]);
        }
        throw new SwitchException(Ci.F(t.T("The switch rejected the SCP ({0}): {1}"), step, msg.ToString().Trim()));
    }

    /// <summary>Waits for an asynchronous operation; honors "Stop".</summary>
    private static void WaitHandle(WaitHandle handle, int seconds, string what, Translator t, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        while (!handle.WaitOne(250))
        {
            Stopped(cancel, t);
            if (clock.Elapsed.TotalSeconds > seconds)
            {
                throw new SwitchException(Ci.F(t.T("The switch did not respond ({0}) in {1} s."), what, seconds));
            }
        }
    }

    private static void Stopped(CancellationToken cancel, Translator t)
    {
        if (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(t.T("Operation stopped by the operator."), cancel);
        }
    }
}
