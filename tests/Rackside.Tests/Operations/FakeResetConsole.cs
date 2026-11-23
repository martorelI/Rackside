using System.Text;
using Rackside.Transport.Console;

namespace Rackside.Tests.Operations;

/// <summary>
/// A 2960-X console with login + enable that erases its keys, vlan.dat
/// and startup-config, reloads without saving and boots into the setup dialog, autoinstall and "Switch>".
/// </summary>
internal sealed class FakeResetConsole : IConsoleChannel
{
    private readonly StringBuilder output = new();
    private readonly Queue<string> pending = new();
    private string input = string.Empty;
    private string state = "idle";
    private string mode = string.Empty;

    public string Host { get; } = "SW07";
    public bool Startup { get; private set; } = true;
    public bool Vlan { get; private set; } = true;
    public List<string> Lines { get; } = [];

    public string ReadExisting()
    {
        lock (output)
        {
            if (pending.Count > 0)
            {
                output.Append(pending.Dequeue());
            }
            var s = output.ToString();
            output.Clear();
            return s;
        }
    }

    private void P(string s)
    {
        lock (output)
        {
            output.Append(s);
        }
    }

    private string Pr() => "\r\n" + Host + mode + "#";

    public void Write(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                var l = input;
                input = string.Empty;
                Lines.Add(l);
                Line(l);
            }
            else
            {
                input += ch;
                if (!state.Contains("pw", StringComparison.Ordinal))
                {
                    P(ch.ToString());
                }
            }
        }
    }

    private void Line(string l)
    {
        switch (state)
        {
            case "idle": P("\r\n\r\nUser Access Verification\r\n\r\nUsername: "); state = "user"; return;
            case "user": P("\r\nPassword: "); state = "pw1"; return;
            case "pw1": P("\r\n" + Host + ">"); state = "exec"; return;
            case "exec":
                if (l == "enable") { P("\r\nPassword: "); state = "pw2"; } else { P("\r\n" + Host + ">"); }
                return;
            case "pw2": state = "priv"; P(Pr()); return;
            case "zero": state = "priv"; P(Pr()); return;
            case "erase": Startup = false; state = "priv"; P("[OK]\r\nErase of nvram: complete" + Pr()); return;
            case "save": P("\r\nProceed with reload? [confirm]"); state = "conf"; return;
            case "conf":
                state = "boot";
                lock (output)
                {
                    pending.Enqueue("\r\n\r\nBoot Sector Filesystem (bs) installed\r\nLoading \"flash:/c2960x.bin\"...@@@@@@@\r\n");
                    pending.Enqueue("Restricted Rights Legend...\r\n         --- System Configuration Dialog ---\r\n\r\nWould you like to enter the initial configuration dialog? [yes/no]: ");
                }
                return;
            case "boot": P("\r\nWould you like to terminate autoinstall? [yes]: "); state = "auto"; return;
            case "auto": P("\r\n\r\nPress RETURN to get started!\r\n"); state = "ret"; return;
            case "ret": P("\r\nSwitch>"); state = "end"; return;
        }
        var o = string.Empty;
        switch (l)
        {
            case "terminal length 0" or "terminal width 0" or "no logging console" or "" or "delete /force flash:private-config.text":
                break;
            case "show version":
                o = "Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(7)E3, RELEASE\r\nModel number                    : WS-C2960X-48FPD-L\r\nSystem serial number            : FOC1234X0AB";
                break;
            case "show switch":
                o = "Switch/Stack Mac Address : 7010.5c11.2233\r\n                                           H/W   Current\r\nSwitch#  Role   Mac Address     Priority Version  State\r\n----------------------------------------------------------\r\n*1       Master 7010.5c11.2233     1      4       Ready";
                break;
            case "show boot":
                o = "BOOT path-list      : flash:/c2960x-universalk9-mz.152-7.E3/c2960x-universalk9-mz.152-7.E3.bin\r\nConfig file         : flash:/config.text";
                break;
            case "dir":
                o = "Directory of flash:/\r\n\r\n    2  -rwx        1048   Mar 1 1993 00:01:23 +00:00  multiple-fs\r\n    3  drwx         512   Mar 1 1993 00:08:51 +00:00  c2960x-universalk9-mz.152-7.E3\r\n";
                if (Vlan) { o += "    6  -rwx         736   Mar 1 1993 00:03:10 +00:00  vlan.dat\r\n"; }
                if (Startup) { o += "    7  -rwx        5512   Mar 1 1993 02:10:10 +00:00  config.text\r\n    8  -rwx        3096   Mar 1 1993 02:10:10 +00:00  private-config.text\r\n"; }
                o += "\r\n122185728 bytes total (90000000 bytes free)";
                break;
            case "show running-config":
                o = $"Building configuration...\r\n\r\nCurrent configuration : 5512 bytes\r\n!\r\nversion 15.2\r\nhostname {Host}\r\n!\r\n%LINK-3-UPDOWN: noise\r\ninterface Gi1/0/1\r\n!\r\nend";
                break;
            case "configure terminal": mode = "(config)"; o = "Enter configuration commands, one per line.  End with CNTL/Z."; break;
            case "crypto key zeroize rsa": P("\r\n% All keys will be removed.\r\nDo you really want to remove these keys? [yes/no]: "); state = "zero"; return;
            case "end": mode = string.Empty; break;
            case "delete /force flash:vlan.dat": Vlan = false; break;
            case "write erase": P("\r\nErasing the nvram filesystem will remove all configuration files! Continue? [confirm]"); state = "erase"; return;
            case "show startup-config": o = Startup ? "version 15.2" : "startup-config is not present"; break;
            case "reload": P("\r\n\r\nSystem configuration has been modified. Save? [yes/no]: "); state = "save"; return;
            default: o = "                 ^\r\n% Invalid input detected at '^' marker."; break;
        }
        if (o.Length > 0) { P("\r\n" + o); }
        P(Pr());
    }

    public bool IsOpen => true;

    public bool IsSsh => false;

    public void Dispose()
    {
    }
}
