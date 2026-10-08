using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AgentMeter.Core;
using Microsoft.Win32;

namespace AgentMeter;

internal sealed class MonitorForm : Form
{
    private string[] providerNames;
    private string[] requestedProviderNames;
    private readonly ContextMenuStrip menu = new();
    private readonly Font bodyFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font numberFont = new("Segoe UI Semibold", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly System.Windows.Forms.Timer transition = new() { Interval = 16 };
    private readonly Font expandedNumberFont = new("Segoe UI Semibold", 17, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly MonitorAnimation animation = new();
    private readonly System.Windows.Forms.Timer hoverDelay = new();
    private readonly Func<Rectangle>? workingArea;
    private readonly Func<Point> pointerPosition;
    private MonitorPosition? positionAnchor;
    private MonitorDragGesture? pointerGesture;
    private MonitorRow[] rows = [];
    private bool hoverPending, providersChanged;
    private Rectangle transitionStartBounds, transitionTargetBounds;
    private double opacity = 1, expansion, startOpacity, targetOpacity, startExpansion, targetExpansion;
    internal bool DesiredVisible { get; private set; }
    internal bool IsAnimating => transition.Enabled;
    internal bool NativeAnimation => animation.Native;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<bool> MotionAllowed { get; set; } = () => MonitorAnimation.MotionAllowed;
    internal IReadOnlyList<string> ProviderNames => providerNames;
    internal Point RestingLocation => AnchoredLocation(SizeForDpi(DeviceDpi, providerNames.Length));
    internal IReadOnlyList<string> ExpandedProviderNames => rows.Select(r => r.Name == "Claude" ? "Claude Code" : r.Name).ToArray();
    internal IReadOnlyList<string> ExpandedObservations => rows.Select(r => r.Observation).ToArray();
    internal static Color Accent(string name) => SystemInformation.HighContrast ? SystemColors.WindowText :
        name.StartsWith("Claude", StringComparison.Ordinal)
            ? (Palette.IsLight ? Color.FromArgb(151, 65, 24) : Color.FromArgb(255, 186, 143))
            : (Palette.IsLight ? Color.FromArgb(13, 112, 86) : Color.FromArgb(115, 230, 196));
    private static Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Palette.Foreground;
    private static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Palette.Muted;
    internal static bool TransparencyAllowed
    {
        get
        {
            if (SystemInformation.HighContrast) return false;
            try { return (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) != 0; }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
        }
    }
    private Bitmap? surface;
    private bool hovered, drawing, opaqueFallback;
    private Point originalPosition;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool AllowExit { get; set; }
    internal int RenderVersion { get; private set; }
    internal bool UsesPerPixelTransparency => !opaqueFallback;
    internal IReadOnlyList<string> RowValues => rows.Select(r => r.Value).ToArray();
    internal bool Expanded => hovered;
    internal event Action? OpenRequested;
    internal event Action? UnpinRequested;
    internal event Action? RefreshRequested;
    internal event Action? ExitRequested;
    internal event Action? PositionCommitted;
    internal event Action? SurfaceFallbackUsed;
    internal event Action? ResetPositionRequested;
    internal MonitorForm(IEnumerable<string> names, Icon icon, Func<Rectangle>? workingArea = null, Func<Point>? pointerPosition = null)
    {
        this.workingArea = workingArea;
        this.pointerPosition = pointerPosition ?? (() => Cursor.Position);
        providerNames = requestedProviderNames = names.Distinct().ToArray();
        Text = "Llumi monitor"; Icon = icon;
        AccessibleName = "Llumi compact monitor";
        AccessibleDescription = "Remaining coding subscription allowance. Hover for details; Open Usage shows all supported limits. Click, Enter or Control+1 opens Usage. Drag to move. Reset Position is in the menu.";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Background; ForeColor = Palette.Foreground;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        menu.Items.Add("Open Llumi", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Refresh", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add("Hide monitor", null, (_, _) => UnpinRequested?.Invoke());
        menu.Items.Add("Reset Position", null, (_, _) => ResetPositionRequested?.Invoke());
        menu.Items.Add("Quit", null, (_, _) => ExitRequested?.Invoke());
        ContextMenuStrip = menu;
        transition.Tick += (_, _) => AdvanceTransition();
        hoverDelay.Tick += (_, _) => ApplyPendingHover();
        FormClosing += (_, e) => { if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; UnpinRequested?.Invoke(); } };
        _ = Handle; ClientSize = SizeForDpi(DeviceDpi, providerNames.Length);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; if (!opaqueFallback) p.ExStyle |= 0x80000; return p; }
    }
    internal static Size SizeForDpi(int dpi, int providerCount = 2, bool expanded = false, bool claudeDetails = true, int allowanceRows = 2)
    {
        var scale = Math.Clamp(dpi, 48, 768) / 96d;
        return new((int)Math.Round((expanded ? (providerCount > 1 ? 390 : 228) : (providerCount > 1 ? 202 : 112)) * scale),
            (int)Math.Round((expanded ? Math.Max(178, 124 + Math.Max(0, allowanceRows) * 54) : 34) * scale));
    }
    private Size CurrentSize(bool expanded)
    {
        var size = SizeForDpi(DeviceDpi, providerNames.Length, expanded, allowanceRows: rows.Select(r => r.Windows.Length).DefaultIfEmpty(0).Max());
        if (expanded && IsHandleCreated)
        {
            var area = WorkingArea; var margin = (int)Math.Round(16 * DeviceDpi / 96d);
            size.Height = Math.Min(size.Height, Math.Max(1, area.Height - margin));
            size.Width = Math.Min(size.Width, Math.Max(1, area.Width - margin));
        }
        return size;
    }
    private Rectangle WorkingArea => workingArea?.Invoke() ?? Screen.FromControl(this).WorkingArea;
    internal Size ExpandedSize => CurrentSize(true);
    internal MonitorDetailLayout ExpandedLayout => DetailLayout(DeviceDpi, ExpandedSize.Height, rows.Select(r => r.Windows.Length).DefaultIfEmpty(0).Max());
    internal IReadOnlyList<int> ExpandedAllowanceCounts => rows.Select(r => Math.Min(r.Windows.Length, ExpandedLayout.VisibleRows)).ToArray();
    internal static MonitorDetailLayout DetailLayout(int dpi, int pixelHeight, int allowanceRows)
    {
        var height = Math.Max(1, pixelHeight) * 96f / Math.Clamp(dpi, 48, 768);
        var count = Math.Max(0, allowanceRows);
        if (height + .51f * 96 / Math.Clamp(dpi, 48, 768) >= Math.Max(178, 124 + count * 54))
            return new(count, false, false, true, false, 78 + Math.Max(1, count) * 54,
                97 + Math.Max(1, count) * 54, 8, 78, 0, 0);
        // Names, observation date, status and Open Usage stay visible before choosing
        // complete allowance rows. Tiny work areas fall back to the accessible footer.
        if (height >= 145)
        {
            var visible = Math.Min(count, (int)Math.Floor((height - 145) / 54));
            return new(visible, true, false, true, false, 78 + visible * 54,
                97 + visible * 54, 8, 78, height - 21, 19);
        }
        if (height >= 114) return new(0, true, true, true, false, 54, 73, 4, 0, height - 21, 19);
        if (height >= 82) return new(0, true, true, false, false, 23, 42, 4, 0, height - 21, 19);
        var footer = Math.Min(19, Math.Max(1, height - 4));
        return new(0, true, true, false, true, 0, 0, -1, 0,
            Math.Max(0, (height - footer) / 2), footer);
    }
    internal readonly record struct MonitorDetailLayout(int VisibleRows, bool Overflow, bool CompactHeader,
        bool HeaderVisible, bool CombinedStatus, float StatusY, float ObservationY, float NameY, float AllowanceY, float UsageY, float FooterHeight);
    internal void SetProviders(IEnumerable<string> names)
    {
        var next = names.Distinct().ToArray(); requestedProviderNames = next; if (providerNames.SequenceEqual(next)) return;
        providerNames = next; providersChanged = true;
        // Removing providers must invalidate cached accessibility children even
        // when the hidden monitor will not receive another scheduled render.
        rows = next.Select(name => rows.FirstOrDefault(row => row.Name == name)).OfType<MonitorRow>().ToArray();
        AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        if (Visible) { if (pointerGesture is null) BeginTransition(CurrentSize(hovered), 1, hovered ? 1 : 0, true); }
        else { ClientSize = CurrentSize(hovered); KeepOnScreen(); }
    }
    internal void Render(IReadOnlyList<ProviderState> states, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var enabledNames = requestedProviderNames.Where(name => states.FirstOrDefault(s => s.Name == name)?.Enabled != false).ToArray();
        if (!providerNames.SequenceEqual(enabledNames)) { providerNames = enabledNames; providersChanged = true; }
        var next = providerNames.Select(name =>
        {
            var state = states.FirstOrDefault(s => s.Name == name) ?? new(name, ProviderStatus.Unavailable);
            var window = MonitorSelection.Select(state); var stale = state.IsStale(at);
            var value = window is not null ? PopupText.Remaining(window) : state.Status == ProviderStatus.Loading ? "…" : "—";
            var status = PopupText.Status(state, at);
            var reset = window is null ? status : PopupText.Reset(window, at);
            var hint = $"{name}: {status}\n{value} remaining\n{(window is null ? "" : PopupText.AccessibleWindowName(name, window))}\n{reset}\n{UsageAccessibility.Observation(state, at)}";
            var allowances = UsagePresentation.Windows(state);
            var windows = allowances.Select(w => new MonitorDetail(PopupText.WindowName(name, w), PopupText.Remaining(w), PopupText.Reset(w, at))).ToArray();
            var details = string.Join("\n", windows.Select(w => $"{w.Label} · {w.Value} remaining\n{w.Reset}"));
            var accessibleDetails = string.Join("\n", allowances.Select(w => $"{PopupText.AccessibleWindowName(name, w)} · {PopupText.Remaining(w)} remaining\n{PopupText.Reset(w, at)}"));
            var observation = state.Snapshot is { } snapshot ? PopupText.ObservationDate(snapshot.ObservedAt, at) : "Not yet updated";
            return new MonitorRow(name, value, window?.RemainingPercent, stale, status, reset, observation,
                window is null ? "Allowance unavailable" : PopupText.WindowName(name, window), hint + "\n" + accessibleDetails, details, windows);
        }).ToArray();
        var changed = providersChanged || !rows.Select(r => r.Visual(hovered)).SequenceEqual(next.Select(r => r.Visual(hovered)));
        var accessibilityChanged = !rows.Select(r => r.Hint).SequenceEqual(next.Select(r => r.Hint));
        rows = next; providersChanged = false;
        if (providerNames.Length == 0 && Visible) HideMonitor();
        var size = CurrentSize(hovered);
        if (size != ClientSize && !transition.Enabled && pointerGesture is null) BeginTransition(size, 1, hovered ? 1 : 0, Visible);
        else if (changed) UpdateSurface();
        if (accessibilityChanged) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }
    internal void ShowMonitor(Point position, MonitorPosition? saved = null)
    {
        var wasVisible = Visible; DesiredVisible = true;
        if (providerNames.Length == 0) return;
        var size = CurrentSize(hovered);
        if (!wasVisible)
        {
            Location = position; ClientSize = size;
            positionAnchor = saved is { IsValid: true, Version: 2 } ? saved :
                MonitorPosition.Capture(position, Screen.FromControl(this).DeviceName, WorkingArea, size);
            KeepOnScreen();
            if (MotionAllowed())
            {
                var width = (int)(Width * .9); Location = new(Left + (Width - width) / 2, Top);
                ClientSize = new(width, Math.Max(3, DeviceDpi / 24)); opacity = 0;
            }
            else opacity = 1;
            TopMost = true; Show(); UpdateSurface();
        }
        BeginTransition(size, 1, hovered ? 1 : 0, true);
    }
    internal void HideMonitor(bool animate = false)
    {
        if (!DesiredVisible && transition.Enabled && animate) return;
        DesiredVisible = false; hoverDelay.Stop(); pointerGesture = null; Capture = false; hovered = false;
        if (animate && Visible)
            BeginTransition(new(Math.Max(1, (int)(Width * .9)), Math.Max(3, DeviceDpi / 32)), 0, 0, true);
        else { transition.Stop(); animation.Stop(); expansion = 0; Hide(); TopMost = false; }
    }
    private void BeginTransition(Size size, double alpha, double expanded, bool animate)
    {
        transition.Stop(); animation.Stop();
        transitionStartBounds = Bounds;
        EnsureAnchor();
        transitionTargetBounds = new(AnchoredLocation(size), size);
        startOpacity = opacity; targetOpacity = alpha; startExpansion = expansion; targetExpansion = expanded;
        if (animate && Visible && MotionAllowed())
        {
            animation.Start(alpha == 0 ? .14 : .24);
            if (animation.Native) { transition.Start(); return; }
        }
        ApplyFrame(1);
    }
    private void AdvanceTransition()
    {
        var progress = MotionAllowed() ? animation.Progress : 1;
        ApplyFrame(progress);
        if (progress >= 1) { transition.Stop(); animation.Stop(); }
    }
    private void ApplyFrame(double progress)
    {
        int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * progress);
        Bounds = new(Lerp(transitionStartBounds.X, transitionTargetBounds.X), Lerp(transitionStartBounds.Y, transitionTargetBounds.Y),
            Math.Max(1, Lerp(transitionStartBounds.Width, transitionTargetBounds.Width)), Math.Max(1, Lerp(transitionStartBounds.Height, transitionTargetBounds.Height)));
        opacity = startOpacity + (targetOpacity - startOpacity) * progress;
        expansion = startExpansion + (targetExpansion - startExpansion) * progress;
        UpdateSurface();
        if (progress >= 1 && !DesiredVisible && targetOpacity == 0) { Hide(); TopMost = false; expansion = 0; }
    }
    internal void CommitPosition()
    {
        transition.Stop(); animation.Stop();
        Location = PopupPlacement.Clamp(Location, Size, WorkingArea);
        positionAnchor = MonitorPosition.Capture(Location, Screen.FromControl(this).DeviceName, WorkingArea, Size);
        KeepOnScreen();
        PositionCommitted?.Invoke();
    }
    private void EnsureAnchor() => positionAnchor ??= MonitorPosition.Capture(Location, Screen.FromControl(this).DeviceName, WorkingArea, Size);
    private Point AnchoredLocation(Size size)
    {
        EnsureAnchor();
        return MonitorPosition.Restore(positionAnchor, positionAnchor!.Display, WorkingArea, size, DeviceDpi);
    }
    internal MonitorPosition SavedPosition
    {
        get { EnsureAnchor(); return positionAnchor!; }
    }
    internal void RestorePosition(MonitorPosition? saved, string display)
    {
        var compact = SizeForDpi(DeviceDpi, providerNames.Length);
        var restored = MonitorPosition.Restore(saved, display, WorkingArea, compact, DeviceDpi);
        positionAnchor = saved is { IsValid: true, Version: 2 } && string.Equals(saved.Display, display, StringComparison.OrdinalIgnoreCase)
            ? saved : MonitorPosition.Capture(restored, display, WorkingArea, compact);
        KeepOnScreen();
    }
    internal void KeepOnScreen()
    {
        transition.Stop(); animation.Stop();
        var size = CurrentSize(hovered);
        Bounds = new(AnchoredLocation(size), size);
        expansion = hovered ? 1 : 0;
        UpdateSurface();
    }
    internal void SetExpanded(bool value, bool animate = true)
    {
        if (pointerGesture is not null || providerNames.Length == 0 || hovered == value || (Visible && !DesiredVisible)) return;
        hovered = value;
        BeginTransition(CurrentSize(hovered), 1, hovered ? 1 : 0, animate);
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        EnsureAnchor(); base.OnDpiChanged(e);
        transition.Stop(); animation.Stop();
        if (pointerGesture is null) KeepOnScreen();
        else { ClientSize = CurrentSize(hovered); expansion = hovered ? 1 : 0; UpdateSurface(); }
    }
    private void ScheduleHover(bool value)
    { hoverDelay.Stop(); hoverPending = value; hoverDelay.Interval = value ? 120 : 100; if (DesiredVisible && pointerGesture is null) hoverDelay.Start(); }
    private void ApplyPendingHover()
    {
        hoverDelay.Stop();
        if (pointerGesture is null && DesiredVisible && (hoverPending || !Bounds.Contains(pointerPosition()))) SetExpanded(hoverPending);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); ScheduleHover(true); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ScheduleHover(false); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        hoverDelay.Stop();
        if (transition.Enabled) { ApplyFrame(1); transition.Stop(); animation.Stop(); }
        pointerGesture = new(pointerPosition(), DeviceDpi); originalPosition = Location; Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); MovePointer();
    }
    private void MovePointer()
    {
        if (pointerGesture is null) return;
        var point = pointerPosition(); pointerGesture.Move(point);
        if (pointerGesture.IsDragging)
            Location = new(originalPosition.X + point.X - pointerGesture.Origin.X, originalPosition.Y + point.Y - pointerGesture.Origin.Y);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (pointerGesture is null || e.Button != MouseButtons.Left) return;
        MovePointer(); var moved = pointerGesture.IsDragging;
        pointerGesture = null; Capture = false;
        if (moved) CommitPosition(); else { KeepOnScreen(); OpenRequested?.Invoke(); }
        ScheduleHover(Bounds.Contains(pointerPosition()));
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (pointerGesture is null || Capture) return;
        var moved = pointerGesture.IsDragging; pointerGesture = null;
        if (moved) CommitPosition(); else KeepOnScreen();
        ScheduleHover(Bounds.Contains(pointerPosition()));
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter) { OpenRequested?.Invoke(); return true; }
        if (keyData == Keys.Escape) { UnpinRequested?.Invoke(); return true; }
        if (keyData == (Keys.Control | Keys.R)) { RefreshRequested?.Invoke(); return true; }
        if (keyData == (Keys.Control | Keys.D1)) { OpenRequested?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    internal Bitmap CreatePreviewBitmap()
    {
        var image = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(image); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent); g.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
        var width = Width * 96f / DeviceDpi; var height = Height * 96f / DeviceDpi;
        var expanded = (float)expansion;
        var radius = Math.Min(height / 2, 17 + expanded);
        using var shape = DrawingHelpers.RoundedRectangle(new(.5f, .5f, Math.Max(1, width - 1), Math.Max(1, height - 1)), radius);
        if (!SystemInformation.HighContrast)
        {
            using var shadow = new Pen(Color.FromArgb(Palette.IsLight ? 20 : 40, Color.Black), 3);
            g.TranslateTransform(0, 1);
            g.DrawPath(shadow, shape);
            g.TranslateTransform(0, -1);
        }
        // Native per-pixel translucent HUD: no captured desktop pixels, private blur APIs,
        // or forced backdrop incompatible with a nonactivating layered window.
        var alpha = TransparencyAllowed ? 242 : 255;
        var top = SystemInformation.HighContrast ? SystemColors.Window : Palette.IsLight ? Color.FromArgb(253, 253, 254) : Color.FromArgb(43, 44, 48);
        var bottom = SystemInformation.HighContrast ? SystemColors.Window : Palette.IsLight ? Color.FromArgb(246, 247, 249) : Color.FromArgb(34, 35, 39);
        using var background = new LinearGradientBrush(new RectangleF(0, 0, width, Math.Max(1, height)), Color.FromArgb(alpha, top), Color.FromArgb(alpha, bottom), 90);
        g.FillPath(background, shape);
        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(150, Palette.Border), SystemInformation.HighContrast ? 1.5f : .75f);
        g.DrawPath(border, shape);
        using var animatedNumberFont = new Font("Segoe UI Semibold", 13 + 4 * expanded, FontStyle.Regular, GraphicsUnit.Pixel);
        var count = rows.Length;
        var compactWidth = Math.Min(count > 1 ? 202f : 112f, width);
        var expandedWidth = Math.Min(count > 1 ? 390f : 228f, width);
        var contentWidth = compactWidth + (expandedWidth - compactWidth) * expanded;
        var offset = (width - contentWidth) / 2;
        var detailLayout = DetailLayout(DeviceDpi, Height, rows.Select(r => r.Windows.Length).DefaultIfEmpty(0).Max());
        var padding = Math.Min(18, expandedWidth / 10);
        var gap = count > 1 ? Math.Min(37, Math.Max(0, expandedWidth - padding * 2 - count * 100)) : 0;
        for (var i = 0; i < count; i++)
        {
            var row = rows[i]; var ink = row.Stale ? Palette.Warning : Accent(row.Name);
            var compactX = count > 1 ? 16 + i * 96 : 20;
            var columnWidth = Math.Max(1, (expandedWidth - padding * 2 - (count - 1) * gap) / Math.Max(1, count));
            var expandedX = padding + i * (columnWidth + gap);
            var x = offset + compactX + (expandedX - compactX) * expanded;
            var headerExpansion = expanded >= .99 && detailLayout.CompactHeader ? 0 : expanded;
            var headerY = detailLayout.CompactHeader ? 26 : 8;
            var y = 7.5f + (headerY - 7.5f) * expanded;
            var markSize = 19 + 4 * headerExpansion;
            if (expanded < .99 || detailLayout.HeaderVisible)
            {
                ProviderMark.Draw(g, row.Name, new(x, y, markSize, markSize), Ink);
                var valueX = x + (markSize + 7) * (1 - headerExpansion);
                var valueY = (y - 4) * (1 - headerExpansion) + 34 * headerExpansion;
                Draw(g, row.Value, headerExpansion is > 0 and < 1 ? animatedNumberFont : headerExpansion == 1 ? expandedNumberFont : numberFont, ink, new(valueX, valueY, 58, 29));
                if (row.Stale) { using var stale = new SolidBrush(ink); g.FillEllipse(stale, valueX + 54, valueY + 12, 4, 4); }
            }
            if (i > 0)
            {
                using var separator = new Pen(Color.FromArgb(110, Palette.Border));
                g.DrawLine(separator, x - 19, 9 + 9 * expanded, x - 19, 25 + Math.Max(0, height - 43) * expanded);
            }
            if (expanded <= .01) continue;
            Color Detail(Color color) => Color.FromArgb((int)(255 * Math.Clamp((expanded - .2) / .8, 0, 1)), color);
            if (detailLayout.NameY >= 0)
                Draw(g, row.Name == "Claude" ? "Claude Code" : row.Name, numberFont, Detail(Ink),
                    new(x + (detailLayout.CompactHeader ? 0 : 31), detailLayout.NameY + (detailLayout.CompactHeader ? 0 : 2), Math.Max(1, columnWidth - (detailLayout.CompactHeader ? 0 : 31)), 18));
            if (!detailLayout.CompactHeader && detailLayout.HeaderVisible)
                Draw(g, "remaining", bodyFont, Detail(Muted), new(x + 62, 37, Math.Max(1, columnWidth - 62), 23));
            if (!detailLayout.CombinedStatus)
            {
                if (row.Status != "Live") Draw(g, row.Status, bodyFont, Detail(ink), new(x, detailLayout.StatusY, columnWidth, 19));
                Draw(g, row.Observation, bodyFont, Detail(row.Stale ? Palette.Warning : Muted), new(x, detailLayout.ObservationY, columnWidth, 19));
            }
            if (detailLayout.CompactHeader)
            {
                continue;
            }
            using var track = new SolidBrush(Detail(Palette.Track));
            using var trackShape = DrawingHelpers.RoundedRectangle(new(x, 66, columnWidth, 5), 2.5f);
            g.FillPath(track, trackShape);
            if (row.Remaining is > 0)
            {
                using var fill = new SolidBrush(Detail(ink));
                using var bar = DrawingHelpers.RoundedRectangle(new(x, 66, Math.Max(1, (float)(columnWidth * row.Remaining / 100)), 5), 2.5f);
                g.FillPath(fill, bar);
            }
            if (row.Windows.Length > 0)
            {
                for (var detailIndex = 0; detailIndex < Math.Min(row.Windows.Length, detailLayout.VisibleRows); detailIndex++)
                {
                    var detail = row.Windows[detailIndex]; var detailY = detailLayout.AllowanceY + detailIndex * 54;
                    Draw(g, detail.Label, numberFont, Detail(Ink), new(x, detailY, columnWidth, 17));
                    Draw(g, detail.Value + " remaining", bodyFont, Detail(Ink), new(x, detailY + 17, columnWidth, 17));
                    Draw(g, detail.Reset, bodyFont, Detail(Muted), new(x, detailY + 34, columnWidth, 17));
                }
                continue;
            }
            if (detailLayout.Overflow)
            {
                continue;
            }
            Draw(g, row.Window, bodyFont, Detail(Muted), new(x, 78, columnWidth, 19));
            using var resetFormat = new StringFormat { Trimming = StringTrimming.EllipsisWord };
            using var resetBrush = new SolidBrush(Detail(Ink));
            g.DrawString(row.Reset, bodyFont, resetBrush, new RectangleF(x, 99, columnWidth, 33), resetFormat);
        }
        if (expanded >= .99 && detailLayout.Overflow)
        {
            var omitted = rows.Sum(r => Math.Max(0, r.Windows.Length - detailLayout.VisibleRows));
            var text = omitted > 0 ? $"Open Usage · {omitted} more" : "Open Usage";
            if (detailLayout.CombinedStatus)
            {
                var statuses = rows.Select(r => $"{r.Name}: {r.Status} · {r.Observation}").ToArray();
                if (statuses.Length > 0) text += " · " + string.Join(" · ", statuses);
            }
            using var footerFont = new Font("Segoe UI", Math.Min(12, Math.Max(1, detailLayout.FooterHeight - 3)), FontStyle.Regular, GraphicsUnit.Pixel);
            Draw(g, text, footerFont, rows.Any(r => r.Stale) && detailLayout.CombinedStatus ? Palette.Warning : Ink,
                new(padding, detailLayout.UsageY, Math.Max(1, width - padding * 2), detailLayout.FooterHeight));
        }
        return image;
    }
    private static void Draw(Graphics g, string text, Font font, Color color, RectangleF bounds)
    { using var brush = new SolidBrush(color); using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }; g.DrawString(text, font, brush, bounds, format); }
    internal void UpdateSurface()
    {
        if (drawing || IsDisposed || !IsHandleCreated) return; drawing = true;
        try
        {
            var next = CreatePreviewBitmap(); surface?.Dispose(); surface = next; RenderVersion++;
            if (!Visible) return;
            if (opaqueFallback) { Invalidate(); return; }
            try { LayeredWindowSurface.Present(Handle, Location, surface, (byte)Math.Round(255 * opacity)); }
            catch (Win32Exception) { opaqueFallback = true; RecreateHandle(); Invalidate(); SurfaceFallbackUsed?.Invoke(); }
        }
        finally { drawing = false; }
    }
    protected override void OnPaint(PaintEventArgs e) { if (opaqueFallback && surface is not null) e.Graphics.DrawImageUnscaled(surface, Point.Empty); }
    protected override AccessibleObject CreateAccessibilityInstance() => new MonitorAccessibleObject(this);
    private sealed class MonitorAccessibleObject(MonitorForm owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.IsDisposed ? 0 : owner.rows.Length;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < GetChildCount() ? new RowAccessibleObject(owner, this, owner.rows[index].Name) : null;
    }
    private sealed class RowAccessibleObject(MonitorForm owner, AccessibleObject parent, string providerName) : AccessibleObject
    {
        // Assistive clients can retain children after the provider rows change.
        // Resolve by identity so an old child cannot read another provider or an
        // out-of-range slot, and never retain a removed provider's old reading.
        private MonitorRow? Current => owner.IsDisposed ? null : owner.rows.FirstOrDefault(row =>
            string.Equals(row.Name, providerName, StringComparison.OrdinalIgnoreCase) ||
            UsagePresentation.IsClaude(row.Name) && UsagePresentation.IsClaude(providerName));
        public override string? Name
        {
            get
            {
                var row = Current;
                if (row is null) return UsagePresentation.IsClaude(providerName) ? "Claude Code" : providerName;
                var hint = row.Hint;
                if (!owner.hovered || !owner.ExpandedLayout.Overflow) return hint;
                var omitted = Math.Max(0, row.Windows.Length - owner.ExpandedLayout.VisibleRows);
                return hint + $"\nOpen Usage for all limits ({omitted} more). Click, Enter or Control+1 opens Usage.";
            }
            set { }
        }
        public override string? Value => Current?.Value;
        public override AccessibleRole Role => AccessibleRole.StaticText;
        public override AccessibleStates State => AccessibleStates.ReadOnly |
            (Current is null ? AccessibleStates.Unavailable | AccessibleStates.Offscreen : AccessibleStates.None);
        public override AccessibleObject? Parent => parent;
    }
    private sealed record MonitorDetail(string Label, string Value, string Reset);
    private sealed record MonitorRow(string Name, string Value, double? Remaining, bool Stale, string Status, string Reset, string Observation, string Window, string Hint, string Details, MonitorDetail[] Windows)
    { public string Visual(bool expanded) => $"{Name}|{Value}|{Remaining}|{Stale}" + (expanded ? $"|{Status}|{Reset}|{Observation}|{Details}" : ""); }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) { transition.Dispose(); hoverDelay.Dispose(); animation.Dispose(); menu.Dispose(); surface?.Dispose(); bodyFont.Dispose(); numberFont.Dispose(); expandedNumberFont.Dispose(); } }
}
