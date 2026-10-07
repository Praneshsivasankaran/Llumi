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
    private readonly Font primaryFont = new("Segoe UI Semibold", 20, FontStyle.Regular);
    private string providerName = "Codex";
    private readonly LinkLabel setup = new() { Text = "Set up →", LinkColor = Palette.Foreground,
        ActiveLinkColor = Palette.Codex, VisitedLinkColor = Palette.Foreground, BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false, Visible = false, TabStop = true };
    private Uri? setupDestination;
    private readonly List<WindowRow> rows = [];
    private readonly ToolTip hints;
    private string[] ids = [];
    private readonly Font titleFont = new("Segoe UI Semibold", 11, FontStyle.Regular);
    private readonly Font numberFont = new("Segoe UI Semibold", 10.5f, FontStyle.Regular);
    private readonly Font bodyFont = new("Segoe UI", 10);
    private readonly Font smallFont = new("Segoe UI", 9);
    private Color accent = Palette.Codex;
    private Color? statusDot;
    private float currentScale = 1;
    internal int LogicalHeight { get; private set; } = 66;

    public ProviderCard(ToolTip hints)
    {
        this.hints = hints;
        BackColor = Palette.Card;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        foreach (var label in new[] { title, status, summary })
        {
            label.ForeColor = Palette.Muted; label.BackColor = Color.Transparent;
            label.UseMnemonic = false; label.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(label);
        }
        title.Font = titleFont; title.ForeColor = Palette.Foreground;
        status.Font = smallFont; status.TextAlign = ContentAlignment.MiddleLeft;
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
        BackColor = Palette.Card; title.ForeColor = Palette.Foreground; summary.ForeColor = Palette.Muted;
        setup.LinkColor = setup.VisitedLinkColor = Palette.Foreground;
        setup.ActiveLinkColor = Palette.HighContrast ? SystemColors.WindowText : Palette.Codex;
        accent = Palette.ProviderAccent(state.Name);
        title.Text = UsagePresentation.IsClaude(state.Name) ? "Claude Code" : state.Name;
        status.Text = PopupText.Status(state, now);
        var stale = state.IsStale(now);
        statusDot = status.Text == "Live" ? Palette.Live : status.Text == "Refreshing…" ? accent : stale ? Palette.Warning : null;
        status.ForeColor = statusDot ?? Palette.Muted;
        summary.Text = PopupText.Summary(state, now);
        summary.AccessibleName = UsageAccessibility.Observation(state, now);
        setupDestination = ProviderSetup.For(state);
        setup.Text = setupDestination is null ? "" : "Set up →";
        setup.Visible = setupDestination is not null;
        setup.AccessibleName = $"Set up {state.Name} using official instructions";
        hints.SetToolTip(setup, setupDestination?.AbsoluteUri);
        if (setupDestination is not null && state.Name.StartsWith("Claude", StringComparison.Ordinal) && state.Failure == FailureKind.NotInstalled)
            summary.Text = "Install Claude Code";
        var detail = state.Detail ?? FailureText.For(state.Failure);
        hints.SetToolTip(status, detail);
        hints.SetToolTip(summary, state.Snapshot is { } source ? $"{UsageAccessibility.Observation(state, now)}\n{source.Source}" : detail);
        var selected = MonitorSelection.Select(state);
        primary.Text = selected is null ? "" : PopupText.Remaining(selected) + " remaining";
        primary.Visible = selected is not null; primaryBar.Visible = selected is not null;
        primary.ForeColor = stale ? Palette.Warning : Palette.Foreground;
        primary.AccessibleName = selected is null ? status.Text : $"{title.Text}: {PopupText.AccessibleWindowName(state.Name, selected)}, {primary.Text}, {status.Text}";
        primaryBar.Accent = accent;
        primaryBar.UpdateValue(selected?.RemainingPercent, stale);
        var windows = UsagePresentation.Windows(state)
            .OrderBy(w => w.Id == selected?.Id ? 0 : 1).ToArray();
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
            var accessibleLabel = PopupText.AccessibleWindowName(state.Name, window);
            row.Name.AccessibleName = $"{accessibleLabel}, {PopupText.Remaining(window)} remaining, {row.Reset.Text}, {status.Text}";
            row.Value.AccessibleName = $"{accessibleLabel}: {PopupText.Remaining(window)} remaining";
            row.Reset.AccessibleName = $"{accessibleLabel}: {row.Reset.Text}";
            hints.SetToolTip(row.Name, row.Name.Text);
            hints.SetToolTip(row.Reset, UsageText.Reset(window.ResetsAt, now));
            row.Bar.Accent = accent;
            row.Bar.UpdateValue(window.RemainingPercent, stale);
        }
        AccessibleName = $"{title.Text}, {status.Text}";
        AccessibleDescription = UsageAccessibility.Observation(state, now) + "; " + string.Join("; ", windows.Select(w => $"{PopupText.AccessibleWindowName(state.Name, w)}: {PopupText.Remaining(w)} remaining, {PopupText.Reset(w, now)}"));
        LogicalHeight = windows.Length == 0 ? 132 : 170 + windows.Length * 56;
        LayoutRows(scale);
        Invalidate();
    }

    private void LayoutRows(float scale)
    {
        int S(int n) => (int)Math.Round(n * scale);
        var innerWidth = Width - S(40);
        title.SetBounds(S(50), S(17), innerWidth - S(30), S(25));
        status.SetBounds(S(statusDot is null ? 20 : 32), S(49), innerWidth - S(12), S(22));
        summary.AutoEllipsis = true;
        summary.SetBounds(S(20), S(73), innerWidth - (setupDestination is null ? 0 : S(74)), S(20));
        setup.SetBounds(Width - S(90), S(73), S(70), S(20));
        primary.SetBounds(S(20), S(100), innerWidth, S(38));
        primaryBar.SetBounds(S(20), S(144), innerWidth, S(5));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var top = 162 + i * 56;
            row.Name.SetBounds(S(20), S(top), innerWidth - S(69), S(23));
            row.Value.SetBounds(Width - S(89), S(top - 1), S(69), S(24));
            row.Bar.SetBounds(0, 0, 0, 0);
            row.Reset.SetBounds(S(20), S(top + 24), innerWidth, S(20));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Transparent labels ask their parent to paint into a translated, clipped DC.
        // Graphics.Clear ignores that clip and erases adjacent labels during refresh.
        using var background = new SolidBrush(Palette.Background);
        e.Graphics.FillRectangle(background, e.ClipRectangle);
        SurfaceDrawing.Card(e.Graphics, new RectangleF(3 * currentScale, 3 * currentScale,
            Math.Max(1, Width - 6 * currentScale), Math.Max(1, Height - 8 * currentScale)), currentScale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ProviderMark.Draw(e.Graphics, providerName, new RectangleF(20 * currentScale, 19 * currentScale, 22 * currentScale, 22 * currentScale), Palette.Foreground);
        var s = currentScale;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (statusDot is { } dotColor)
        {
            using var dot = new SolidBrush(dotColor);
            e.Graphics.FillEllipse(dot, 20 * s, 57 * s, 6 * s, 6 * s);
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
        public readonly Label Reset = new() { ForeColor = Palette.Muted, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        public readonly UsageBar Bar = new();
        public Control[] Controls => [Name, Value, Reset, Bar];
        public WindowRow(Font number, Font body, Font small) { Value.Font = number; Name.Font = body; Reset.Font = small; }
    }
}
