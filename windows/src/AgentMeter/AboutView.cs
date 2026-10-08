using System.Diagnostics;

namespace AgentMeter;

internal sealed class AboutView : Panel
{
    internal static readonly Uri ReleaseNotesUri = new("https://tryllumi.com/releases/");
    private readonly FlowLayoutPanel body = new() { AccessibleName = "About details", FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
    private readonly Action<Uri> openExternal;
    private readonly PictureBox identity = new() { SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Llumi" };
    private readonly Font heading = new("Segoe UI Semibold", 21, FontStyle.Regular);
    private readonly Font compactHeading = new("Segoe UI Semibold", 17, FontStyle.Regular);
    private readonly Font descriptionFont = new("Segoe UI", 11);
    private readonly Label feedback = new() { AutoSize = true, Visible = false, UseCompatibleTextRendering = false };

    internal AboutView(Action<Uri>? openExternal = null)
    {
        Name = "about"; AccessibleName = "About Llumi"; AutoScroll = true;
        this.openExternal = openExternal ?? (uri => { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
        using var icon = AppIcon.Load(128); identity.Image = icon.ToBitmap();
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
        ClearBody();
        var identityRow = new Panel { Name = "aboutIdentity", AccessibleName = "App identity", Height = S(128), Margin = new Padding(0, 0, 0, S(6)) };
        identity.Size = new(S(56), S(56)); identity.Location = Point.Empty;
        var name = TextLine("Llumi", true); name.Name = "aboutName";
        var version = TextLine($"Version {ReleaseNotes.AppVersion}"); version.Name = "aboutVersion";
        identityRow.Controls.AddRange([identity, name, version]); body.Controls.Add(identityRow);
        var description = TextLine("Track your AI coding usage."); description.Font = descriptionFont; description.Margin = new Padding(0, 0, 0, S(18));
        body.Controls.Add(description);
        body.Controls.Add(ActionButton("Release notes", () =>
        {
            try
            {
                openExternal(ReleaseNotesUri);
                feedback.Visible = false;
                feedback.Text = string.Empty;
                Arrange();
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
            { feedback.Text = "Release notes could not be opened. Please try again."; feedback.Visible = true; Arrange(); }
        }));
        foreach (var button in body.Controls.OfType<Button>())
        { button.Height = S(36); button.Margin = new Padding(0, 0, 0, S(8)); }
        body.Controls.Add(feedback);
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
        foreach (var label in body.Controls.OfType<Label>()) label.ForeColor = Palette.Muted;
        foreach (var label in body.Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<Label>()).Where(l => l.Name == "aboutVersion")) label.ForeColor = Palette.Muted;
        feedback.ForeColor = Palette.Muted;
        Arrange();
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ShowOverview();
    }
    private void Arrange()
    {
        if (IsDisposed) return;
        var scroll = AutoScrollPosition;
        var compact = ClientSize.Height < S(300);
        var width = Math.Max(S(160), Math.Min(S(500), ClientSize.Width - S(40) - SystemInformation.VerticalScrollBarWidth));
        body.SuspendLayout();
        body.Width = width;
        foreach (var label in body.Controls.OfType<Label>())
        {
            label.MaximumSize = new(width, 0); label.TextAlign = ContentAlignment.MiddleCenter;
            label.MinimumSize = new(width, 0);
            if (label != feedback) label.Margin = new Padding(0, 0, 0, S(compact ? 10 : 18));
        }
        foreach (var button in body.Controls.OfType<Button>())
        {
            button.Width = Math.Min(width, S(170)); button.Margin = new Padding((width - button.Width) / 2, 0, 0, S(8));
        }
        foreach (var row in body.Controls.OfType<Panel>())
        {
            row.Width = width; row.Height = S(compact ? 100 : 128);
            identity.Size = new(S(compact ? 40 : 56), S(compact ? 40 : 56));
            identity.Location = new((width - identity.Width) / 2, 0);
            foreach (var label in row.Controls.OfType<Label>())
            {
                label.MaximumSize = new(width, 0);
                if (label.Name == "aboutName") label.Font = compact ? compactHeading : heading;
                label.Location = new((width - label.PreferredWidth) / 2,
                    S(label.Name == "aboutName" ? compact ? 45 : 62 : compact ? 80 : 106));
            }
        }
        body.ResumeLayout(true);
        var padding = S(16);
        body.Location = new(Math.Max(S(20), (ClientSize.Width - width) / 2), Math.Max(padding, (ClientSize.Height - body.Height) / 2) + scroll.Y);
        AutoScrollMinSize = new(0, body.Height + padding * 2);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { identity.Image?.Dispose(); identity.Dispose(); feedback.Dispose(); heading.Dispose(); compactHeading.Dispose(); descriptionFont.Dispose(); }
        base.Dispose(disposing);
    }
}
