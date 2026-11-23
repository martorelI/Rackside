using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Core.Restore;
using Rackside.Core.Security;

namespace Rackside.Transport.Console;

/// <summary>What <see cref="ConsoleSession.Wait"/> found: the pattern key, the clean text read and the match.</summary>
public sealed record WaitResult(string Key, string Text, Match Match);

/// <summary>Answer to a configuration line: mode it was left in (config, config-if... or exec), output and % messages.</summary>
public sealed record ConfigAnswer(string Mode, string Output, IReadOnlyList<string> Messages, IReadOnlyList<string> Errors);

/// <summary>A line that failed or warned while applying.</summary>
public sealed record ApplyIssue(string Line, string Answer);

public sealed record ApplyResult(IReadOnlyList<ApplyIssue> Errors, IReadOnlyList<ApplyIssue> Warnings);

/// <summary>
/// The console engine (serial or SSH): read until a prompt, commands, login and enable, configuration
/// lines with only the expected questions answered.
/// </summary>
public sealed class ConsoleSession(IConsoleChannel channel, Translator t, SecretMasker masker, SessionTranscript? transcript = null, CancellationToken cancel = default)
{
    /// <summary>Generic prompt: the hostname changes in the middle of the restore.</summary>
    public const string PromptLineRegex = @"\s*[A-Za-z0-9][\w\.\-]*(?:\([^\)\n]*\))?#\s*";

    public const string PromptRegex = "(?:^|\\n)" + PromptLineRegex + "$";

    /// <summary>
    /// Switch question (not the command prompt): [confirm], [yes/no], [y/n], (y/n)?, "...?", "...[value]:",
    /// Username:/Password:. Lines that start with the prompt (echo of what was sent) do not count.
    /// </summary>
    public const string QuestionRegex = @"(?:^|\n)(?!\s*[A-Za-z0-9][\w\.\-]*(?:\([^\)\n]*\))?#)[^\n]*(?:\[confirm\]|\[yes/no\]:?|\[y/n\](?:\s*\[[^\]]*\])?\s*:?|\(y/n\)\??|\?|\]\s*:|(?i:password|passcode|username)\s*:)\s*$";

    public const string ErrorRegex = @"(?i)^%\s+(Invalid input|Incomplete command|Ambiguous command|Unrecognized command|Unknown|Error|Bad |Cannot|Can not|Please|.*not supported|.*not allowed|.*does not exist on|.*failed|.*denied)";

    private const int PollMs = 50;

    public IConsoleChannel Channel => channel;

    public Translator Translator => t;

    public SecretMasker Masker => masker;

    public CancellationToken Cancellation => cancel;

    /// <summary>Hostname seen at the last successful login.</summary>
    public string Hostname { get; private set; } = string.Empty;

    /// <summary>Reads what is available (and copies it to the transcript).</summary>
    public string Read()
    {
        var text = channel.ReadExisting();
        if (!string.IsNullOrEmpty(text))
        {
            transcript?.Write(text);
        }
        return text ?? string.Empty;
    }

    public void SendLine(string text) => channel.Write(text + "\r");

    public void Write(string text) => channel.Write(text);

    public void Sleep(int ms)
    {
        if (cancel.WaitHandle.WaitOne(ms))
        {
            ThrowIfStopped();
        }
    }

    public void ThrowIfStopped()
    {
        if (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(t.T("Operation stopped by the operator."), cancel);
        }
    }

