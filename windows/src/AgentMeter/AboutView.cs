using System.Diagnostics;

namespace AgentMeter;

internal sealed class AboutView : Panel
{
    internal static readonly Uri StoreUri = new("https://apps.microsoft.com/detail/9NV153Q5K5MQ");
    private readonly FlowLayoutPanel body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
    private readonly Action<Uri> openExternal;
    private readonly PictureBox identity = new() { SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Llumi" };
    private readonly Font heading = new("Segoe UI", 19, FontStyle.Bold);
    private readonly Font sectionHeading = new("Segoe UI", 11, FontStyle.Bold);
    private readonly Label feedback = new() { AutoSize = true, Visible = false, UseCompatibleTextRendering = false };
    private readonly ReleaseNotes? notes = ReleaseNotes.Load();
    private bool showingNotes;
    internal bool ShowingNotes => showingNotes;

    internal AboutView(Action<Uri>? openExternal = null)
    {
        Name = "about"; AutoScroll = true;
        this.openExternal = openExternal ?? (uri => { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
        using var icon = AppIcon.Load(64); identity.Image = icon.ToBitmap();
        Controls.Add(body);
        Resize += (_, _) => Arrange();
        ShowOverview();
    }
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96f);
    private Label TextLine(string text, bool title = false) => new()
    {
        Text = text, AccessibleName = text, AutoSize = true, UseMnemonic = false, UseCompatibleTextRendering = false,
        Font = title ? heading : Font, Margin = new Padding(0, 0, 0, S(14))
    };
    private Button ActionButton(string text, Action action)
    {
        var button = Palette.Button(text, text);
        button.Width = S(218); button.Height = S(38); button.Margin = new Padding(0, 0, 0, S(12));
        button.Click += (_, _) => action();
        return button;
    }
    private void ClearBody()
    {
        body.SuspendLayout();
        identity.Parent?.Controls.Remove(identity);
        foreach (var control in body.Controls.Cast<Control>().ToArray())
        { body.Controls.Remove(control); if (control != identity && control != feedback) control.Dispose(); }
        feedback.Visible = false; feedback.Text = string.Empty;
        AutoScrollPosition = Point.Empty;
    }
    internal void ShowOverview()
    {
        showingNotes = false; ClearBody();
        var identityRow = new Panel { Name = "aboutIdentity", Height = S(56), Margin = new Padding(0, 0, 0, S(12)) };
        identity.Size = new(S(48), S(48)); identity.Location = Point.Empty;
        var name = TextLine("Llumi", true); name.Location = new(S(64), 0);
        var version = TextLine($"Version {ReleaseNotes.AppVersion}"); version.Location = new(S(64), S(34));
        identityRow.Controls.AddRange([identity, name, version]); body.Controls.Add(identityRow);
        var description = TextLine("Track your AI coding usage."); description.Margin = new Padding(0, 0, 0, S(12));
        body.Controls.Add(description);
        body.Controls.Add(ActionButton("Release notes", ShowReleaseNotes));
        body.Controls.Add(ActionButton("Open Microsoft Store", () =>
        {
            try { openExternal(StoreUri); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
            { feedback.Text = "Microsoft Store could not be opened. Please try again."; feedback.Visible = true; Arrange(); }
        }));
        foreach (var button in body.Controls.OfType<Button>())
        { button.Height = S(34); button.Margin = new Padding(0, 0, 0, S(8)); }
        body.Controls.Add(feedback);
        body.ResumeLayout(); ApplyTheme();
    }
    internal void ShowReleaseNotes()
    {
        showingNotes = true; ClearBody();
        body.Controls.Add(ActionButton("Back to About", ShowOverview));
        body.Controls.Add(TextLine($"What’s new in {ReleaseNotes.AppVersion}", true));
        if (notes is null) body.Controls.Add(TextLine("Release notes are unavailable for this version."));
        else
        {
            if (notes.Preview) body.Controls.Add(TextLine("Local preview"));
            body.Controls.Add(TextLine(notes.Summary));
            foreach (var section in notes.Sections)
            {
                var label = TextLine(section.Title); label.Font = sectionHeading; body.Controls.Add(label);
                foreach (var item in section.Items) body.Controls.Add(TextLine("• " + item));
            }
        }
        body.ResumeLayout(); ApplyTheme();
    }
    internal void ApplyTheme()
    {
        BackColor = body.BackColor = Palette.Background; ForeColor = body.ForeColor = Palette.Foreground;
        void Theme(Control child)
        {
            child.ForeColor = Palette.Foreground; child.BackColor = Palette.Background;
            if (child is Button button) { button.BackColor = Palette.Card; Palette.StyleButton(button); }
            foreach (Control nested in child.Controls) Theme(nested);
        }
        foreach (Control child in body.Controls) Theme(child);
        feedback.ForeColor = Palette.Muted;
        Arrange();
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (showingNotes) ShowReleaseNotes(); else ShowOverview();
    }
    private void Arrange()
    {
        if (IsDisposed) return;
        var scroll = AutoScrollPosition;
        var width = Math.Max(S(160), Math.Min(S(500), ClientSize.Width - S(40) - SystemInformation.VerticalScrollBarWidth));
        body.SuspendLayout();
        body.Width = width;
        foreach (var label in body.Controls.OfType<Label>()) label.MaximumSize = new(width, 0);
        foreach (var button in body.Controls.OfType<Button>()) button.Width = Math.Min(width, S(218));
        foreach (var row in body.Controls.OfType<Panel>())
        {
            row.Width = width;
            foreach (var label in row.Controls.OfType<Label>()) label.MaximumSize = new(Math.Max(1, width - S(64)), 0);
        }
        body.ResumeLayout(true);
        var padding = showingNotes ? S(24) : S(16);
        body.Location = new(Math.Max(S(20), (ClientSize.Width - width) / 2), padding + scroll.Y);
        AutoScrollMinSize = new(0, body.Height + padding * 2);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { identity.Image?.Dispose(); identity.Dispose(); feedback.Dispose(); heading.Dispose(); sectionHeading.Dispose(); }
        base.Dispose(disposing);
    }
}
