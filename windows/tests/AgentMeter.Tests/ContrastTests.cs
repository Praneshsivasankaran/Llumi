using AgentMeter.Core;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class ContrastTests
{
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static IEnumerable<Control> Controls(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Controls(child)));

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public Task ContrastThemePairsCardTextAndSurfacesRegardlessOfAppAppearance(int appearance) => Sta(() =>
    {
        try
        {
            Palette.Apply((Appearance)appearance, highContrast: true);
            using var form = new UsageForm(["Codex"], SystemIcons.Application);
            var now = DateTimeOffset.UtcNow;
            form.Show();
            form.Render([new("Codex", ProviderStatus.Ready,
                new([new("five_hour", "fixture", 20, now.AddHours(5))], now, "fixture"))], false, false);
            var card = Controls(form).OfType<ProviderCard>().Single();
            Assert.Equal(SystemColors.Window, card.BackColor);
            Assert.All(card.Controls.OfType<Label>().Where(c => c.Visible), c => Assert.Equal(SystemColors.WindowText, c.ForeColor));
            Assert.Equal(SystemColors.WindowText, Palette.Warning); // Stale monitor values also remain readable.
            Assert.Equal(SystemColors.WindowText, Palette.ProviderAccent("Codex"));
            form.ShowSettings();
            Assert.All(Controls(form).OfType<Label>().Where(c => c.Visible), c => Assert.Equal(SystemColors.WindowText, c.ForeColor));
            form.ShowAbout();
            Assert.All(Controls(form).OfType<Label>().Where(c => c.Visible), c => Assert.Equal(SystemColors.WindowText, c.ForeColor));
        }
        finally { Palette.Apply(Appearance.System); }
    });

    [Fact]
    public Task ProviderSetupLinkTracksLiveAppearanceChanges() => Sta(() =>
    {
        try
        {
            Palette.Apply(Appearance.Light, highContrast: false);
            using var form = new UsageForm(["Codex"], SystemIcons.Application);
            form.Show();
            form.Render([new("Codex", ProviderStatus.Error, Failure: FailureKind.NotInstalled,
                Authentication: AuthenticationStatus.Missing)], false, false);
            var link = Controls(form).OfType<LinkLabel>().Single();
            Assert.True(link.Visible);
            var light = link.LinkColor;
            foreach (var appearance in new[] { Appearance.Dark, Appearance.Light })
            {
                Palette.Apply(appearance, highContrast: false); form.ApplyTheme();
                Assert.Equal(Palette.Foreground, link.LinkColor);
                Assert.Equal(Palette.Foreground, link.VisitedLinkColor);
                if (appearance == Appearance.Dark) Assert.NotEqual(light, link.LinkColor);
                else Assert.Equal(light, link.LinkColor);
            }
        }
        finally { Palette.Apply(Appearance.System); }
    });

    [Fact]
    public Task SelectedNavigationKeepsVisibleFocusInsideTheContrastHighlight() => Sta(() =>
    {
        try
        {
            Palette.Apply(Appearance.Light, highContrast: true);
            using var host = new Form();
            using var button = new FocusCueButton
            {
                Navigation = true, Selected = true, Size = new(240, 56),
                BackColor = Palette.Secondary, ForeColor = Palette.Foreground, MotionAllowed = () => false
            };
            host.Controls.Add(button); host.Show(); button.Focus(); Assert.True(button.Focused);
            using var bitmap = new Bitmap(button.Width, button.Height); button.DrawToBitmap(bitmap, button.ClientRectangle);
            int S(int n) => (int)Math.Round(n * button.DeviceDpi / 96f);
            int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            // The 1.4-pixel stroke is antialiased: it must visibly favor the focus
            // foreground over the highlight fill without requiring an opaque pixel.
            var fillDistance = Distance(SystemColors.Highlight, SystemColors.HighlightText);
            Assert.Contains(Enumerable.Range(S(2), Math.Max(1, S(5) - S(2))).Select(x => bitmap.GetPixel(x, bitmap.Height / 2)),
                pixel => Distance(pixel, SystemColors.HighlightText) < fillDistance / 2);
        }
        finally { Palette.Apply(Appearance.System); }
    });

    private sealed class FocusCueButton : RoundedButton
    {
        protected override bool ShowFocusCues => true;
    }
}