    /// <summary>Removes VT100 sequences, --More--, control characters and CR.</summary>
    public static string CleanText(string? text)
    {
        var s = Ci.Replace(text, "\\x1B\\[[0-9;?]*[A-Za-z]", string.Empty);
        s = Ci.Replace(s, "\\s*--More--\\s*(\\x08+\\s*\\x08*)*", string.Empty);
        s = Ci.Replace(s, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", string.Empty);
        return s.Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the console until the end of the text matches one of the patterns (in order).
    /// </summary>
    /// <param name="pushSeconds">&gt; 0: if nothing matches after N seconds of silence, sends an Enter (once).</param>
    /// <param name="noThrow">On timeout returns null.</param>
    /// <param name="returnText">On timeout returns Key = TIMEOUT with the text read.</param>
    /// <param name="stallSeconds">&gt; 0: throws if no data at all arrives for N seconds (the session seems stuck).</param>
    public WaitResult? Wait(IReadOnlyList<(string Key, string Pattern)> patterns, int timeoutSeconds = 60, int silenceMs = 500, int pushSeconds = 0,
        bool noThrow = false, bool returnText = false, int stallSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var sb = new StringBuilder();
        var clock = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(timeoutSeconds);
        var lastDatum = clock.Elapsed;
        var pushed = false;
        while (clock.Elapsed < limit)
        {
            ThrowIfStopped();
            var r = Read();
            if (r.Length > 0)
            {
                sb.Append(r);
                lastDatum = clock.Elapsed;
                if (Ci.IsMatch(r, "--More--\\s*$"))
                {
                    channel.Write(" ");
                }
                continue;
            }
            var silence = (clock.Elapsed - lastDatum).TotalMilliseconds;
            if (silence >= silenceMs)
            {
                var text = CleanText(sb.ToString());
                foreach (var (key, pattern) in patterns)
                {
                    if (Ci.TryMatch(text, pattern, out var m))
                    {
                        return new WaitResult(key, text, m);
                    }
                }
                if (pushSeconds > 0 && !pushed && silence >= pushSeconds * 1000)
                {
                    SendLine(string.Empty);
                    pushed = true;
                }
                if (stallSeconds > 0 && silence >= stallSeconds * 1000)
                {
                    throw new SwitchException(Ci.F(t.T("No data from the switch for {0} s: the session seems stuck. Last output: {1}"), stallSeconds, masker.Protect(Tail(text))));
                }
            }
            Sleep(PollMs);
        }
        if (returnText)
        {
            return new WaitResult("TIMEOUT", CleanText(sb.ToString()), Match.Empty);
        }
        if (noThrow)
        {
            return null;
        }
        var tail = Tail(CleanText(sb.ToString()));
        tail = Ci.Replace(tail, "\\n+", " | ").Trim();
        throw new SwitchException(Ci.F(t.T("Timed out ({0}s). Last output: {1}"), timeoutSeconds, masker.Protect(tail)));
    }

    private static string Tail(string text) => text.Length > 200 ? text[^200..] : text;

    /// <summary>Removes the command echo and the final prompt.</summary>
    public static string CommandOutput(string? text, string? command)
    {
        var lines = (text ?? string.Empty).Split('\n');
        var start = 0;
        if (!string.IsNullOrEmpty(command))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(command, StringComparison.Ordinal))
                {
                    start = i + 1;
                    break;
                }
            }
        }
        var end = lines.Length - 1;
        while (end >= start && (string.IsNullOrWhiteSpace(lines[end]) || Ci.IsMatch(lines[end], "^" + PromptLineRegex + "$")))
        {
            end--;
        }
        if (end < start)
        {
            return string.Empty;
        }
        return string.Join("\r\n", lines[start..(end + 1)].Select(l => l.TrimEnd()));
    }

    /// <summary>Exec mode command; returns the output without echo or prompt.</summary>
    public string RunCommand(string command, int timeoutSeconds = 120, int pushSeconds = 5)
    {
        Read();
        SendLine(command);
        var r = Wait([("PROMPT", PromptRegex)], timeoutSeconds, pushSeconds: pushSeconds)!;
        return CommandOutput(r.Text, command);
    }

    /// <summary>Wakes up the console and gets to privileged mode. Returns the hostname.</summary>
    public string Connect(string? user, string? password, string? enable)
    {
        (string, string)[] patterns =
        [
            ("Invalid", "(?i)%\\s*(Login invalid|Bad passwords?|Bad secrets?|Authentication failed|Access denied)"),
            ("NoEnable", "(?i)%\\s*No password set"),
            ("Rommon", "(?:^|\\n)\\s*(switch:|rommon\\s*\\d*\\s*>)\\s*$"),
            ("Dialog", "(?i)initial configuration dialog\\?\\s*\\[yes/no\\]:\\s*$"),
            ("Basic", "(?i)basic management setup\\?\\s*\\[yes/no\\]:\\s*$"),
            ("Autoinst", "(?i)terminate autoinstall\\?\\s*\\[yes\\]:\\s*$"),
            ("Return", "(?i)Press RETURN to get started"),
            ("User", "(?i)(Username|User Name|login):\\s*$"),
            ("Password", "(?i)Password:\\s*$"),
            ("Priv", "(?:^|\\n)\\s*([A-Za-z0-9][\\w\\.\\-]*)#\\s*$"),
            ("NoPriv", "(?:^|\\n)\\s*([A-Za-z0-9][\\w\\.\\-]*)>\\s*$"),
        ];
        var sentEnable = false;
        var passwordAttempts = 0;
        var noAnswer = 0;
        SendLine(string.Empty);
        for (var n = 0; n < 25; n++)
        {
            var r = Wait(patterns, timeoutSeconds: 15, silenceMs: 800, noThrow: true);
            if (r is null)
            {
                noAnswer++;
                if (noAnswer >= 3)
                {
                    throw new SwitchException(t.T("No response on the console. Check the cable, the COM port and the speed."));
                }
                SendLine(string.Empty);
                continue;
            }
            switch (r.Key)
            {
                case "Invalid":
                    throw new SwitchException(Ci.F(t.T("Login rejected by the switch: {0}"), r.Match.Groups[0].Value));
                case "NoEnable":
                    throw new SwitchException(t.T("The switch has no enable password configured for the console (\"% No password set\")."));
                case "Rommon":
                    throw new SwitchException(t.T("The switch is in ROMMON (prompt \"switch:\"); IOS did not boot."));
                case "Dialog":
                case "Basic":
                    SendLine("no");
                    break;
                case "Autoinst":
                case "Return":
                    SendLine(string.Empty);
                    break;
                case "User":
                    if (string.IsNullOrEmpty(user))
                    {
                        throw new SwitchException(t.T("The console asks for a username but no username was entered."));
                    }
                    sentEnable = false;
                    SendLine(user);
                    break;
                case "Password":
                    passwordAttempts++;
                    if (passwordAttempts > 3)
                    {
                        throw new SwitchException(t.T("Too many password attempts on the console."));
                    }
                    SendLine((sentEnable ? enable : password) ?? string.Empty);
                    break;
                case "NoPriv":
                    sentEnable = true;
                    SendLine("enable");
                    break;
                case "Priv":
                    Hostname = r.Match.Groups[1].Value;
                    return Hostname;
            }
        }
        throw new SwitchException(t.T("Could not reach privileged mode (#)."));
    }

    /// <summary>The switch asked something this step does not expect: answer NO (or cancel) and stop.</summary>
    public UnexpectedQuestionException StopUnexpectedQuestion(string text, string command)
    {
        var question = string.Join(' ', (text ?? string.Empty).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(2)).Trim();
        string said;
        if (Ci.IsMatch(text, "\\[confirm\\]\\s*$"))
        {
            channel.Write("n");
            said = "n";
        }
        else if (Ci.IsMatch(text, "(?i)(\\[yes/no\\]|\\[no\\])\\s*:?\\s*$"))
        {
            SendLine("no");
            said = "no";
        }
        else if (Ci.IsMatch(text, "(?i)(\\[y/n\\]|\\(y/n\\))[^\\n]*$"))
        {
            SendLine("n");
            said = "n";
        }
        else
        {
            channel.Write("\u0003");
            said = "Ctrl+C";
        }
        try
        {
            Wait([("PROMPT", PromptRegex), ("BL", "(?:^|\\n)\\s*switch:\\s*$")], timeoutSeconds: 30, silenceMs: 800, noThrow: true);
        }
        catch (SwitchException)
        {
        }
        return new UnexpectedQuestionException(Ci.F(t.T("Unexpected question from the switch for \"{0}\": \"{1}\". Answered \"{2}\" and the operation was stopped for safety."), masker.Protect(command), masker.Protect(question), said));
    }

    /// <summary>
    /// Sends a line and waits for the prompt. Only answers the questions given in <paramref name="answers"/>
    /// (regex -> answer, in order); any other question stops the operation.
    /// </summary>
    public ConfigAnswer SendConfigLine(string line, int timeoutSeconds = 60, IReadOnlyList<(string Pattern, string Answer)>? answers = null)
    {
        Read();
        SendLine(line);
        var patterns = new List<(string, string)>();
        var replies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (answers is not null)
        {
            for (var i = 0; i < answers.Count; i++)
            {
                var key = Ci.F("R{0}", i);
                patterns.Add((key, answers[i].Pattern));
                replies[key] = answers[i].Answer;
            }
        }
        patterns.Add(("PROMPT", PromptRegex));
        patterns.Add(("QUESTION", QuestionRegex));
        var all = new StringBuilder();
        for (var n = 0; n < 5; n++)
        {
            var r = Wait(patterns, timeoutSeconds, silenceMs: 200, pushSeconds: 4)!;
            all.Append(r.Text);
            if (r.Key == "PROMPT")
            {
                break;
            }
            if (r.Key == "QUESTION")
            {
                throw StopUnexpectedQuestion(all.ToString(), line);
            }
            SendLine(replies[r.Key]);
        }
        var text = all.ToString();
        var mode = Ci.TryMatch(text, "\\(([^\\)\\n]*)\\)#\\s*$", out var mm) ? mm.Groups[1].Value : "exec";
        var output = CommandOutput(text, line);
        var messages = Ci.Split(output, "\\r?\\n").Where(l => Ci.IsMatch(l, "^\\s*%\\s")).Select(l => l.Trim()).ToList();
        var errors = messages.Where(m => Ci.IsMatch(m, ErrorRegex)).ToList();
        return new ConfigAnswer(mode, output, messages, errors);
    }

    /// <summary>
    /// A command that can take a while (copy, archive, install, verify). Only answers the expected questions
    /// for that command; any other question (or more than 5 answers) stops it.
    /// </summary>
    public string RunLong(string command, int timeoutSeconds = 3600, IReadOnlyList<(string Pattern, string Answer)>? answers = null, int stallSeconds = 0)
    {
        Read();
        SendLine(command);
        var patterns = new List<(string, string)>();
        for (var i = 0; i < (answers?.Count ?? 0); i++)
        {
            patterns.Add((Ci.F("R{0}", i), answers![i].Pattern));
        }
        patterns.Add(("PROMPT", PromptRegex));
        patterns.Add(("QUESTION", QuestionRegex));
        var all = new StringBuilder();
        var clock = Stopwatch.StartNew();
        var answered = 0;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var left = (int)Math.Max(5, timeoutSeconds - clock.Elapsed.TotalSeconds);
            var r = Wait(patterns, left, silenceMs: 1500, pushSeconds: 45, stallSeconds: stallSeconds)!;
            all.Append(r.Text);
            if (r.Key == "PROMPT")
            {
                return CommandOutput(all.ToString(), command);
            }
            if (r.Key == "QUESTION" || answered >= 5)
            {
                throw StopUnexpectedQuestion(all.ToString(), command);
            }
            SendLine(answers![int.Parse(r.Key[1..], System.Globalization.CultureInfo.InvariantCulture)].Answer);
            answered++;
        }
        throw new SwitchException(Ci.F(t.T("\"{0}\" did not finish in {1} s."), command, timeoutSeconds));
    }

    /// <summary>The only questions a reload may ask: do not save (it was saved before if requested) and confirm.</summary>
    public static readonly IReadOnlyList<(string Pattern, string Answer)> ReloadAnswers =
    [
        (@"(?i)modified[^\n]*Save\?\s*\[yes/no\]:\s*$", "no"),
        (@"(?i)Proceed with reload\?\s*\[confirm\]\s*$", string.Empty),
    ];

    /// <summary>reload answering "no" to saving and confirming; any other question stops it.</summary>
    public void ReloadWithoutSaving()
    {
        Read();
        SendLine("reload");
        var patterns = new List<(string, string)>();
        for (var i = 0; i < ReloadAnswers.Count; i++)
        {
            patterns.Add((Ci.F("R{0}", i), ReloadAnswers[i].Pattern));
        }
        patterns.Add(("PROMPT", PromptRegex));
        patterns.Add(("QUESTION", QuestionRegex));
        var all = new StringBuilder();
        for (var n = 0; n < 4; n++)
        {
            var r = Wait(patterns, 60)!;
            all.Append(r.Text);
            if (r.Key == "QUESTION")
            {
                throw StopUnexpectedQuestion(all.ToString(), "reload");
            }
            if (r.Key == "PROMPT")
            {
                throw new SwitchException(t.T("The reload did not ask for confirmation and the switch is still at the prompt."));
            }
            var answer = ReloadAnswers[int.Parse(r.Key[1..], System.Globalization.CultureInfo.InvariantCulture)].Answer;
            SendLine(answer);
            if (answer.Length == 0)
            {
                return;
            }
        }
        throw new SwitchException(t.T("The reload did not reach the confirmation."));
    }

    /// <summary>Goes back to global configuration mode (exit from submodes, configure terminal from exec).</summary>
    public void RestoreConfigMode(string mode)
    {
        var m = mode;
        for (var n = 0; n < 5 && m != "config"; n++)
        {
            m = SendConfigLine(m == "exec" ? "configure terminal" : "exit").Mode;
        }
    }

    /// <summary>No backup line may take the switch out of configuration mode: if it happens, it stops.</summary>
    public void EnsureStillInConfig(ConfigAnswer answer, string line)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Mode == "exec")
        {
            throw new SwitchException(Ci.F(t.T("Line \"{0}\" took the switch out of configuration mode. The apply was stopped for safety: do not reload without checking and do not save."), masker.Protect(line)));
        }
    }

    /// <summary>Applies the blocks of a plan in configuration mode.</summary>
    /// <param name="status">Translated status text and percentage.</param>
    public ApplyResult ApplyPlan(IReadOnlyList<ConfigBlock> blocks, int lines, Action<string, int>? status = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var errors = new List<ApplyIssue>();
        var warnings = new List<ApplyIssue>();
        var total = Math.Max(lines, 1);
        var done = 0;
        var r = SendConfigLine("configure terminal");
        if (r.Mode != "config")
        {
            throw new SwitchException(t.T("Could not enter configuration mode."));
        }
        foreach (var b in blocks)
        {
            status?.Invoke(Ci.F(t.T("Applying configuration: {0} of {1} lines"), done, total), 100 * done / total);
            if (b.IsBanner)
            {
                Read();
                SendLine(Ci.F("banner {0} {1}", b.BannerType, b.Delimiter));
                Sleep(400);
                Read();
                foreach (var x in b.BannerText)
                {
                    SendLine(x);
                    Sleep(80);
                    Read();
                }
                r = SendConfigLine(b.Delimiter);
                EnsureStillInConfig(r, "banner " + b.BannerType);
                errors.AddRange(r.Errors.Select(e => new ApplyIssue("banner " + b.BannerType, e)));
                done += 1 + b.BannerText.Count;
                RestoreConfigMode(r.Mode);
                continue;
            }
            if (Ci.IsMatch(b.Header, "^crypto pki certificate chain "))
            {
                // Hex lines return no prompt: they are sent back to back and the wait happens only after "quit"
                r = SendConfigLine(b.Header);
                done++;
                foreach (var h in b.Children)
                {
                    var x = h.Trim();
                    done++;
                    if (Ci.Eq(x, "quit"))
                    {
                        r = SendConfigLine("quit");
                        EnsureStillInConfig(r, b.Header);
                        errors.AddRange(r.Errors.Select(e => new ApplyIssue(b.Header, e)));
                    }
                    else
                    {
                        SendLine(x);
                        Sleep(80);
                        Read();
                    }
                }
                RestoreConfigMode(r.Mode);
                continue;
            }
            r = SendConfigLine(b.Header, 120);
            done++;
            EnsureStillInConfig(r, b.Header);
            warnings.AddRange(r.Messages.Where(m => !r.Errors.Contains(m, StringComparer.OrdinalIgnoreCase)).Select(m => new ApplyIssue(b.Header, m)));
            if (r.Errors.Count > 0)
            {
                errors.Add(new ApplyIssue(b.Header, string.Join(" / ", r.Errors)));
                errors.AddRange(b.Children.Select(h => new ApplyIssue("  " + h.Trim(), t.T("Not applied: the header line failed"))));
                done += b.Children.Count;
                RestoreConfigMode(r.Mode);
                continue;
            }
            foreach (var h in b.Children)
            {
                var label = Ci.F("[{0}] {1}", b.Header, h.Trim());
                var rh = SendConfigLine(h.Trim(), 120);
                done++;
                EnsureStillInConfig(rh, label);
                warnings.AddRange(rh.Messages.Where(m => !rh.Errors.Contains(m, StringComparer.OrdinalIgnoreCase)).Select(m => new ApplyIssue(label, m)));
                if (rh.Errors.Count > 0)
                {
                    errors.Add(new ApplyIssue(label, string.Join(" / ", rh.Errors)));
                }
                r = rh;
            }
            RestoreConfigMode(r.Mode);
        }
        status?.Invoke(Ci.F(t.T("Apply finished: {0} lines"), total), 100);
        SendConfigLine("end");
        return new ApplyResult(errors, warnings);
    }
}
