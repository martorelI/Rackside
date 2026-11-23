using System.ComponentModel;
using System.Diagnostics;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Ui;

/// <summary>A section of the app: header (title, description, actions) and body.</summary>
internal abstract class Page : UserControl
{
    private readonly FlowLayoutPanel actions = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        FlowDirection = FlowDirection.RightToLeft,
        BackColor = Theme.Surface,
        Padding = new Padding(24, 12, 16, 12),
    };
    private readonly Panel body = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };

    protected Page(MainForm shell, string title, string description)
    {
        Shell = shell;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        Font = Theme.Body;
        var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Theme.Surface };
        var titles = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 0, 0) };
        var desc = Make.Label(description, Theme.Body, Theme.Muted);
        desc.Dock = DockStyle.Top;
        desc.AutoEllipsis = true;
        desc.AutoSize = false;
        desc.Height = 24;
        var t = Make.Label(title, Theme.Title);
        t.Dock = DockStyle.Top;
        titles.Controls.Add(desc);
        titles.Controls.Add(t);
        header.Controls.Add(titles);
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };
        var footerLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };
        Controls.Add(body);
        Controls.Add(footerLine);
        Controls.Add(actions);
        Controls.Add(line);
        Controls.Add(header);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    protected MainForm Shell { get; }

    protected AppState App => Shell.App;

    /// <summary>The page content (fills the area under the header).</summary>
    protected void SetBody(Control c)
    {
        c.Dock = DockStyle.Fill;
        body.Controls.Add(c);
    }

    /// <summary>Adds a button to the footer bar; they show left to right in the order added, aligned to the right.</summary>
    protected Button Action(Button b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(b);
        actions.Controls.SetChildIndex(b, 0);
        return b;
    }

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void Activated()
    {
    }

    /// <summary>Enables or disables the buttons while an operation runs.</summary>
    public virtual void SetBusy(bool busy)
    {
        foreach (Control c in actions.Controls)
        {
            c.Enabled = !busy;
        }
        UpdateButtons();
    }

    /// <summary>Enables each action according to the page state.</summary>
    protected virtual void UpdateButtons()
    {
    }

    protected static void OpenPath(string path)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            Dialogs.Error(null, S("No record has been generated yet."));
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
