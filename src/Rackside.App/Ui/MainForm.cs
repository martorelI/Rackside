using System.ComponentModel;
using Rackside.App.Pages;
using Rackside.Core;
using Rackside.Operations;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Ui;

/// <summary>The shell: navigation on the left, the page in the middle, status and Stop at the bottom.</summary>
internal sealed class MainForm : Form
{
    private readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly FlowLayoutPanel nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Padding = new Padding(0, 8, 0, 0) };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true, Padding = new Padding(16, 0, 0, 0) };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Right, Width = 220, Style = ProgressBarStyle.Continuous, Visible = false, Margin = new Padding(0, 12, 12, 12) };
    private readonly Button stop;
    private readonly List<(Button Button, Page Page)> pages = [];
    private CancellationTokenSource? cts;
    private Page? current;

    public MainForm(AppState app)
    {
        // Sizes in this code are for 96 DPI; WinForms scales them (and the fonts) to the screen.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        SuspendLayout();
        App = app;
        Text = AppInfo.Name;
        Font = Theme.Body;
        BackColor = Theme.Background;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1280, 820);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (ArgumentException)
        {
        }

        stop = Make.Danger(S("Stop"), (_, _) => cts?.Cancel());
        stop.Dock = DockStyle.Right;
        stop.Visible = false;

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Theme.Sidebar };
        var brand = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Sidebar, Padding = new Padding(18, 20, 12, 12) };
        var logo = new PictureBox { Image = Theme.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Dock = DockStyle.Left };
        var name = new Label { Text = AppInfo.Name, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
        brand.Controls.Add(name);
        brand.Controls.Add(logo);
        var version = new Label { Text = F("Version {0}", AppInfo.Version), Dock = DockStyle.Bottom, Height = 34, ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(20, 0, 0, 0) };
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(version);
        sidebar.Controls.Add(brand);

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Surface, Padding = new Padding(0, 6, 12, 6) };
        bar.Controls.Add(status);
        bar.Controls.Add(progress);
        bar.Controls.Add(stop);
        var barLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };

        Controls.Add(host);
        Controls.Add(barLine);
        Controls.Add(bar);
        Controls.Add(sidebar);

        AddPage("↻", S("Restore from backup"), new RestorePage(this));
        AddPage("⭯", S("Factory reset"), new ResetPage(this));
        AddPage("⬆", S("Update IOS"), new UpgradePage(this));
        AddPage("⚙", S("Configure switches"), new ConfigurePage(this));
        AddPage("☰", S("Settings"), new SettingsPage(this));
        AddPage("ⓘ", S("About"), new AboutPage(this));
        Show(0);
        SetStatus(S("Ready."), null);
        ResumeLayout(true);
        FormClosing += (_, e) =>
        {
            if (Busy && !Dialogs.Ask(this, S("An operation is running. Close anyway? The switch may be left halfway.")))
            {
                e.Cancel = true;
            }
        };
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AppState App { get; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy { get; private set; }

    private void AddPage(string glyph, string title, Page page)
    {
        var index = pages.Count;
        var b = new Button
        {
            Text = "   " + glyph + "   " + title,
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.SidebarText,
            BackColor = Theme.Sidebar,
            Font = Theme.Nav,
            Width = 236,
            Height = 46,
            Margin = new Padding(0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.SidebarHover;
        b.Click += (_, _) => Show(index);
        b.Paint += (_, e) =>
        {
            if (current == page)
            {
                using var brush = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(brush, 0, 8, 4, b.Height - 16);
            }
        };
        nav.Controls.Add(b);
        page.Visible = false;
        host.Controls.Add(page);
        pages.Add((b, page));
    }

    private void Show(int index)
    {
        if (Busy)
        {
            return;
        }
        for (var i = 0; i < pages.Count; i++)
        {
            var on = i == index;
            pages[i].Page.Visible = on;
            pages[i].Button.BackColor = on ? Theme.SidebarHover : Theme.Sidebar;
            pages[i].Button.ForeColor = on ? Color.White : Theme.SidebarText;
            if (on)
            {
                current = pages[i].Page;
            }
            pages[i].Button.Invalidate();
        }
        current?.Activated();
    }

    public void SetStatus(string text, int? percent)
    {
        status.Text = text;
        if (percent is { } p)
        {
            progress.Value = Math.Clamp(p, 0, 100);
        }
    }

    /// <summary>The bridge an operation uses to ask the operator and update the status bar.</summary>
    public OperatorBridge Operator() => new(this, SetStatus);

    /// <summary>
    /// Runs an operation on a background thread with the Stop button active. Errors are shown in a dialog
    /// (the operations already translate and mask their messages). Returns default if it failed or stopped.
    /// </summary>
    public async Task<T?> RunAsync<T>(Func<CancellationToken, T> work)
    {
        if (Busy)
        {
            return default;
        }
        Busy = true;
        cts = new CancellationTokenSource();
        foreach (var (b, _) in pages)
        {
            b.Enabled = false;
        }
        current?.SetBusy(true);
        stop.Visible = true;
        progress.Visible = true;
        progress.Value = 0;
        UseWaitCursor = true;
        try
        {
            var token = cts.Token;
            return await Task.Run(() => work(token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(App.T.T("Operation stopped by the operator."), null);
            return default;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(ex.Message.Split('\n')[0], null);
            Dialogs.Error(this, ex.Message);
            return default;
        }
        finally
        {
            UseWaitCursor = false;
            stop.Visible = false;
            progress.Visible = false;
            foreach (var (b, _) in pages)
            {
                b.Enabled = true;
            }
            Busy = false;
            current?.SetBusy(false);
            cts.Dispose();
            cts = null;
        }
    }

    public Task RunAsync(Action<CancellationToken> work) => RunAsync<bool>(c =>
    {
        work(c);
        return true;
    });
}
