using AgentMeter.Core;

namespace AgentMeter;

// Embedded in the existing Settings scroll surface; owns no provider queries or notification delivery.
internal sealed class AlertSettingsView : Panel
{
    private readonly ToggleSwitch enabled = new() { Text = "Usage alerts", AccessibleName = "Usage alerts" };
    private readonly ToggleSwitch codex = new() { Text = "Codex alerts", AccessibleName = "Codex alerts" };
    private readonly ToggleSwitch claude = new() { Text = "Claude Code alerts", AccessibleName = "Claude Code alerts" };
    private readonly Label explanation = new() { Text = "Warn when quota is used. Alerts never stop agents or limit spending.", AutoSize = false };
    private readonly Label thresholdCaption = new() { Text = "Thresholds (% used)", AutoSize = false };
    private readonly Label validation = new() { AutoSize = false, AccessibleName = "Alert threshold validation" };
    private readonly Label snoozeStatus = new() { AutoSize = false, AccessibleName = "Alert snooze status" };
    private readonly Label deliveryStatus = new() { AutoSize = false, AccessibleName = "Notification status", Text = "Notification availability has not been checked." };
    private readonly Button test = Palette.Button("Send test notification", "Send test notification");
    private readonly Button snoozeHour = Palette.Button("Snooze for 1 hour", "Snooze alerts for 1 hour");
    private readonly Button snoozeTomorrow = Palette.Button("Until tomorrow, 09:00", "Snooze alerts until tomorrow at 09:00 local time");
    private readonly Button resume = Palette.Button("Resume alerts", "Resume alerts");
    private readonly List<(ToggleSwitch Toggle, NumericUpDown Percent)> rows = [];
    private UsageAlertSettings settings = new();
    private bool syncing;
    private float scale = 1;

    internal event Action<UsageAlertSettings>? SettingsChanged;
    internal event Action? TestNotificationRequested;

    internal AlertSettingsView()
    {
        AccessibleName = "Usage alert settings";
        Controls.AddRange([enabled, explanation, codex, claude, thresholdCaption, validation,
            snoozeStatus, deliveryStatus, test, snoozeHour, snoozeTomorrow, resume]);
        enabled.TabIndex = 0; codex.TabIndex = 1; claude.TabIndex = 2;
        foreach (var control in new[] { enabled, codex, claude }) control.CheckedChanged += (_, _) => Save();
        test.Click += (_, _) => TestNotificationRequested?.Invoke();
        snoozeHour.Click += (_, _) => Save(DateTimeOffset.UtcNow.AddHours(1), changeSnooze: true);
        snoozeTomorrow.Click += (_, _) => Save(TomorrowMorning(DateTimeOffset.UtcNow, TimeZoneInfo.Local), changeSnooze: true);
        resume.Click += (_, _) => Save(DateTimeOffset.UtcNow, changeSnooze: true);
        SetSettings(new());
    }

