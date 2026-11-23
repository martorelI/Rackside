using Rackside.App.Ui;
using Rackside.Core.Security;
using Rackside.Operations.Reset;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>Factory reset (security rule 7): review, type the exact hostname, erase and reload without saving.</summary>
internal sealed class ResetPage : Page
{
    private readonly ResetOperation op;
    private readonly ConnectionCard conn;
    private readonly PathField folder;
    private readonly TextBox ticket = Make.Input();
    private readonly TextBox responsible = Make.Input();
    private readonly TextBox reason = Make.Input(width: 420);
    private readonly CheckBox waitBoot = Make.Check(S("Wait for the \"Switch>\" prompt after the reload"), true);
    private readonly TextBox summary = Make.Area(height: 230, readOnly: true, mono: true);
    private readonly TextBox confirm = Make.Input(width: 240);
    private readonly Button review;
    private readonly Button reset;
    private readonly Button record;
    private readonly LogView log = new();
    private readonly Tabs tabs = new();
    private ResetReview? current;
    private string recordPath = string.Empty;

    public ResetPage(MainForm shell) : base(shell, S("Factory reset"), S("Backs up the configuration, keeps the IOS image, erases keys, vlan.dat and startup-config and reloads without saving."))
    {
        op = new ResetOperation(App.T, new SecretMasker(), shell.Operator());
        op.Log.Line += log.Append;
        conn = new ConnectionCard(App, allowSsh: false);
        folder = new PathField(App.Paths.FactoryResets, folder: true);

        var setup = new CardList();
        setup.Add(conn);
        var rec = setup.Add(new Card(S("Record"), S("Evidence folder, ticket and reason for the erase record.")));
        var f = new Fields();
        f.Add(S("Evidence folder"), folder);
        f.Add(S("Ticket / reference"), ticket);
        f.Add(S("Responsible"), responsible);
        f.Add(S("Reason"), reason);
        f.Full(waitBoot);
        rec.Add(f);

        var confirmList = new CardList();
        var sum = confirmList.Add(new Card(S("What will happen"), S("Read from the switch without changing anything. The backup is already in the evidence folder.")));
        sum.Add(summary);
        var danger = confirmList.Add(new Card(S("Confirm"), S("Type the exact hostname of the device (case-sensitive) to enable the button.")));
        danger.Add(confirm);
        confirm.TextChanged += (_, _) => UpdateButtons();

        tabs.Add(S("1. Connection"), setup);
        tabs.Add(S("2. Review and confirm"), confirmList);
        tabs.Add(S("Log"), log);
        SetBody(tabs);

        review = Action(Make.Secondary(S("Connect and review"), async (_, _) => await Review()));
        reset = Action(Make.Danger(S("Factory reset"), async (_, _) => await Reset()));
        record = Action(Make.Secondary(S("Open record"), (_, _) => OpenPath(recordPath)));
        UpdateButtons();
    }

    private ResetRun Run() => new()
    {
        PortCom = conn.PortCom, BaudRate = conn.BaudRate, ConsoleUser = conn.User, ConsolePassword = conn.Password, ConsoleEnable = conn.Enable,
        FolderRecord = folder.Value, Ticket = ticket.Text.Trim(), Responsible = responsible.Text.Trim(), Reason = reason.Text.Trim(), WaitBoot = waitBoot.Checked,
    };

    private async Task Review()
    {
        var run = Run();
        current = null;
        confirm.Text = string.Empty;
        summary.Text = string.Empty;
        tabs.Select(2);
        current = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Review(run);
        });
        if (current is not null)
        {
            summary.Text = current.Summary;
            tabs.Select(1);
            confirm.Focus();
        }
        UpdateButtons();
    }

    private async Task Reset()
    {
        if (current is null)
        {
            return;
        }
        var typed = confirm.Text;
        tabs.Select(2);
        var r = await Shell.RunAsync(c =>
        {
            op.Cancellation = c;
            return op.Reset(current, typed);
        });
        if (r is not null)
        {
            recordPath = r.RecordPath;
            var msg = S("Result: ") + App.T.T(r.Outcome.Code()) + (r.ErrorFatal.Length > 0 ? "\r\n\r\n" + r.ErrorFatal : string.Empty) + App.T.T("\r\n\r\nEvidence and record in:\r\n") + current.Folder;
            if (r.Outcome == ResetOutcome.Successful) { Dialogs.Info(FindForm(), msg, S("Factory reset")); }
            else { Dialogs.Error(FindForm(), msg, S("Factory reset")); }
        }
        UpdateButtons();
    }

    protected override void UpdateButtons()
    {
        if (reset is null)
        {
            return;
        }
        var busy = Shell.Busy;
        review.Enabled = !busy;
        reset.Enabled = !busy && ResetOperation.CanReset(current, confirm.Text);
        record.Enabled = !busy && recordPath.Length > 0;
    }
}
