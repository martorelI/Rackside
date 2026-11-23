using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Transport.Console;

namespace Rackside.Transport.Ssh;

/// <summary>The key the device presents (read without authenticating).</summary>
public sealed record PresentedKey(string Algorithm, string Fingerprint)
{
    public NormalFingerprint Normal { get; } = NormalFingerprint.Parse(Fingerprint);

    public string Text => Algorithm + " " + Normal;
}

/// <summary>The only key accepted for this device during an operation (every connection is pinned to it).</summary>
public sealed record SshTrust(string Device, string IP, int Port, string Algorithm, string Fingerprint, NormalFingerprint Normal, string How);

/// <summary>What the policy needs to decide.</summary>
public sealed record FingerprintRequest(string IP, int Port)
{
    /// <summary>Fingerprint typed by the operator (has priority over the saved one).</summary>
    public string? Expected { get; init; }

    /// <summary>Where <see cref="Expected"/> comes from, for the messages (default: the fingerprint you typed in tab 1).</summary>
    public string? ExpectedSource { get; init; }

    /// <summary>Sweeps only: accept without asking ONLY when there is no saved or expected fingerprint.</summary>
    public bool TrustFirstUse { get; init; }

    /// <summary>Already accepted in this same operation (reconnection after a reload, SCP).</summary>
    public SshTrust? Accepted { get; init; }

    public string Hostname { get; init; } = string.Empty;

    public string Serial { get; init; } = string.Empty;

    public string Device => Ci.F("{0}:{1}", IP, Port);
}

/// <summary>
/// Security rule 2: first connection = explicit question (unless the operator checked
/// "accept new" in a sweep); a fingerprint different from the saved one = always a strong warning; nothing is
/// replaced without confirmation. Returns the trusted key or throws <see cref="SwitchException"/>.
/// </summary>
public static class FingerprintPolicy
{
    /// <param name="warn">Shows a warning (text, title) and returns true only if the operator accepts.</param>
    /// <param name="log">(level, translated message): OK / WARN.</param>
    public static SshTrust Confirm(FingerprintRequest request, PresentedKey presented, FingerprintStore store, Translator t,
        Func<string, string, bool> warn, Action<string, string>? log = null, string? operatorName = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(warn);
        var user = operatorName ?? Environment.UserName;
        var device = request.Device;
        var txtPres = presented.Text;
        var c = request.Accepted;
        if (c is not null && Ci.Eq(c.Device, device) && string.Equals(c.Normal.Value, presented.Normal.Value, StringComparison.Ordinal))
        {
            return c;
        }

        var refText = string.Empty;
        NormalFingerprint? reference = null;
        if (!string.IsNullOrEmpty(request.Expected))
        {
            reference = NormalFingerprint.Parse(request.Expected);
            refText = string.IsNullOrEmpty(request.ExpectedSource) ? t.T("the fingerprint you typed in tab 1") : request.ExpectedSource;
        }
        else if (store.Find(device) is { } saved)
        {
            reference = NormalFingerprint.Parse(saved.Fingerprint);
            refText = Ci.F(t.T("the one saved on {0} ({1}{2})"), saved.Date, saved.Hostname, saved.Serial.Length > 0 ? t.T(", serial ") + saved.Serial : string.Empty);
        }

        string how;
        const string nl = "\r\n";
        if (reference is null && request.TrustFirstUse)
        {
            how = t.T("First use, accepted without asking (sweep option checked by the operator)");
            log?.Invoke("WARN", Ci.F(t.T("SSH fingerprint of {0} accepted on first use without asking (sweep option): {1}"), device, txtPres));
        }
        else if (reference is null)
        {
            var txt = Ci.F(t.T("First SSH connection to {0}. There is no saved fingerprint to compare with.{1}{1}The device presents:{1}{2}{1}{1}Verify it by other means before accepting (for example, the one PuTTY showed you when connecting from a trusted location, or the record of a previous access). If someone controls the network between your computer and the switch, they could be impersonating it.{1}{1}Trust this fingerprint and save it for future connections?"), device, nl, txtPres);
            if (!warn(txt, t.T("Unverified SSH fingerprint")))
            {
                throw new FingerprintRejectedException(Ci.F(t.T("SSH connection cancelled: the fingerprint of {0} was not accepted."), device));
            }
            how = t.T("First use, accepted by the operator");
            log?.Invoke("WARN", Ci.F(t.T("SSH fingerprint of {0} accepted on first use: {1}"), device, txtPres));
        }
        else if (reference.SameAs(presented.Normal))
        {
            how = string.IsNullOrEmpty(request.Expected) ? t.T("Matches the saved one") : t.T("Matches the one entered by the operator");
            log?.Invoke("OK", Ci.F(t.T("SSH fingerprint of {0} verified ({1}): {2}"), device, refText, txtPres));
        }
        else
        {
            var detail = !Ci.Eq(reference.Type, presented.Normal.Type)
                ? Ci.F(t.T("The reference is in another format ({0}) and cannot be compared with the one presented ({1})."), reference.Type, presented.Normal.Type)
                : t.T("THEY DO NOT MATCH.");
            var txt = Ci.F(t.T("WARNING: the SSH fingerprint of {0} does not match {1}.{2}{2}Expected : {3}{2}Presented: {4}{2}{2}{5}{2}{2}It may be a man-in-the-middle attack (someone impersonating the switch to capture your password or hand it a tampered image). It also happens if the switch was replaced or its RSA keys were regenerated.{2}{2}If you do not know why it changed, answer NO and check it on the console.{2}{2}Accept the presented fingerprint (override) and continue?"),
                device, refText, nl, reference, txtPres, detail);
            if (!warn(txt, t.T("The SSH fingerprint does not match")))
            {
                throw new FingerprintRejectedException(Ci.F(t.T("SSH connection cancelled: the fingerprint of {0} does not match {1}."), device, refText));
            }
            how = Ci.F(t.T("Operator OVERRIDE (expected {0})"), reference);
            log?.Invoke("WARN", Ci.F(t.T("SSH fingerprint OVERRIDE for {0}: expected {1}, presented {2}. Accepted by {3}."), device, reference, txtPres, user));
        }
        store.Save(device, presented.Algorithm, presented.Fingerprint, request.Hostname, request.Serial, how, user: user);
        return new SshTrust(device, request.IP, request.Port, presented.Algorithm, presented.Fingerprint, presented.Normal, how);
    }
}
