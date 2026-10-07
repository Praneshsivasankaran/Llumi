using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class AboutTests
{
    [Fact]
    public void BundledNotesMatchRunningVersionAndDoNotClaimPublication()
    {
        var notes = Assert.IsType<ReleaseNotes>(ReleaseNotes.Load());
        Assert.Equal(typeof(UsageForm).Assembly.GetName().Version!.ToString(3), notes.Version);
        Assert.True(notes.Preview);
        Assert.Equal(4, notes.Sections.Length);
        Assert.All(notes.Sections, section => Assert.All(section.Items, item => Assert.False(string.IsNullOrWhiteSpace(item))));
    }

    [Fact]
    public void AboutNavigationReusesMainWindowAndShowsBrandWithoutInlineNotes() => RunSta(() =>
    {
        using var icon = AppIcon.Load();
        using var form = new UsageForm(["Codex", "Claude Code"], icon) { AllowExit = true };
        form.Show();
        form.ShowAbout();
        var about = Assert.Single(form.Controls.OfType<AboutView>());
        Assert.True(about.Visible);
        Assert.Contains(Descendants(about), c => c.Text == "Llumi");
        Assert.Contains(Descendants(about), c => c.Text == "Track your AI coding usage.");
        Assert.Contains(Descendants(about), c => c.Text == $"Version {ReleaseNotes.AppVersion}");
        Assert.NotNull(Assert.Single(Descendants(about).OfType<PictureBox>()).Image);
        Assert.Equal("Release notes", Assert.Single(Descendants(about).OfType<Button>()).Text);
        Assert.DoesNotContain(Descendants(about), c => c.Text.Contains("Microsoft Store") || c.Text.Contains("What’s new") || c.Text == "Local preview");
        Assert.False(form.Controls.Find("settings", true).Single().Visible);
        form.ShowSettings(); Assert.False(about.Visible);
        form.ShowAbout(); Assert.True(about.Visible);
        form.ShowUsage(); Assert.False(about.Visible);
        Assert.Single(form.Controls.OfType<AboutView>());
    });

    [Fact]
    public void ReleaseNotesOpensOnlyCanonicalWebsiteOnExplicitClick() => RunSta(() =>
    {
        var opened = new List<Uri>();
        using var host = new Form();
        using var view = new AboutView(opened.Add) { Dock = DockStyle.Fill };
        host.Controls.Add(view); host.Show();
        Assert.Empty(opened);
        Descendants(view).OfType<Button>().Single(b => b.Text == "Release notes").PerformClick();
        Assert.Equal([AboutView.ReleaseNotesUri], opened);
        Assert.Equal("https://tryllumi.com/releases/", opened[0].AbsoluteUri);
        Assert.Contains(Descendants(view), c => c.Text == $"Version {ReleaseNotes.AppVersion}");
        Assert.DoesNotContain(Descendants(view), c => c.Text.Contains("What’s new") || c.Text == "Back to About");
    });

    [Fact]
    public void BrowserFailureIsFriendlyAndRetryClearsFeedback() => RunSta(() =>
    {
        var opened = new List<Uri>();
        using var host = new Form();
        using var view = new AboutView(uri =>
        {
            opened.Add(uri);
            if (opened.Count == 1) throw new Win32Exception("private failure detail");
        }) { Dock = DockStyle.Fill };
        host.Controls.Add(view); host.Show();
        Assert.Empty(opened);
        var button = Descendants(view).OfType<Button>().Single(b => b.Text == "Release notes");
        button.PerformClick();
        Assert.Equal([AboutView.ReleaseNotesUri], opened);
        var feedback = Assert.Single(Descendants(view), c => c.Text == "Release notes could not be opened. Please try again.");
        Assert.True(feedback.Visible);
        Assert.DoesNotContain(Descendants(view), c => c.Text.Contains("private failure"));
        button.PerformClick();
        Assert.Equal([AboutView.ReleaseNotesUri, AboutView.ReleaseNotesUri], opened);
        Assert.False(feedback.Visible);
        Assert.Empty(feedback.Text);
    });

    [Theory]
    [InlineData(640, 440)]
    [InlineData(470, 377)]
    public void OverviewActionsFitWithoutScrollingAtNormalReviewSizes(int width, int height) => RunSta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude Code"], SystemIcons.Application) { AllowExit = true };
        int S(int value) => (int)Math.Round(value * form.DeviceDpi / 96d);
        form.ClientSize = new(S(width), S(height)); form.Show(); form.ShowAbout(); Application.DoEvents();
        var about = Assert.Single(form.Controls.OfType<AboutView>());
        Assert.False(about.VerticalScroll.Visible);
        foreach (var button in Descendants(about).OfType<Button>())
            Assert.True(about.ClientRectangle.Contains(about.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
    });

    [Theory]
    [InlineData(380, 320)]
    [InlineData(640, 440)]
    public void AboutFitsSmallWindowsAndRetainsThemeWithoutOpeningBrowser(int width, int height) => RunSta(() =>
    {
        using var host = new Form { ClientSize = new(width, height) };
        using var view = new AboutView(_ => throw new InvalidOperationException("No action requested")) { Dock = DockStyle.Fill };
        host.Controls.Add(view); host.Show(); Application.DoEvents();
        var body = Assert.Single(view.Controls.OfType<FlowLayoutPanel>());
        Assert.True(body.Right <= view.ClientSize.Width);
        Assert.All(Descendants(view).OfType<Label>(), label => Assert.True(label.Width <= body.Width));
        var original = Palette.IsLight;
        try
        {
            Palette.Apply(Appearance.Dark); view.ApplyTheme();
            Assert.Equal(Palette.Background, view.BackColor);
            Assert.Equal("Release notes", Assert.Single(Descendants(view).OfType<Button>()).Text);
        }
        finally { Palette.Apply(original ? Appearance.Light : Appearance.Dark); }
    });

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10))); failure?.Throw();
    }
}
