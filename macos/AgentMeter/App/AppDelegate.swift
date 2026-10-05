import AppKit
import SwiftUI

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation {
  let model = Presentation()
  private var store: UsageStore!
  private var activity: ActivityMonitor!
  private var notch: NotchController!
  private var window: NSWindow!
  private var setupWindow: NSWindow?
  private var updateWindow: NSWindow?
  private lazy var setup = SetupFlow(preferences: model.preferences)
  private var status: NSStatusItem!
  private var schedule: Task<Void, Never>?
  private var wake: Task<Void, Never>?
  private var observers: [NSObjectProtocol] = []
  private var quitting = false
  private lazy var updates = UpdaterCoordinator(presentation: model.updates)
  private var appliedProviders: Set<ProviderID>?
  func applicationDidFinishLaunching(_ notification: Notification) {
    DistributedNotificationCenter.default().addObserver(
      self, selector: #selector(reopenMain), name: InstanceLease.reopen, object: nil)
    Diagnostics.shared.record("launch")
    createApplicationMenu()
    model.updates.showPromptAction = { [weak self] in self?.showUpdatePrompt() }
    model.updates.hidePromptAction = { [weak self] in self?.updateWindow?.orderOut(nil) }
    startUpdaterIfReady()
    let discovery = ProviderDiscovery()
    activity = ActivityMonitor { [weak self] snapshot in
      guard let self, !self.quitting else { return }
      let previous = self.model.activity
      self.model.activity = snapshot
      self.notch?.update(self.model)
      if previous != snapshot {
        Diagnostics.shared.record("activity", code: snapshot.name)
        self.validationEvent("activity")
      }
      for p in snapshot.providers where !previous[p].active {
        Task { await self.store?.refresh(p, onlyIfOlderThan: 15) }
      }
    }
    let discovered: @Sendable (ProviderID, Installation) -> Void = { [weak self] p, install in
      Task { @MainActor in
        guard let self, !self.quitting, self.model.preferences.isEnabled(p) else { return }
        self.model.installations[p] = install
        self.activity.update(p, install)
      }
    }
    store = UsageStore(sources: [
      .codex: ProviderAdapter(provider: .codex, discovery: discovery, discovered: discovered),
      .claude: ProviderAdapter(provider: .claude, discovery: discovery, discovered: discovered),
    ], enabledProviders: model.preferences.enabledProviders) { [weak self] snapshot in
      Task { @MainActor in
        guard let self, !self.quitting else { return }
        self.model.usage = snapshot
        self.notch.update(self.model)
        self.validationEvent("usage")
      }
    }
    model.refreshAction = { [weak self] in self?.manualRefresh() }
    model.setupRetryAction = { [weak self] provider in self?.performManualRefresh(provider) }
    notch = NotchController(open: { [weak self] in self?.openMain() })
    model.resetNotchPositionAction = { [weak self] in self?.notch.resetPosition() }
    model.preferences.changed = { [weak self] in self?.applyPreferences() }
    applyPreferences()
    let event = NSAppleEventManager.shared().currentAppleEvent
    let loginLaunch =
      event?.eventID == kAEOpenApplication
      && (event?.paramDescriptor(forKeyword: keyAELaunchedAsLogInItem) != nil
        || event?.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue
          == keyAELaunchedAsLogInItem)
    if setup.needsAutomaticSetup { openSetup() }
    else if !loginLaunch { openMain() }
    activity.start()
    refresh()
    schedule = Task { [weak self] in
      while !Task.isCancelled {
        do { try await Task.sleep(for: .seconds(30)) } catch { break }
        self?.refresh()
      }
    }
    observers.append(
      NSWorkspace.shared.notificationCenter.addObserver(
        forName: NSWorkspace.willSleepNotification, object: nil, queue: .main
      ) { [weak self] _ in MainActor.assumeIsolated { self?.sleep() } })
    observers.append(
      NSWorkspace.shared.notificationCenter.addObserver(
        forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
      ) { [weak self] _ in MainActor.assumeIsolated { self?.didWake() } })
    startValidationIfRequested()
  }
  func applicationDidBecomeActive(_ notification: Notification) { model.loginItem.synchronize() }
  private func startUpdaterIfReady() {
    updates.startIfReady(setupComplete: !setup.needsAutomaticSetup)
  }
  private func showUpdatePrompt() {
    guard !quitting else { return }
    if updateWindow == nil {
      let view = NSHostingView(rootView: UpdatePromptView(updates: model.updates))
      let dialog = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 536, height: 230),
        styleMask: [.titled, .closable], backing: .buffered, defer: false)
      dialog.title = "Llumi"
      dialog.isReleasedWhenClosed = false
      dialog.delegate = self
      dialog.contentView = view
      dialog.center()
      updateWindow = dialog
    }
    updateWindow?.makeKeyAndOrderFront(nil)
    NSApp.activate(ignoringOtherApps: true)
  }
  @objc private func checkForUpdates() { updates.checkForUpdates() }
  func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
    menuItem.action != #selector(checkForUpdates) || model.updates.canCheck
  }
  private func createApplicationMenu() {
    let main = NSMenu()
    let appItem = NSMenuItem(title: "Llumi", action: nil, keyEquivalent: "")
    let appMenu = NSMenu(title: "Llumi")
    let update = NSMenuItem(title: "Check for Updates…",
      action: #selector(checkForUpdates), keyEquivalent: "")
    update.target = self
    appMenu.addItem(update)
    appMenu.addItem(.separator())
    for (title, action, key) in [
      ("Settings…", #selector(openSettings), ","), ("Quit Llumi", #selector(quit), "q"),
    ] {
      let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
      item.target = self
      appMenu.addItem(item)
    }
    appItem.submenu = appMenu
    main.addItem(appItem)
    let windowItem = NSMenuItem(title: "Window", action: nil, keyEquivalent: "")
    let windowMenu = NSMenu(title: "Window")
    let open = NSMenuItem(title: "Open Llumi", action: #selector(openMain), keyEquivalent: "0")
    open.target = self
    windowMenu.addItem(open)
    let details = NSMenuItem(title: "Allowance Details", action: #selector(openMain), keyEquivalent: "1")
    details.target = self
    windowMenu.addItem(details)
    windowMenu.addItem(
      NSMenuItem(title: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w"))
    windowMenu.addItem(
      NSMenuItem(
        title: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"))
    windowItem.submenu = windowMenu
    main.addItem(windowItem)
    let helpItem = NSMenuItem(title: "Help", action: nil, keyEquivalent: "")
    let helpMenu = NSMenu(title: "Help")
    let setupItem = NSMenuItem(title: "Setup Llumi…", action: #selector(openSetup), keyEquivalent: "")
    setupItem.target = self
    helpMenu.addItem(setupItem)
    let checkItem = NSMenuItem(title: "Check Setup…", action: #selector(openCheckSetup), keyEquivalent: "")
    checkItem.target = self
    helpMenu.addItem(checkItem)
    helpItem.submenu = helpMenu
    main.addItem(helpItem)
    NSApp.mainMenu = main
    NSApp.windowsMenu = windowMenu
    NSApp.helpMenu = helpMenu
  }
  private func createMenu() {
    guard status == nil else { return }
    status = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
      NSColor.black.setStroke()
      NSColor.black.setFill()
      let arc = NSBezierPath()
      arc.move(to: NSPoint(x: 2, y: 5))
      arc.curve(to: NSPoint(x: 16, y: 5), controlPoint1: NSPoint(x: 2, y: 16), controlPoint2: NSPoint(x: 16, y: 16))
      arc.lineWidth = 2.2
      arc.lineCapStyle = .round
      arc.stroke()
      let needle = NSBezierPath()
      needle.move(to: NSPoint(x: 8, y: 6))
      needle.line(to: NSPoint(x: 13.5, y: 12))
      needle.line(to: NSPoint(x: 10, y: 4.5))
      needle.close()
      needle.fill()
      NSBezierPath(ovalIn: NSRect(x: 7.5, y: 4, width: 3.5, height: 3.5)).fill()
      return true
    }
    image.isTemplate = true
    status.button?.image = image
    status.button?.toolTip = "Llumi"
    let menu = NSMenu()
    for (title, selector, key) in [
      ("Open Llumi", #selector(openMain), ""), ("Refresh", #selector(manualRefresh), "r"),
      ("Settings…", #selector(openSettings), ","),
      ("Quit", #selector(quit), "q"),
    ] {
      if title == "Settings…" { menu.addItem(.separator()) }
      let item = NSMenuItem(title: title, action: selector, keyEquivalent: key)
      item.target = self
      menu.addItem(item)
    }
    status.menu = menu
  }
  @objc func openMain() {
    guard !quitting else { return }
    let newlyVisible = window?.isVisible != true
    if window == nil {
      window = NSWindow(
        contentRect: NSRect(x: 0, y: 0, width: 850, height: 560),
        styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered,
        defer: false)
      window.title = "Llumi"
      window.minSize = NSSize(width: 630, height: 470)
      window.titlebarAppearsTransparent = true
      window.isReleasedWhenClosed = false
      window.delegate = self
      window.contentView = NSHostingView(rootView: MainView(model: model))
      window.center()
    }
    model.destination = .usage
    window.deminiaturize(nil)
    window.makeKeyAndOrderFront(nil)
    NSApp.activate(ignoringOtherApps: true)
    if newlyVisible { updates.foregroundOpened() }
    Diagnostics.shared.record("window-open")
  }
  private func applyPreferences() {
    NSApp.appearance = model.preferences.appearance.native
    if model.preferences.menuEnabled {
      createMenu()
    } else if let status {
      NSStatusBar.system.removeStatusItem(status)
      self.status = nil
    }
    notch?.update(model)
    let enabled = model.preferences.enabledProviders
    if appliedProviders != enabled {
      appliedProviders = enabled
      activity?.setEnabledProviders(enabled)
      Task { [weak self] in await self?.store?.setEnabledProviders(enabled) }
    }
  }
  @objc func openSetup() {
    guard !quitting else { return }
    if setupWindow == nil {
      let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 610),
        styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
      w.title = "Setup Llumi"
      w.appearance = NSAppearance(named: .aqua)
      w.isReleasedWhenClosed = false
      w.delegate = self
      w.contentView = NSHostingView(rootView: SetupView(model: model, flow: setup) { [weak self] in
        self?.setupWindow?.orderOut(nil)
        self?.startUpdaterIfReady()
        self?.openMain()
      })
      w.center()
      setupWindow = w
    }
    if setupWindow?.isVisible != true { setup.reopen() }
    setupWindow?.deminiaturize(nil)
    setupWindow?.makeKeyAndOrderFront(nil)
    NSApp.activate(ignoringOtherApps: true)
  }
  @objc func openCheckSetup() {
    guard !quitting else { return }
    openSettings()
    model.showSetupChecks()
    manualRefresh()
  }
  @objc func openSettings() {
    openMain()
    model.destination = .settings
  }
  @objc func manualRefresh() {
    performManualRefresh(nil)
  }
  private func performManualRefresh(_ provider: ProviderID?) {
    guard !quitting, !model.manuallyRefreshing else { return }
    model.manuallyRefreshing = true
    Task {
      await store.refresh(provider, intent: .userInitiated)
      await store.waitForIdle()
      model.manuallyRefreshing = false
    }
  }
  @objc func refresh() {
    guard !quitting else { return }
    Task { await store.refresh() }
  }
  @objc func quit() { NSApp.terminate(nil) }
  func windowWillClose(_ notification: Notification) { Diagnostics.shared.record("window-close") }
  func windowShouldClose(_ sender: NSWindow) -> Bool {
    if sender === updateWindow { model.updates.dismissPrompt() }
    return true
  }
  func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
  func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
    reopenMain()
    return true
  }
  @objc private func reopenMain() {
    let alreadyVisible = window?.isVisible == true
    openMain()
    if alreadyVisible { updates.foregroundOpened() }
  }
  func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
    guard !quitting else { return .terminateLater }
    quitting = true
    schedule?.cancel()
    wake?.cancel()
    activity.stop()
    notch.close()
    if let status { NSStatusBar.system.removeStatusItem(status) }
    status = nil
    window?.orderOut(nil)
    setupWindow?.orderOut(nil)
    updateWindow?.orderOut(nil)
    let service = store!
    Task.detached {
      await service.stop()
      Diagnostics.shared.record("quit-clean")
      RunLoop.main.perform(inModes: [.default, .modalPanel, .eventTracking]) {
        MainActor.assumeIsolated {
          self.validationEvent("quit-clean")
          sender.reply(toApplicationShouldTerminate: true)
        }
      }
    }
    return .terminateLater
  }
  private func sleep() {
    wake?.cancel()
    Task { await store.suspend() }
    Diagnostics.shared.record("sleep")
  }
  private func didWake() {
    wake?.cancel()
    wake = Task { [weak self] in
      do { try await Task.sleep(for: .seconds(1)) } catch { return }
      guard let self, !self.quitting else { return }
      self.notch.place()
      self.activity.reconcile()
      await self.store.resume()
      Diagnostics.shared.record("wake")
    }
  }
  // Debug-only acceptance instrumentation. No command channel is present in Release.
  private var validation = false
  func validationEvent(_ event: String) {
    #if DEBUG
      guard validation else { return }
      let usage: [String: Any] = Dictionary(
        uniqueKeysWithValues: ProviderID.allCases.map { p in
          let s = model.usage[p] ?? UsageSnapshot(provider: p)
          return (
            p.rawValue,
            [
              "status": s.state.rawValue, "version": model.installations[p]?.version ?? "",
              "windows": s.reading?.windows.map { w -> [String: Any] in
                [
                  "id": w.id, "remaining": w.remaining as Any? ?? NSNull(),
                  "reset": w.reset?.timeIntervalSince1970 as Any? ?? NSNull(),
                  "minutes": w.durationMinutes as Any? ?? NSNull(),
                ]
              } ?? [],
            ] as [String: Any]
          )
        })
      let value: [String: Any] = [
        "event": event, "time": Date().timeIntervalSince1970, "pid": getpid(),
        "activity": model.activity.name, "cliCodex": model.activity.codex.cli,
        "cliClaude": model.activity.claude.cli, "desktopCodex": model.activity.codex.desktop,
        "desktopClaude": model.activity.claude.desktop, "notch": notch?.isVisible ?? false,
        "notchText": notch?.text ?? "", "notchPhase": notch?.phase ?? "hidden",
        "notchFrame": notch.map { NSStringFromRect($0.frame) } ?? "",
        "destination": model.destination?.rawValue ?? "",
        "appearance": model.preferences.appearance.rawValue,
        "loginEnabled": model.loginItem.enabled, "loginMessage": model.loginItem.message ?? "",
        "notchEnabled": model.preferences.notchEnabled,
        "focusPreserved": notch?.focusPreserved ?? true,
        "window": window?.isVisible ?? false, "windowNumber": window?.windowNumber ?? 0,
        "menuItem": status?.button != nil,
        "mainFullscreen": window?.styleMask.contains(.fullScreen) ?? false,
        "notchOnActiveSpace": notch?.onActiveSpace ?? false,
        "notchOnScreen": notch?.onScreen ?? false, "usage": usage,
      ]
      if let d = try? JSONSerialization.data(withJSONObject: value, options: .sortedKeys) {
        print(String(decoding: d, as: UTF8.self))
        fflush(stdout)
      }
    #endif
  }
  private func startValidationIfRequested() {
    #if DEBUG
      let args = CommandLine.arguments
      guard let i = args.firstIndex(of: "--validation-script"), args.indices.contains(i + 1) else {
        return
      }
      let url = URL(fileURLWithPath: args[i + 1]).resolvingSymlinksInPath()
      let root =
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Llumi/Phase1Test").path + "/"
      guard url.path.hasPrefix(root),
        let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size < 32768,
        let data = try? Data(contentsOf: url), let commands = try? J.parse(data).array
      else { return }
      validation = true
      validationEvent("ready")
      for item in commands {
        guard let seconds = item["at"].number, seconds >= 0, seconds <= 900,
          let command = item["command"].string
        else { continue }
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [weak self] in
          guard let self, !self.quitting else { return }
          switch command {
          case "menu-open": self.status?.menu?.performActionForItem(at: 0)
          case "menu-refresh": self.status?.menu?.performActionForItem(at: 1)
          case "menu-quit": self.status?.menu?.performActionForItem(at: 4)
          case "open": self.openMain()
          case "setup": self.openSetup()
          case "setup-next": self.setup.next()
          case "setup-back": self.setup.back()
          case "fixture-usage": self.applyUsageFixture()
          case "settings": self.openSettings()
          case "login-on": self.model.loginItem.setEnabled(true)
          case "login-off": self.model.loginItem.setEnabled(false)
          case "hover": self.notch.setHover(true)
          case "click-notch": self.notch.clickForValidation()
          case "narrow": self.window.setContentSize(NSSize(width: 640, height: 600))
          case "wide": self.window.setContentSize(NSSize(width: 850, height: 560))
          case "unhover": self.notch.setHover(false)
          case "notch-off": self.model.preferences.notchEnabled = false
          case "notch-on": self.model.preferences.notchEnabled = true
          case "menu-off": self.model.preferences.menuEnabled = false
          case "menu-on": self.model.preferences.menuEnabled = true
          case "light": self.model.preferences.appearance = .light
          case "dark": self.model.preferences.appearance = .dark
          case "system": self.model.preferences.appearance = .system
          case "capture":
            if let name = item["name"].string,
              name.range(of: "^[a-z0-9-]+$", options: .regularExpression) != nil
            {
              let file = URL(fileURLWithPath: root).appendingPathComponent(name + ".png")
              if item["surface"].string == "notch" {
                self.notch.capture(to: file)
              } else if let view = (item["surface"].string == "setup"
                ? self.setupWindow?.contentView : self.window?.contentView),
                let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds)
              {
                view.cacheDisplay(in: view.bounds, to: bitmap)
                try? bitmap.representation(using: .png, properties: [:])?.write(to: file)
              }
            }
          case "close": self.window.performClose(nil)
          case "refresh": self.refresh()
          case "quit": self.quit()
          case "activate":
            let allowed = [
              "com.apple.Safari", "com.apple.Terminal", "com.openai.codex",
              "com.anthropic.claudefordesktop",
            ]
            if let bundle = item["bundle"].string, allowed.contains(bundle) {
              NSWorkspace.shared.runningApplications.first { $0.bundleIdentifier == bundle }?
                .activate(options: [])
            }
          case "fullscreen": self.window.toggleFullScreen(nil)
          case "wake": self.didWake()
          default: break
          }
          self.validationEvent(command)
        }
      }
    #endif
  }
  #if DEBUG
    private func applyUsageFixture() {
      Task { [weak self] in
        guard let self else { return }
        await self.store.suspend()
        let now = Date()
        for provider in ProviderID.allCases {
          let short = try! UsageWindow(id: "five_hour", bucket: provider.rawValue, label: "5-hour",
            durationMinutes: 300, used: provider == .codex ? 24 : 18,
            reset: now.addingTimeInterval(7_200))
          let weekly = try! UsageWindow(id: "seven_day", bucket: provider.rawValue, label: "Weekly",
            durationMinutes: 10_080, used: provider == .codex ? 37 : 42,
            reset: now.addingTimeInterval(345_600))
          var windows = [short, weekly]
          if provider == .claude {
            windows.append(try! UsageWindow(id: "seven_day_sonnet", bucket: "sonnet", label: "Sonnet",
              durationMinutes: 10_080, used: 12, reset: now.addingTimeInterval(345_600)))
          }
          var snapshot = UsageSnapshot(provider: provider)
          snapshot.apply(.success(.init(binding: "synthetic-preview", windows: windows, date: now)))
          self.model.usage[provider] = snapshot
        }
        self.notch.update(self.model)
        self.validationEvent("fixture-usage")
      }
    }
  #endif
}
