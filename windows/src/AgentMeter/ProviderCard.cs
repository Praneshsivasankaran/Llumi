using System.Drawing.Drawing2D;
using AgentMeter.Core;

namespace AgentMeter;

internal sealed class ProviderCard : Panel
{
    private readonly Label title = new();
    private readonly Label status = new();
    private readonly Label summary = new();
    private readonly Label primary = new();
    private readonly UsageBar primaryBar = new();
    private readonly Font primaryFont = new("Segoe UI", 20, FontStyle.Bold);
    private string providerName = "Codex";
    private readonly LinkLabel setup = new() { Text = "Set up →", LinkColor = Palette.Foreground,
        ActiveLinkColor = Palette.Codex, VisitedLinkColor = Palette.Foreground, BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false, Visible = false, TabStop = true };
    private Uri? setupDestination;
    private readonly List<WindowRow> rows = [];
    private readonly ToolTip hints;
    private string[] ids = [];
    private readonly Font titleFont = new("Segoe UI", 10.5f, FontStyle.Bold);
    private readonly Font numberFont = new("Segoe UI", 11.5f, FontStyle.Bold);
    private readonly Font bodyFont = new("Segoe UI", 10);
    private readonly Font smallFont = new("Segoe UI", 8.5f);
    private Color accent = Palette.Codex;
    private Color? statusDot;
    private float currentScale = 1;
    internal int LogicalHeight { get; private set; } = 66;

    public ProviderCard(ToolTip hints)
    {
        this.hints = hints;
        BackColor = Palette.Background;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        foreach (var label in new[] { title, status, summary })
        {
            label.ForeColor = Palette.Muted; label.BackColor = Color.Transparent;
            label.UseMnemonic = false; label.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(label);
        }
        title.Font = titleFont; title.ForeColor = Palette.Foreground;
        status.Font = smallFont; status.TextAlign = ContentAlignment.MiddleRight;
        summary.Font = smallFont;
        setup.Font = smallFont;
        primary.Font = primaryFont; primary.ForeColor = Palette.Foreground;
        primary.BackColor = Color.Transparent;
        Controls.AddRange([primary, primaryBar]);
        Controls.Add(setup);
        setup.LinkClicked += (_, _) =>
        {
            if (setupDestination is { } destination && !ProviderSetup.Open(destination))
            {
                summary.Text = "Couldn't open setup instructions";
                hints.SetToolTip(setup, destination.AbsoluteUri);
            }
        };
    }

    public void Render(ProviderState state, DateTimeOffset now, float scale)
    {
        currentScale = scale;
        providerName = state.Name;
        BackColor = Palette.Background; title.ForeColor = Palette.Foreground; summary.ForeColor = Palette.Muted;
        accent = Palette.ProviderAccent(state.Name);
        title.Text = state.Name;
        status.Text = PopupText.Status(state, now);
        var stale = state.IsStale(now);
        statusDot = status.Text == "Live" ? Palette.Live : status.Text == "Refreshing…" ? accent : stale ? Palette.Warning : null;
        status.ForeColor = statusDot ?? Palette.Muted;
        summary.Text = state.Status == ProviderStatus.Ready && !stale ? "" : PopupText.Summary(state, now);
        setupDestination = ProviderSetup.For(state);
        setup.Text = setupDestination is null ? "" : "Set up →";
        setup.Visible = setupDestination is not null;
        setup.AccessibleName = $"Set up {state.Name} using official instructions";
        hints.SetToolTip(setup, setupDestination?.AbsoluteUri);
        if (setupDestination is not null && state.Name.StartsWith("Claude", StringComparison.Ordinal) && state.Failure == FailureKind.NotInstalled)
            summary.Text = "Install Claude Code";
        var detail = state.Detail ?? FailureText.For(state.Failure);
        hints.SetToolTip(status, detail);
        hints.SetToolTip(summary, state.Snapshot is { } source ? $"{source.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n{source.Source}" : detail);
        var selected = MonitorSelection.Select(state);
        primary.Text = selected is null ? "" : PopupText.Remaining(selected) + " remaining";
        primary.Visible = selected is not null; primaryBar.Visible = selected is not null;
        primary.ForeColor = stale ? Palette.Warning : Palette.Foreground;
        primaryBar.UpdateValue(selected?.RemainingPercent, stale);
        var windows = UsagePresentation.Windows(state)
            .OrderBy(w => w.Id == selected?.Id ? 0 : 1).ToArray();
        if (state.Status == ProviderStatus.Ready && !stale && state.Snapshot is not null &&
            windows.Length == 0 && UsagePresentation.IsClaude(state.Name))
            summary.Text = "Verified allowance is temporarily unavailable.";
        if (!ids.SequenceEqual(windows.Select(w => w.Id)))
        {
            foreach (var row in rows)
                foreach (var control in row.Controls) { hints.SetToolTip(control, null); Controls.Remove(control); control.Dispose(); }
            rows.Clear(); ids = windows.Select(w => w.Id).ToArray();
            foreach (var window in windows)
            {
                var row = new WindowRow(numberFont, bodyFont, smallFont);
                rows.Add(row); Controls.AddRange(row.Controls);
            }
        }
        for (var i = 0; i < windows.Length; i++)
        {
            var row = rows[i]; var window = windows[i];
            row.Name.Text = PopupText.WindowName(state.Name, window);
            row.Name.ForeColor = Palette.Foreground; row.Reset.ForeColor = Palette.Muted;
            row.Value.Text = window.Id == selected?.Id ? "" : PopupText.Remaining(window);
            row.Value.ForeColor = stale ? Palette.Warning : Palette.Foreground;
            row.Reset.Text = PopupText.Reset(window, now);
            hints.SetToolTip(row.Name, UsagePresentation.IsClaude(state.Name) ? PopupText.WindowName(state.Name, window) : window.Name);
            hints.SetToolTip(row.Reset, UsageText.Reset(window.ResetsAt, now));
            row.Bar.Accent = accent;
            row.Bar.UpdateValue(window.RemainingPercent, stale);
        }
        LogicalHeight = windows.Length == 0 ? 82 : 114 + windows.Length * 50;
        LayoutRows(scale);
        Invalidate();
    }

