using System.ComponentModel;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Ui;

/// <summary>Factory of the styled controls every page uses.</summary>
internal static class Make
{
    public static Button Primary(string text, EventHandler? click = null) => Styled(text, Theme.Accent, Color.White, Theme.Accent, click);

    public static Button Secondary(string text, EventHandler? click = null) => Styled(text, Theme.Surface, Theme.TextColor, Theme.Border, click);

    public static Button Danger(string text, EventHandler? click = null) => Styled(text, Theme.Danger, Color.White, Theme.Danger, click);

    private static Button Styled(string text, Color back, Color fore, Color border, EventHandler? click)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = Theme.BodyBold,
            Padding = new Padding(12, 5, 12, 5),
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = back == Theme.Surface ? Theme.AccentSoft : ControlPaint.Dark(back, 0.05f);
        // A flat button keeps its colors when disabled: switch to a gray look so the state is obvious.
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? back : Theme.Disabled;
            b.ForeColor = b.Enabled ? fore : Theme.DisabledText;
            b.FlatAppearance.BorderColor = b.Enabled ? border : Theme.Border;
            b.Cursor = b.Enabled ? Cursors.Hand : Cursors.Default;
        };
        if (click is not null)
        {
            b.Click += click;
        }
        return b;
    }

    public static TextBox Input(string text = "", bool password = false, int width = 260) => new()
    {
        Text = text,
        UseSystemPasswordChar = password,
        Width = width,
        Font = Theme.Body,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 2, 0, 6),
    };

    public static TextBox Area(string text = "", int height = 90, bool readOnly = false, bool mono = false) => new()
    {
        Text = text,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        ReadOnly = readOnly,
        WordWrap = !mono,
        Height = height,
        Dock = DockStyle.Fill,
        Font = mono ? Theme.Mono : Theme.Body,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = readOnly ? Theme.Background : Theme.Surface,
        Margin = new Padding(0, 2, 0, 6),
    };

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, int width = 260, bool editable = false)
    {
        var c = new ComboBox { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Width = width, Font = Theme.Body, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 0, 6) };
        c.Items.AddRange([.. items]);
        if (c.Items.Count > selected && selected >= 0)
        {
            c.SelectedIndex = selected;
        }
        return c;
    }

    public static CheckBox Check(string text, bool value = false) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Font = Theme.Body,
        Margin = new Padding(0, 3, 16, 3),
    };

    public static RadioButton Radio(string text, bool value = false) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Font = Theme.Body,
        Margin = new Padding(0, 3, 16, 3),
    };

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Theme.Body,
        ForeColor = color ?? Theme.TextColor,
        Margin = new Padding(0, 6, 12, 0),
    };

    /// <summary>A note in gray that wraps to the width of its container.</summary>
    public static Label Note(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        Font = Theme.Small,
        ForeColor = color ?? Theme.Muted,
        Margin = new Padding(0, 2, 0, 8),
    };

    /// <summary>Horizontal row of controls.</summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0), FlowDirection = FlowDirection.LeftToRight };
        f.Controls.AddRange(controls);
        return f;
    }
}

/// <summary>Label + control grid in two columns.</summary>
internal sealed class Fields : TableLayoutPanel
{
    public Fields()
    {
        ColumnCount = 2;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Margin = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public T Add<T>(string label, T control) where T : Control
    {
        Controls.Add(Make.Label(label, color: Theme.Muted), 0, RowCount);
        Controls.Add(control, 1, RowCount);
        RowCount++;
        return control;
    }

    public T Full<T>(T control) where T : Control
    {
        Controls.Add(control, 0, RowCount);
        SetColumnSpan(control, 2);
        RowCount++;
        return control;
    }
}

/// <summary>
/// A white panel with a title and a thin border. The body is a one-column table: wide controls (field grids,
/// multi-line boxes) are anchored left and right so they follow the card width without resizing loops.
/// </summary>
internal class Card : Panel
{
    public Card(string title, string? subtitle = null)
    {
        BackColor = Theme.Surface;
        Padding = new Padding(18, 14, 18, 14);
        Margin = new Padding(0, 0, 0, 14);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows, Margin = new Padding(0) };
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(Body);
        if (subtitle is not null)
        {
            var sub = Make.Note(subtitle);
            sub.Dock = DockStyle.Top;
            Controls.Add(sub);
        }
        var head = Make.Label(title, Theme.Section);
        head.Dock = DockStyle.Top;
        head.Margin = new Padding(0, 0, 0, 6);
        Controls.Add(head);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TableLayoutPanel Body { get; }

