using System.Net.Sockets;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;
using Rackside.Transport.Console;
using Rackside.Transport.Ssh;

namespace Rackside.Operations;

/// <summary>A switch connected and at "#": channel, transcript and the console engine on top.</summary>
public sealed class SwitchConnection : IDisposable
{
    private SwitchConnection(IConsoleChannel channel, SessionTranscript transcript, ConsoleSession session, SshTrust? trust)
    {
        Channel = channel;
        Transcript = transcript;
        Session = session;
        Trust = trust;
    }

    public IConsoleChannel Channel { get; }

    public SessionTranscript Transcript { get; }

    public ConsoleSession Session { get; }

    /// <summary>The key this connection was pinned to (SSH only).</summary>
    public SshTrust? Trust { get; }

    public string Hostname => Session.Hostname;

    /// <summary>"ip:port" answers a TCP connection within the time.</summary>
    public static bool TcpOpen(string ip, int port, int timeoutMs)
    {
        using var c = new TcpClient();
        try
        {
            return c.ConnectAsync(ip, port).Wait(timeoutMs) && c.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// SSH: checks the fingerprint (security rule 2) and opens a shell pinned to it. The transcript goes to
    /// <paramref name="folder"/>/ssh_session.txt.
    /// </summary>
    public static SwitchConnection OverSsh(FingerprintRequest request, Credentials cred, string folder, FingerprintStore store, Translator t,
        SecretMasker masker, IOperatorUi ui, OperationLog log, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(cred);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);
        var presented = SshConnector.ReadHostKey(request.IP, request.Port, t);
        var trust = FingerprintPolicy.Confirm(request, presented, store, t, ui.Warn, log.Write);
        var channel = SshShellChannel.Open(trust, cred.User, cred.Password, t);
        return Start(channel, Path.Combine(folder, "ssh_session.txt"), cred, t, masker, cancel, trust);
    }

    /// <summary>Console (COM port or, in the tests, a pty). The transcript goes to <paramref name="folder"/>/console_session.txt.</summary>
    public static SwitchConnection OverConsole(string port, int baudRate, bool pty, Credentials cred, string folder, Translator t, SecretMasker masker, CancellationToken cancel)
    {
        var channel = SerialChannel.Open(port, baudRate, t, pty);
        return Start(channel, Path.Combine(folder, "console_session.txt"), cred, t, masker, cancel, null);
    }

    /// <summary>Any already open channel (a COM port opened by the caller, or a fake one in the tests).</summary>
    public static SwitchConnection Over(IConsoleChannel channel, string transcriptPath, Credentials cred, Translator t, SecretMasker masker, CancellationToken cancel) =>
        Start(channel, transcriptPath, cred, t, masker, cancel, null);

    private static SwitchConnection Start(IConsoleChannel channel, string transcriptPath, Credentials cred, Translator t, SecretMasker masker, CancellationToken cancel, SshTrust? trust)
    {
        SessionTranscript? transcript = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
            transcript = SessionTranscript.Create(transcriptPath, masker);
            var session = new ConsoleSession(channel, t, masker, transcript, cancel);
            var c = new SwitchConnection(channel, transcript, session, trust);
            session.Connect(cred.User, cred.Password, cred.Enable);
            session.RunCommand("terminal length 0", 20);
            session.RunCommand("terminal width 0", 20);
            return c;
        }
        catch
        {
            transcript?.Dispose();
            channel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What the restore reads from the target: version, interfaces, flash and whether it is
    /// at factory defaults.
    /// </summary>
    public TargetInfo Target(OperationLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var s = Session;
        var version = s.RunCommand("show version", 60);
        if (!Ci.IsMatch(version, "(?i)cisco"))
        {
            throw new SwitchException(s.Translator.T("The connected device does not look like a Cisco switch."));
        }
        var brief = s.RunCommand("show ip interface brief", 120);
        var dir = s.RunCommand("dir", 60);
        var run = s.RunCommand("show running-config | include ^hostname|^username|^enable|^ip access-list|^access-list", 120);
        var info = TargetInfo.Parse(version, brief, dir, run, s.Translator.T);
        if (!info.Clean)
        {
            log.Warn(Ci.F(s.Translator.T("The target switch is NOT at factory defaults: {0}."), info.Remains));
        }
        return info;
    }

    /// <summary>show version, only the version part.</summary>
    public TargetInfo Version()
    {
        var text = Session.RunCommand("show version", 60);
        try
        {
            return TargetInfo.Parse(text, string.Empty, string.Empty, string.Empty, Session.Translator.T);
        }
        catch (FormatException ex)
        {
            throw new SwitchException(ex.Message, ex);
        }
    }

    /// <summary>The whole running-config; refuses an incomplete one.</summary>
    public string FullRunning()
    {
        var r = Session.RunCommand("show running-config", 300);
        if (!Ci.IsMatch(r, "(?m)^hostname\\s+\\S+") || !Ci.IsMatch(r, "(?m)^end\\s*$"))
        {
            throw new SwitchException(Session.Translator.T("The running-config came out incomplete; not continuing."));
        }
        return r;
    }

    public void Dispose()
    {
        Transcript.Dispose();
        Channel.Dispose();
    }
}