    private void LayoutRows(float scale)
    {
        int S(int n) => (int)Math.Round(n * scale);
        var innerWidth = Width - S(28);
        title.SetBounds(S(44), S(9), Math.Max(S(110), Width - S(136)), S(23));
        status.SetBounds(Width - S(94), S(10), S(80), S(22));
        summary.SetBounds(S(14), S(31), innerWidth - (setupDestination is null ? 0 : S(74)), S(19));
        setup.SetBounds(Width - S(84), S(31), S(70), S(19));
        primary.SetBounds(S(14), S(56), innerWidth, S(38));
        primaryBar.SetBounds(S(14), S(100), innerWidth, S(5));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var top = 114 + i * 50;
            row.Name.SetBounds(S(14), S(top), innerWidth - S(69), S(23));
            row.Value.SetBounds(Width - S(83), S(top - 1), S(69), S(24));
            row.Bar.SetBounds(0, 0, 0, 0);
            row.Reset.SetBounds(S(14), S(top + 24), innerWidth, S(20));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Transparent labels ask their parent to paint into a translated, clipped DC.
        // Graphics.Clear ignores that clip and erases adjacent labels during refresh.
        using var background = new SolidBrush(Palette.Background);
        e.Graphics.FillRectangle(background, e.ClipRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ProviderMark.Draw(e.Graphics, providerName, new RectangleF(14 * currentScale, 11 * currentScale, 21 * currentScale, 21 * currentScale), Palette.Foreground);
        var s = currentScale;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Palette.Border);
        e.Graphics.DrawLine(border, 14 * s, Height - 1, Width - 14 * s, Height - 1);
        if (statusDot is { } dotColor)
        {
            var textWidth = TextRenderer.MeasureText(e.Graphics, status.Text, status.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
            using var dot = new SolidBrush(dotColor);
            e.Graphics.FillEllipse(dot, status.Right - textWidth - 10 * s, 18 * s, 6 * s, 6 * s);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { primaryFont.Dispose(); titleFont.Dispose(); numberFont.Dispose(); bodyFont.Dispose(); smallFont.Dispose(); }
    }

    private sealed class WindowRow
    {
        public readonly Label Name = new() { ForeColor = Palette.Foreground, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        public readonly Label Value = new() { TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent, ForeColor = Palette.Foreground };
        public readonly Label Reset = new() { ForeColor = Palette.Muted, BackColor = Color.Transparent, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        public readonly UsageBar Bar = new();
        public Control[] Controls => [Name, Value, Reset, Bar];
        public WindowRow(Font number, Font body, Font small) { Value.Font = number; Name.Font = body; Reset.Font = small; }
    }
}