    public T Add<T>(T control) where T : Control
    {
        if (control is Fields or TextBox { Multiline: true } or LogView)
        {
            control.Dock = DockStyle.None;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        }
        Body.Controls.Add(control, 0, Body.RowCount);
        Body.RowCount++;
        return control;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>A scrollable column of cards.</summary>
internal sealed class CardList : Panel
{
    public CardList()
    {
        AutoScroll = true;
        Dock = DockStyle.Fill;
        Padding = new Padding(24, 16, 24, 16);
        BackColor = Theme.Background;
    }

    /// <summary>Adds at the bottom (Dock = Top stacks in reverse order, so it is moved to the front).</summary>
    public T Add<T>(T card) where T : Control
    {
        card.Dock = DockStyle.Top;
        Controls.Add(card);
        card.BringToFront();
        return card;
    }
}

/// <summary>Segmented tabs: a row of flat buttons that switch the visible panel (a lighter look than TabControl).</summary>
internal sealed class Tabs : Panel
{
    private readonly FlowLayoutPanel strip = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(24, 8, 24, 0), BackColor = Theme.Background, WrapContents = false };
    private readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly List<(Button Button, Control Page)> pages = [];

    public Tabs()
    {
        Dock = DockStyle.Fill;
        Controls.Add(host);
        Controls.Add(strip);
    }

    public event EventHandler? Changed;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected { get; private set; } = -1;

    public T Add<T>(string title, T page) where T : Control
    {
        var index = pages.Count;
        var b = new Button
        {
            Text = title,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => Select(index);
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        host.Controls.Add(page);
        strip.Controls.Add(b);
        pages.Add((b, page));
        if (Selected < 0)
        {
            Select(0);
        }
        return page;
    }

    public void Select(int index)
    {
        Selected = index;
        for (var i = 0; i < pages.Count; i++)
        {
            var on = i == index;
            pages[i].Page.Visible = on;
            pages[i].Button.BackColor = on ? Theme.Surface : Theme.Background;
            pages[i].Button.ForeColor = on ? Theme.Accent : Theme.Muted;
            pages[i].Button.Font = on ? Theme.BodyBold : Theme.Body;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Live log of the operation, colored by level.</summary>
internal sealed class LogView : RichTextBox
{
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;

    public LogView()
    {
        ReadOnly = true;
        Dock = DockStyle.Fill;
        Font = Theme.Mono;
        BorderStyle = BorderStyle.None;
        BackColor = Color.FromArgb(0x16, 0x1B, 0x22);
        ForeColor = Color.FromArgb(0xD6, 0xDE, 0xE8);
        WordWrap = false;
        DetectUrls = false;
    }

    /// <summary>Safe from any thread (the operations log from a background thread, even while this tab was never shown).</summary>
    public void Append(string line)
    {
        if (ui is not null && SynchronizationContext.Current != ui)
        {
            ui.Post(_ => Append(line), null);
            return;
        }
        if (IsDisposed)
        {
            return;
        }
        SelectionStart = TextLength;
        SelectionColor = line.Contains("[ERROR]", StringComparison.Ordinal) ? Color.FromArgb(0xFF, 0x7B, 0x86)
            : line.Contains("[WARN]", StringComparison.Ordinal) ? Color.FromArgb(0xF5, 0xC2, 0x6B)
            : line.Contains("[OK]", StringComparison.Ordinal) ? Color.FromArgb(0x7E, 0xD6, 0x8E)
            : ForeColor;
        AppendText(line + Environment.NewLine);
        ScrollToCaret();
    }
}

/// <summary>A path with a "Browse" button (file or folder).</summary>
internal sealed class PathField : FlowLayoutPanel
{
    public PathField(string value, bool folder, string? filter = null, Func<string>? initial = null)
    {
        AutoSize = true;
        WrapContents = false;
        Margin = new Padding(0);
        Box = Make.Input(value, width: 420);
        var browse = Make.Secondary(S("Browse..."));
        browse.Margin = new Padding(6, 0, 0, 4);
        browse.Click += (_, _) =>
        {
            if (folder)
            {
                using var d = new FolderBrowserDialog { SelectedPath = Box.Text, UseDescriptionForTitle = true, Description = S("Choose the folder") };
                if (d.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    Box.Text = d.SelectedPath;
                }
            }
            else
            {
                using var d = new OpenFileDialog { Filter = filter ?? S("All files (*.*)|*.*"), InitialDirectory = initial?.Invoke() ?? string.Empty };
                if (d.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    Box.Text = d.FileName;
                }
            }
        };
        Controls.Add(Box);
        Controls.Add(browse);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TextBox Box { get; }

    public string Value => Box.Text.Trim();
}
