using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Transport.Console;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Rackside.Transport.Ssh;

/// <summary>SSH connections, always pinned to the key accepted by <see cref="FingerprintPolicy"/>.</summary>
public static class SshConnector
{
    /// <summary>SSH-level keepalive (sends no keystrokes): keeps idle sessions from going stale.</summary>
    public static TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Key presented by the device, read without authenticating.</summary>
    public static PresentedKey ReadHostKey(string ip, int port, Translator t)
    {
        ArgumentNullException.ThrowIfNull(t);
        PresentedKey? key = null;
        var info = new ConnectionInfo(ip, port, "rackside", new NoneAuthenticationMethod("rackside")) { Timeout = ConnectTimeout };
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            key = new PresentedKey(e.HostKeyName, e.FingerPrintSHA256);
            e.CanTrust = false;
        };
        try
        {
            client.Connect();
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            if (key is null)
            {
                throw new SwitchException(Ci.F(t.T("Could not read the SSH fingerprint of {0}:{1}."), ip, port) + " " + ex.Message, ex);
            }
        }
        return key ?? throw new SwitchException(Ci.F(t.T("Could not read the SSH fingerprint of {0}:{1}."), ip, port));
    }

    /// <summary>
    /// An authenticated client that only accepts <paramref name="trust"/>'s key. Password and
    /// keyboard-interactive (answering every prompt with the password).
    /// </summary>
    public static SshClient Connect(SshTrust trust, string user, string password)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var kbd = new KeyboardInteractiveAuthenticationMethod(user);
        kbd.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts)
            {
                p.Response = password;
            }
        };
        var info = new ConnectionInfo(trust.IP, trust.Port, user, new PasswordAuthenticationMethod(user, password), kbd) { Timeout = ConnectTimeout };
        var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
        client.HostKeyReceived += (_, e) =>
            e.CanTrust = Ci.Eq(e.HostKeyName, trust.Algorithm) && NormalFingerprint.Parse(e.FingerPrintSHA256).SameAs(trust.Normal);
        try
        {
            client.Connect();
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }
}

/// <summary>An SSH shell with the same interface as the serial port.</summary>
public sealed class SshShellChannel : IConsoleChannel
{
    private readonly SshClient client;
    private readonly ShellStream stream;

    private SshShellChannel(SshClient client, ShellStream stream)
    {
        this.client = client;
        this.stream = stream;
    }

    public static SshShellChannel Open(SshTrust trust, string user, string password, Translator t)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(t);
        SshClient client;
        try
        {
            client = SshConnector.Connect(trust, user, password);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new SwitchException(Ci.F(t.T("Could not open SSH to {0}: {1}. If it mentions the key or fingerprint, the device presented a different one from the verified one: do not continue without checking."), trust.IP, ex.Message), ex);
        }
        if (!client.IsConnected)
        {
            client.Dispose();
            throw new SwitchException(Ci.F(t.T("Could not open SSH to {0}."), trust.IP));
        }
        try
        {
            return new SshShellChannel(client, client.CreateShellStream("vt100", 511, 24, 0, 0, 1048576));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public string ReadExisting()
    {
        if (!client.IsConnected)
        {
            throw new ConnectionClosedException();
        }
        return stream.DataAvailable ? stream.Read() : string.Empty;
    }

    public void Write(string text)
    {
        stream.Write(text);
        stream.Flush();
    }

    public bool IsOpen => client.IsConnected;

    public bool IsSsh => true;

    public void Dispose()
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException)
        {
        }
        client.Dispose();
    }
}
