using System.Reflection;

namespace AgentMeter.Tests;

[Collection("Windows UI")]
public sealed class ToggleSwitchTests
{
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static IEnumerable<Control> Controls(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Controls(child)));
    private static void Click(CheckBox control) => typeof(CheckBox)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [EventArgs.Empty]);

    [Fact]
    public Task NativeSpaceAndAccessibleActionToggleCheckedState() => Sta(() =>
    {
        using var host = new Form();
        using var toggle = new ToggleSwitch { Text = "Show monitor", AccessibleName = "Show monitor", Size = new(280, 40) };
        host.Controls.Add(toggle); host.Show(); toggle.Focus();
        var changes = 0; toggle.CheckedChanged += (_, _) => changes++;
        Assert.Equal(AccessibleRole.CheckButton, toggle.AccessibilityObject.Role);
        Assert.False(toggle.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
        typeof(CheckBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, [new KeyEventArgs(Keys.Space)]);
        typeof(CheckBox).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, [new KeyEventArgs(Keys.Space)]);
        Assert.True(toggle.Checked); Assert.Equal(1, changes);
        Assert.True(toggle.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
        toggle.AccessibilityObject.DoDefaultAction();
        Assert.False(toggle.Checked); Assert.Equal(2, changes);
        toggle.Enabled = false;
        Assert.True(toggle.AccessibilityObject.State.HasFlag(AccessibleStates.Unavailable));
        toggle.AccessibilityObject.DoDefaultAction(); Assert.False(toggle.Checked); Assert.Equal(2, changes);
    });

    [Fact]
    public Task SettingsSlidersRetainPersistenceRollbackAndStartupDispatch() => Sta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        form.Show(); form.ShowSettings();
        var toggles = Controls(form).OfType<ToggleSwitch>().ToArray(); Assert.Equal(5, toggles.Length);
        Assert.Equal(5, Controls(form).OfType<CheckBox>().Count());
        var current = new Preferences(); form.SetPreferences(current);
        var saves = new List<Preferences>(); form.PreferencesChanged += value => { saves.Add(value); form.SetPreferences(value); };
        var monitor = toggles.Single(c => c.Text == "Show monitor"); Click(monitor);
        Assert.Single(saves); Assert.False(saves[0].CompactMonitor); Assert.False(monitor.Checked);
        form.SetPreferences(current); form.PreferenceSaveFailed();
        Assert.True(monitor.Checked); Assert.Single(saves);
        Assert.Contains(Controls(form).OfType<Label>(), c => c.Text.Contains("previous preferences remain active"));
        var codex = toggles.Single(c => c.AccessibleName == "Monitor Codex"); Click(codex);
        Assert.False(saves[^1].CodexEnabled); Assert.True(saves[^1].ClaudeEnabled);
        var starts = 0; form.StartupToggleRequested += () => starts++;
        var launch = toggles.Single(c => c.Text == "Launch at Startup");
        form.SetStartupState(false, true); Click(launch); Assert.Equal(1, starts);
        launch.Checked = false; Assert.Equal(2, starts); // UIA Toggle changes Checked without Click.
        form.SetStartupState(false, false); Assert.False(launch.Checked); Assert.False(launch.Enabled); Assert.Equal(2, starts);
        launch.Checked = true; Assert.Equal(2, starts); // A disabled control cannot request a startup change.
    });

    [Fact]
    public Task SettingsTabNavigationFollowsVisibleRows() => Sta(() =>
    {
        using var form = new UsageForm(["Codex", "Claude"], SystemIcons.Application);
        form.Render([new("Codex", AgentMeter.Core.ProviderStatus.Error, Failure: AgentMeter.Core.FailureKind.LoggedOut)], false, false);
        form.Show(); form.ShowSettings(); form.SetStartupState(false, true);
        var panel = Controls(form).OfType<Panel>().Single(c => c.Name == "settings");
        var expected = new[] { "Monitor Codex", "Monitor Claude Code", "Retry provider checks", "Launch at Startup", "Show monitor", "Tray Icon", "Appearance", "Reset compact monitor position" };
        var first = panel.Controls.Cast<Control>().Single(c => c.AccessibleName == expected[0]);
        first.Focus(); Assert.True(first.Focused);
        foreach (var name in expected.Skip(1))
        {
            Assert.True(panel.SelectNextControl(form.ActiveControl, true, true, true, false));
            Assert.Equal(name, form.ActiveControl?.AccessibleName);
        }
    });

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public Task ForegroundOnlyPaintFillsTheNativeOpaqueSurfaceAndHonorsItsClip(int mode) => Sta(() =>
    {
        try
        {
            Palette.Apply(mode == 1 ? Appearance.Light : Appearance.Dark, highContrast: mode == 4);
            using var host = new Form();
            using var toggle = new ToggleSwitch
            {
                Text = "Monitor Claude Code", BackColor = mode == 3 ? Color.White : Palette.Card,
                ForeColor = mode == 3 ? Color.Black : Palette.Foreground,
                LightAppearanceOverride = mode == 3 ? true : null, MotionAllowed = () => false
            };
            host.Controls.Add(toggle); _ = toggle.Handle;
            int S(int n) => (int)Math.Round(n * toggle.DeviceDpi / 96f);
            toggle.Size = new(S(300), S(40));
            var clip = new Rectangle(S(4), S(4), toggle.Width - S(8), toggle.Height - S(8));
            var expected = mode == 4 ? SystemColors.Window : toggle.BackColor;
            foreach (var value in new[] { false, true })
            {
                toggle.Checked = value;
                using var bitmap = new Bitmap(toggle.Width, toggle.Height);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Magenta); graphics.SetClip(clip);
                using var paint = new PaintEventArgs(graphics, clip);
                // The native Opaque path does not promise a prior background paint.
                typeof(ToggleSwitch).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, [paint]);
                Assert.Equal(expected.ToArgb(), bitmap.GetPixel(toggle.Width / 2, S(6)).ToArgb());
                Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel(1, 1).ToArgb());
            }
        }
        finally { Palette.Apply(Appearance.System); }
    });

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public Task PillAppearanceAndThumbPositionFollowState(int appearance) => Sta(() =>
    {
        try
        {
            Palette.Apply((Appearance)appearance);
            using var host = new Form();
            using var toggle = new ToggleSwitch { Text = "Show monitor", Size = new(300, 40), BackColor = Palette.Background, ForeColor = Palette.Foreground, MotionAllowed = () => false };
            host.Controls.Add(toggle); host.Show();
            using var off = new Bitmap(toggle.Width, toggle.Height); toggle.DrawToBitmap(off, toggle.ClientRectangle);
            toggle.Checked = true;
            using var on = new Bitmap(toggle.Width, toggle.Height); toggle.DrawToBitmap(on, toggle.ClientRectangle);
            int S(int n) => (int)Math.Round(n * toggle.DeviceDpi / 96f);
            var x = toggle.Width - S(37); var y = toggle.Height / 2;
            Assert.NotEqual(off.GetPixel(x, y), on.GetPixel(x, y));
            var onTrack = on.GetPixel(x, y); Assert.True(onTrack.B > onTrack.R);
            toggle.Enabled = false;
            using var disabled = new Bitmap(toggle.Width, toggle.Height); toggle.DrawToBitmap(disabled, toggle.ClientRectangle);
            Assert.NotEqual(on.GetPixel(x, y), disabled.GetPixel(x, y));
        }
        finally { Palette.Apply(Appearance.System); }
    });
}
