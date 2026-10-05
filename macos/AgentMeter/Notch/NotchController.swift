import AppKit
import CoreGraphics
import QuartzCore
import SwiftUI

private final class MonitorPanel: NSPanel {
  override var canBecomeKey: Bool { false }
  override var canBecomeMain: Bool { false }
}
final class TrackingSurface: NSView {
  var appearanceChanged: () -> Void = {}
  var allowanceAccessibility: () -> String = { "" }
  override func viewDidChangeEffectiveAppearance() {
    super.viewDidChangeEffectiveAppearance()
    appearanceChanged()
  }
  override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
  var hover: (Bool) -> Void = { _ in }
  var clicked: () -> Void = {}
  var pressed: (CGPoint) -> Void = { _ in }
  var dragStarted: () -> Void = {}
  var dragged: (CGPoint) -> Void = { _ in }
  var released: (Bool) -> Void = { _ in }
  private var gesture: MonitorDragGesture?
  private var tracking: NSTrackingArea?
  private var forwardingScroll = false
  override func updateTrackingAreas() {
    super.updateTrackingAreas()
    if let tracking { removeTrackingArea(tracking) }
    tracking = NSTrackingArea(
      rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self)
    addTrackingArea(tracking!)
  }
  override func hitTest(_ point: NSPoint) -> NSView? {
    bounds.contains(convert(point, from: superview)) ? self : nil
  }
  override func scrollWheel(with event: NSEvent) {
    guard !forwardingScroll else { super.scrollWheel(with: event); return }
    let point = convert(event.locationInWindow, from: nil)
    if let target = super.hitTest(point), target !== self {
      forwardingScroll = true
      defer { forwardingScroll = false }
      target.scrollWheel(with: event)
    } else {
      super.scrollWheel(with: event)
    }
  }
  override func mouseEntered(with event: NSEvent) { hover(true) }
  override func mouseExited(with event: NSEvent) { hover(false) }
  private func screenPoint(_ event: NSEvent) -> CGPoint {
    window?.convertPoint(toScreen: event.locationInWindow) ?? NSEvent.mouseLocation
  }
  override func mouseDown(with event: NSEvent) {
    let point = screenPoint(event)
    gesture = MonitorDragGesture(origin: point)
    pressed(point)
  }
  override func mouseDragged(with event: NSEvent) { movePointer(to: screenPoint(event)) }
  private func movePointer(to point: CGPoint) {
    guard var gesture else { return }
    let wasDragging = gesture.isDragging
    gesture.move(to: point)
    self.gesture = gesture
    if !wasDragging && gesture.isDragging { dragStarted() }
    if gesture.isDragging { dragged(point) }
  }
  override func mouseUp(with event: NSEvent) {
    guard gesture != nil else { return }
    movePointer(to: screenPoint(event))
    let wasDragging = gesture?.isDragging ?? false
    gesture = nil
    released(wasDragging)
    if !wasDragging { clicked() }
  }
  override func accessibilityPerformPress() -> Bool {
    clicked()
    return true
  }
  override func accessibilityLabel() -> String? { allowanceAccessibility() }
}
@MainActor final class NotchController {
  private let panel = MonitorPanel(
    contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered,
    defer: false)
  private let presentation = NotchPresentation()
  private let surface = TrackingSurface()
  private let material = NSVisualEffectView()
  private let tint = NSView()
  private var observers: [NSObjectProtocol] = []
  private var hoverWork: DispatchWorkItem?
  private var generation = 0
  private var click: () -> Void = {}
  private let defaults: UserDefaults
  private var anchor: MonitorAnchor?
  private var pressLocation: CGPoint?
  private var dragStartFrame: CGRect?
  private var dragging = false
  private var dragFrontmost: pid_t?
  private(set) var focusPreserved = true
  var text: String {
    presentation.rows.map { $0.id.title + " " + $0.percentage }.joined(separator: " | ")
  }
  var isVisible: Bool { panel.isVisible }
  var onActiveSpace: Bool { panel.isOnActiveSpace }
  var onScreen: Bool { panel.occlusionState.contains(.visible) }
  var frame: NSRect { panel.frame }
  var phase: String { presentation.state.phase.rawValue }
  var effectiveAppearance: NSAppearance { panel.effectiveAppearance }
  func applyAppearance(_ appearance: AppAppearance) {
    panel.appearance = appearance.native
    updateMaterial()
  }
  init(defaults: UserDefaults = .standard, open: @escaping () -> Void = {}) {
    self.defaults = defaults
    anchor = MonitorAnchor.load(from: defaults)
    click = open
    panel.isOpaque = false
    panel.backgroundColor = .clear
    panel.hidesOnDeactivate = false
    panel.level = .statusBar
    panel.hasShadow = true
    panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    if #available(macOS 15.0, *) { panel.collectionBehavior.insert(.canJoinAllApplications) }
    surface.wantsLayer = true
    surface.layer?.masksToBounds = true
    surface.setAccessibilityElement(true)
    surface.setAccessibilityRole(.button)
    surface.allowanceAccessibility = { [weak self] in
      guard let self else { return "Allowance details" }
      return self.presentation.rows.map(\.accessibility).joined(separator: ". ")
        + ". Open allowance details"
    }
    surface.clicked = { [weak self] in self?.openFromClick() }
    surface.hover = { [weak self] inside in self?.scheduleHover(inside) }
    surface.pressed = { [weak self] point in self?.press(at: point) }
    surface.dragStarted = { [weak self] in self?.startDrag() }
    surface.dragged = { [weak self] point in self?.drag(to: point) }
    surface.released = { [weak self] didDrag in self?.release(didDrag: didDrag) }
    material.material = .hudWindow
    material.blendingMode = .behindWindow
    material.state = .active
    material.autoresizingMask = [.width, .height]
    surface.addSubview(material)
    tint.wantsLayer = true
    tint.autoresizingMask = [.width, .height]
    surface.addSubview(tint)
    let host = NSHostingView(rootView: NotchView(model: presentation))
    host.autoresizingMask = [.width, .height]
    surface.addSubview(host)
    panel.contentView = surface
    surface.appearanceChanged = { [weak self] in self?.updateMaterial() }
    updateMaterial()
    observers.append(
      NotificationCenter.default.addObserver(
        forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
      ) { [weak self] _ in MainActor.assumeIsolated { self?.place() } })
    observers.append(
      NSWorkspace.shared.notificationCenter.addObserver(
        forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification, object: nil,
        queue: .main
      ) { [weak self] _ in MainActor.assumeIsolated { self?.updateMaterial() } })
  }
  private func openFromClick() {
    Diagnostics.shared.record("notch-click")
    click()
  }
  private func press(at point: CGPoint) {
    hoverWork?.cancel()
    pressLocation = point
    generation += 1
    // Replace active AppKit animations before direct pointer movement.
    let current = panel.frame
    NSAnimationContext.runAnimationGroup { context in
      context.duration = 0
      panel.animator().setFrame(current, display: true)
      panel.animator().alphaValue = 1
    }
    dragStartFrame = current
    dragFrontmost = NSWorkspace.shared.frontmostApplication?.processIdentifier
  }
  private func startDrag() {
    dragging = true
    hoverWork?.cancel()
  }
  private func drag(to point: CGPoint) {
    guard dragging, let start = dragStartFrame, let press = pressLocation,
      let display = MonitorGeometry.display(at: point, among: displays)
    else { return }
    let frame = MonitorGeometry.draggedFrame(
      start: start, pressed: press, current: point, visible: display.visible)
    panel.setFrame(frame, display: true)
    anchor = MonitorGeometry.anchor(frame: frame, on: display)
    focusPreserved = focusPreserved
      && dragFrontmost == NSWorkspace.shared.frontmostApplication?.processIdentifier
      && !panel.isKeyWindow && !panel.isMainWindow
  }
  private func release(didDrag: Bool) {
    if didDrag { anchor?.save(to: defaults) }
    pressLocation = nil
    dragStartFrame = nil
    dragging = false
    dragFrontmost = nil
    transition(immediate: true)
    scheduleHover(panel.frame.contains(NSEvent.mouseLocation))
  }
  func resetPosition() {
    anchor = nil
    defaults.removeObject(forKey: MonitorAnchor.defaultsKey)
    place()
  }
  private func updateMaterial() {
    let opaque = NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency
    material.isHidden = opaque
    surface.effectiveAppearance.performAsCurrentDrawingAppearance {
      tint.layer?.backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(opaque ? 1 : 0.78).cgColor
      surface.layer?.backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(opaque ? 1 : 0.65).cgColor
      surface.layer?.borderColor = NSColor.separatorColor.cgColor
    }
    surface.layer?.borderWidth =
      NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast ? 1 : 0.5
  }
  func update(_ model: Presentation) {
    applyAppearance(model.preferences.appearance)
    let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
    let old = presentation.state
    var activity = model.activity
    if !model.preferences.enabledProviders.contains(.codex) { activity.codex = SurfaceActivity() }
    if !model.preferences.enabledProviders.contains(.claude) { activity.claude = SurfaceActivity() }
    presentation.state.reconcile(activity: activity, enabled: model.preferences.notchEnabled)
    presentation.rows = presentation.state.providers.map {
      ProviderGlance(snapshot: model.usage[$0] ?? UsageSnapshot(provider: $0))
    }
    if old != presentation.state {
      transition(immediate: !model.preferences.notchEnabled)
    } else if presentation.state.phase != .hidden {
      place()
    }
    focusPreserved =
      focusPreserved && front == NSWorkspace.shared.frontmostApplication?.processIdentifier
      && !panel.isKeyWindow && !panel.isMainWindow
  }
  private func scheduleHover(_ inside: Bool) {
    hoverWork?.cancel()
    guard pressLocation == nil else { return }
    let work = DispatchWorkItem { [weak self] in
      guard let self, self.pressLocation == nil, self.presentation.state.phase != .hidden else { return }
      if !inside && self.panel.frame.contains(NSEvent.mouseLocation) { return }
      self.setHover(inside)
    }
    hoverWork = work
    DispatchQueue.main.asyncAfter(deadline: .now() + (inside ? 0.12 : 0.10), execute: work)
  }
  func setHover(_ inside: Bool) {
    guard pressLocation == nil else { return }
    let old = presentation.state
    presentation.state.hover(inside)
    if old != presentation.state { transition() }
  }
  private func displayID(_ screen: NSScreen) -> UInt32 {
    (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? UInt32) ?? 0
  }
  private var displays: [MonitorDisplay] {
    NSScreen.screens.map {
      MonitorDisplay(id: displayID($0), frame: $0.frame, visible: $0.visibleFrame)
    }
  }
  private func targetFrame() -> NSRect? {
    let screens = NSScreen.screens
    let builtIn = screens.first {
      CGDisplayIsBuiltin(
        ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? UInt32) ?? 0) != 0
    }
    let selected: NSScreen?
    if let anchor,
      let display = MonitorGeometry.display(
        for: anchor, among: displays, fallbackID: NSScreen.main.map(displayID)) {
      selected = screens.first { displayID($0) == display.id }
    } else {
      selected = builtIn ?? NSScreen.main ?? screens.first
    }
    guard let screen = selected else { return nil }
    let expanded = presentation.state.phase == .expanded
    let measured = presentation.rows.reduce(CGFloat(0)) { total, row in
      total
        + (row.compactText as NSString).size(withAttributes: [
          .font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold)
        ]).width + 26 + (row.snapshot.state == .stale ? 11 : 0)
    }
    let width = min(
      screen.visibleFrame.width,
      expanded
        ? (presentation.rows.count > 1 ? 540 : 292)
        : max(112, measured + 40 + CGFloat(max(0, presentation.rows.count - 1)) * 25))
    let height: CGFloat = expanded
      ? NotchDetailLayout.height(rows: presentation.rows, availableHeight: screen.visibleFrame.height)
      : 34
    if let anchor {
      return MonitorGeometry.frame(
        size: CGSize(width: width, height: height), anchor: anchor, visible: screen.visibleFrame)
    }
    // Preserve Phase 1's screen/visible-area anchor. Presentation alone changes size.
    let anchor = MonitorGeometry.frame(
      screen: screen.frame, visible: screen.visibleFrame, safeTop: screen.safeAreaInsets.top,
      contentWidth: width)
    let top = anchor.maxY + 2
    return MonitorGeometry.clamped(NSRect(
      x: min(
        max(screen.frame.midX - width / 2, screen.visibleFrame.minX),
        screen.visibleFrame.maxX - width), y: max(screen.visibleFrame.minY, top - height),
      width: width, height: height), to: screen.visibleFrame)
  }
  private func transition(immediate: Bool = false) {
    guard pressLocation == nil else { return }
    Diagnostics.shared.record("notch", code: presentation.state.phase.rawValue)
    generation += 1
    let current = generation
    let hidden = presentation.state.phase == .hidden
    hoverWork?.cancel()
    if hidden && immediate {
      panel.orderOut(nil)
      return
    }
    guard let target = targetFrame() else {
      panel.orderOut(nil)
      return
    }
    let reduce = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
    if !hidden && !panel.isVisible {
      panel.alphaValue = 0
      panel.setFrame(
        reduce
          ? target
          : NSRect(
            x: target.midX - target.width * 0.45, y: target.maxY - 4, width: target.width * 0.9,
            height: 4), display: false)
      panel.orderFrontRegardless()
    }
    surface.layer?.cornerRadius = presentation.state.phase == .expanded ? 18 : 17
    NSAnimationContext.runAnimationGroup { context in
      context.duration = immediate ? 0 : (reduce ? 0.1 : (hidden ? 0.14 : 0.24))
      context.timingFunction = CAMediaTimingFunction(name: .easeOut)
      if hidden {
        panel.animator().alphaValue = 0
        if !reduce {
          panel.animator().setFrame(
            NSRect(
              x: panel.frame.midX - panel.frame.width * 0.45, y: panel.frame.maxY - 3,
              width: panel.frame.width * 0.9, height: 3), display: true)
        }
      } else {
        panel.animator().alphaValue = 1
        panel.animator().setFrame(target, display: true)
      }
    } completionHandler: { [weak self] in
      MainActor.assumeIsolated {
        guard let self, self.generation == current else { return }
        if hidden { self.panel.orderOut(nil) }
      }
    }
  }
  func place() {
    guard pressLocation == nil else { return }
    if presentation.state.phase != .hidden, let frame = targetFrame() {
      panel.setFrame(frame, display: true)
    }
  }
  func close() {
    hoverWork?.cancel()
    generation += 1
    for observer in observers {
      NotificationCenter.default.removeObserver(observer)
      NSWorkspace.shared.notificationCenter.removeObserver(observer)
    }
    panel.orderOut(nil)
    panel.close()
  }
  #if DEBUG
    func clickForValidation() { _ = surface.accessibilityPerformPress() }
    func capture(to url: URL) {
      guard let bitmap = surface.bitmapImageRepForCachingDisplay(in: surface.bounds) else { return }
      surface.cacheDisplay(in: surface.bounds, to: bitmap)
      try? bitmap.representation(using: .png, properties: [:])?.write(to: url)
    }
  #endif
}
