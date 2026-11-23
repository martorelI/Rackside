using Rackside.Core;
using Rackside.Operations;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Ui;

/// <summary>Messages, questions and the strong warning (the operator must click "Continue" on purpose).</summary>
internal static class Dialogs
{
    public static void Info(IWin32Window? owner, string text, string? title = null) =>
        MessageBox.Show(owner, text, title ?? AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Error(IWin32Window? owner, string text, string? title = null) =>
        MessageBox.Show(owner, text, title ?? AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static bool Ask(IWin32Window? owner, string text, string? title = null) =>
        MessageBox.Show(owner, text, title ?? AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>Strong warning: red band, the text in a scrollable box and "Cancel" as the default button.</summary>
    public static bool Warn(IWin32Window? owner, string text, string title)
    {
        using var f = new Form
        {
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(620, 420),
            BackColor = Theme.Surface,
            Font = Theme.Body,
        };
        var band = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Danger };
        var head = new Label { Text = "⚠  " + title, Dock = DockStyle.Fill, ForeColor = Color.White, Font = Theme.Section, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0) };
        band.Controls.Add(head);
        var box = new TextBox { Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, Dock = DockStyle.Fill, Font = Theme.Body };
        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 8) };
        middle.Controls.Add(box);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = Theme.Background };
        var cancel = Make.Secondary(S("Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        var ok = Make.Danger(S("Continue"));
        ok.DialogResult = DialogResult.OK;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        f.Controls.Add(middle);
        f.Controls.Add(buttons);
        f.Controls.Add(band);
        f.AcceptButton = cancel;
        f.CancelButton = cancel;
        f.Shown += (_, _) => cancel.Focus();
        return f.ShowDialog(owner) == DialogResult.OK;
    }
}

/// <summary>IOperatorUi for the operations, which run on a background thread: dialogs go to the window thread.</summary>
internal sealed class OperatorBridge(Control owner, Action<string, int?> status) : IOperatorUi
{
    private T OnUi<T>(Func<T> f) => owner.InvokeRequired ? (T)owner.Invoke(f)! : f();

    public bool Warn(string text, string title) => OnUi(() => Dialogs.Warn(owner.FindForm(), text, title));

    public bool Ask(string text, string title) => OnUi(() => Dialogs.Ask(owner.FindForm(), text, title));

    public void Status(string text, int? progress = null)
    {
        if (owner.IsDisposed)
        {
            return;
        }
        if (owner.InvokeRequired)
        {
            owner.BeginInvoke(() => status(text, progress));
        }
        else
        {
            status(text, progress);
        }
    }
}

/// <summary>Username, password and enable for one operation (not saved).</summary>
internal static class CredentialsDialog
{
    public static Credentials? Ask(IWin32Window owner, string title, string detail, bool required, string user = "")
    {
        using var f = new Form
        {
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(480, 300),
            BackColor = Theme.Surface,
            Font = Theme.Body,
            Padding = new Padding(20),
        };
        var fields = new Fields();
        fields.Full(Make.Note(detail));
        var u = fields.Add(S("User"), Make.Input(user));
        var p = fields.Add(S("Password"), Make.Input(password: true));
        var e = fields.Add(S("Enable (optional)"), Make.Input(password: true));
        var ok = Make.Primary(S("Continue"));
        var cancel = Make.Secondary(S("Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) =>
        {
            if (required && (u.Text.Trim().Length == 0 || p.Text.Length == 0))
            {
                Dialogs.Error(f, S("Enter the username and the password."));
                return;
            }
            f.DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        f.Controls.Add(fields);
        f.Controls.Add(buttons);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? new Credentials(u.Text.Trim(), p.Text, e.Text) : null;
    }
}