    // Resolve the intended local wall time once; later time-zone changes only affect its display.
    internal static DateTimeOffset TomorrowMorning(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1).AddHours(9);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Min() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    internal void SetSettings(UsageAlertSettings value)
    {
        var accepted = value.IsValid ? value : new();
        // Accepted switches/snooze changes (and their persistence rollback) do not
        // discard an unrelated invalid percentage draft. Changed saved percentages do.
        var keepDraft = HasDuplicatePercentages() && settings.EffectiveThresholds.Select(t => t.Percent)
            .SequenceEqual(accepted.EffectiveThresholds.Select(t => t.Percent));
        settings = accepted;
        syncing = true;
        try
        {
            var thresholds = settings.EffectiveThresholds;
            if (rows.Count != thresholds.Length)
            {
                foreach (var row in rows) { row.Toggle.Dispose(); row.Percent.Dispose(); }
                rows.Clear();
                for (var i = 0; i < thresholds.Length; i++)
                {
                    var number = i + 1;
                    var toggle = new ToggleSwitch { Text = $"Alert {number}", AccessibleName = $"Enable alert threshold {number}", TabIndex = 3 + i * 2 };
                    var percent = new NumericUpDown { Minimum = 1, Maximum = 100, DecimalPlaces = 0,
                        AccessibleName = $"Alert threshold {number}, percent used", TabIndex = 4 + i * 2,
                        TextAlign = HorizontalAlignment.Right, BorderStyle = BorderStyle.FixedSingle };
                    toggle.CheckedChanged += (_, _) => Save();
                    percent.ValueChanged += (_, _) => Save(thresholdEdit: true);
                    rows.Add((toggle, percent)); Controls.AddRange([toggle, percent]);
                }
            }
            enabled.Checked = settings.Enabled; codex.Checked = settings.CodexEnabled; claude.Checked = settings.ClaudeEnabled;
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].Toggle.Checked = thresholds[i].Enabled;
                if (!keepDraft) rows[i].Percent.Value = thresholds[i].Percent;
            }
            UpdateThresholdValidation();
            var next = 3 + rows.Count * 2;
            snoozeHour.TabIndex = next; snoozeTomorrow.TabIndex = next + 1; resume.TabIndex = next + 2; test.TabIndex = next + 3;
        }
        finally { syncing = false; }
        UpdateSnoozeStatus(DateTimeOffset.UtcNow);
        ApplyTheme();
    }

    private bool HasDuplicatePercentages() => rows.Select(row => row.Percent.Value).Distinct().Count() != rows.Count;

    private void UpdateThresholdValidation()
    {
        var invalid = HasDuplicatePercentages();
        validation.Text = invalid
            ? "Threshold percentages must differ. This threshold edit has not been saved."
            : "Use a different whole percentage for each threshold (1–100).";
        validation.ForeColor = invalid ? Palette.Warning : Palette.Muted;
    }

    private void Save(DateTimeOffset? deadline = null, bool changeSnooze = false, bool thresholdEdit = false)
    {
        if (syncing) return;
        var thresholds = rows.Select(r => new AlertThreshold((int)r.Percent.Value, r.Toggle.Checked)).ToArray();
        var invalid = HasDuplicatePercentages();
        UpdateThresholdValidation();
        if (invalid)
        {
            if (thresholdEdit) return;
            // Other controls remain independent of the unsaved percentages. Apply
            // threshold enable switches to their last valid saved percentage too.
            thresholds = settings.EffectiveThresholds.Select((threshold, index) =>
                threshold with { Enabled = rows[index].Toggle.Checked }).ToArray();
        }
        var requested = settings with { Enabled = enabled.Checked, CodexEnabled = codex.Checked, ClaudeEnabled = claude.Checked,
            Thresholds = thresholds, SnoozedUntil = changeSnooze ? deadline : settings.SnoozedUntil };
        if (!requested.IsValid) return;
        settings = requested;
        SettingsChanged?.Invoke(requested);
        UpdateSnoozeStatus(DateTimeOffset.UtcNow);
    }

    internal void SetStatus(string status) => deliveryStatus.Text = status;

    internal void UpdateSnoozeStatus(DateTimeOffset now)
    {
        var snoozed = settings.IsSnoozed(now);
        snoozeStatus.Text = snoozed && settings.SnoozedUntil is { } deadline
            ? $"Snoozed until {deadline.ToLocalTime():ddd, d MMM yyyy, HH:mm}. Monitoring continues."
            : "Alerts are not snoozed.";
        resume.Enabled = snoozed;
    }

    internal void ApplyTheme()
    {
        BackColor = Palette.Card; ForeColor = Palette.Foreground;
        foreach (Control child in Controls) { child.BackColor = Palette.Card; child.ForeColor = Palette.Foreground; }
        foreach (var label in new[] { explanation, thresholdCaption, snoozeStatus, deliveryStatus }) label.ForeColor = Palette.Muted;
        validation.ForeColor = validation.Text.Contains("not been saved", StringComparison.Ordinal) ? Palette.Warning : Palette.Muted;
        foreach (var button in new[] { test, snoozeHour, snoozeTomorrow, resume }) Palette.StyleButton(button);
        Invalidate(true);
    }

    internal int Arrange(int width, float dpiScale)
    {
        scale = dpiScale; Width = width;
        int S(int n) => (int)Math.Round(n * scale);
        var inside = Math.Max(S(160), width - S(28));
        enabled.SetBounds(S(14), S(12), inside, S(34));
        explanation.SetBounds(S(16), S(53), inside - S(4), S(52));
        codex.SetBounds(S(14), S(108), inside, S(34));
        claude.SetBounds(S(14), S(150), inside, S(34));
        thresholdCaption.SetBounds(S(16), S(197), inside, S(28));
        var y = 230;
        foreach (var row in rows)
        {
            row.Toggle.SetBounds(S(14), S(y), inside - S(96), S(34));
            row.Percent.SetBounds(width - S(94), S(y + 3), S(78), S(30));
            y += 44;
        }
        validation.SetBounds(S(16), S(y), inside - S(4), S(58)); y += 64;
        snoozeStatus.SetBounds(S(16), S(y), inside - S(4), S(58)); y += 64;
        var stacked = inside < S(450);
        var buttonWidth = stacked ? inside : (inside - S(10)) / 2;
        snoozeHour.SetBounds(S(14), S(y), buttonWidth, S(36));
        snoozeTomorrow.SetBounds(stacked ? S(14) : S(24) + buttonWidth, S(y + (stacked ? 44 : 0)), buttonWidth, S(36));
        y += stacked ? 88 : 44;
        resume.SetBounds(S(14), S(y), inside, S(36)); y += 48;
        test.SetBounds(S(14), S(y), inside, S(36)); y += 48;
        deliveryStatus.SetBounds(S(16), S(y), inside - S(4), S(98)); y += 114;
        Height = S(y);
        return Height;
    }
}
