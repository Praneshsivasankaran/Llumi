import AppKit
import Foundation
import XCTest

@MainActor final class SetupTests: XCTestCase {
  private func suite(_ body: (UserDefaults) -> Void) {
    let name = "AgentMeter-setup-" + UUID().uuidString
    let d = UserDefaults(suiteName: name)!
    defer { d.removePersistentDomain(forName: name) }
    body(d)
  }
  func testFreshInstallShowsSetupEvenAfterEmptyMigration() {
    suite { d in
      BetaPreferences.migrate(from: [:], to: d)
      SetupCompletion.recognizeExisting(defaults: d, old: [:])
      XCTAssertTrue(SetupFlow(defaults: d).needsAutomaticSetup)
    }
  }
  func testCompletionPersistsOneBooleanOnly() {
    suite { d in
      let flow = SetupFlow(defaults: d)
      flow.complete()
      XCTAssertFalse(SetupFlow(defaults: d).needsAutomaticSetup)
      XCTAssertEqual(d.dictionaryRepresentation()[SetupCompletion.key] as? Bool, true)
    }
  }
  func testValidBetaPreferencesSkipAutomaticSetup() {
    suite { d in
      SetupCompletion.recognizeExisting(defaults: d, old: ["notchEnabled": false])
      XCTAssertFalse(SetupFlow(defaults: d).needsAutomaticSetup)
    }
  }
  func testPreviouslyMigratedPreferencesSkipSetup() {
    suite { d in
      BetaPreferences.migrate(from: ["appearance": "dark"], to: d)
      SetupCompletion.recognizeExisting(defaults: d, old: [:])
      XCTAssertFalse(SetupFlow(defaults: d).needsAutomaticSetup)
    }
  }
  func testInvalidOrUnknownBetaKeysDoNotSkipSetup() {
    suite { d in
      SetupCompletion.recognizeExisting(defaults: d,
        old: ["appearance": "invalid", "notchEnabled": 1, "loginEnabled": true])
      XCTAssertTrue(SetupFlow(defaults: d).needsAutomaticSetup)
    }
  }
  func testMalformedCompletionFailsSafely() {
    for value: Any in ["true", 1, ["value": true]] {
      suite { d in
        d.set(value, forKey: SetupCompletion.key)
        SetupCompletion.recognizeExisting(defaults: d, old: ["appearance": "dark"])
        XCTAssertTrue(SetupFlow(defaults: d).needsAutomaticSetup)
      }
    }
  }
  func testManualReopenAfterCompletion() {
    suite { d in
      let flow = SetupFlow(defaults: d)
      flow.next(); flow.complete(); flow.reopen()
      XCTAssertEqual(flow.step, .welcome)
      XCTAssertFalse(flow.needsAutomaticSetup)
    }
  }
  func testCodexOnlyRoute() { checkRoute([.codex], [.welcome, .providers, .codex, .verify, .preferences, .done]) }
  func testClaudeOnlyRoute() { checkRoute([.claude], [.welcome, .providers, .claude, .verify, .preferences, .done]) }
  func testBothRoute() { checkRoute([.codex, .claude], [.welcome, .providers, .codex, .claude, .verify, .preferences, .done]) }
  func testNeitherRouteIsNotBlocked() { checkRoute([], [.welcome, .providers, .verify, .preferences, .done]) }
  private func checkRoute(_ selected: Set<ProviderID>, _ expected: [SetupStep]) {
    suite { d in
      let flow = SetupFlow(defaults: d); flow.selected = selected
      for step in expected { XCTAssertEqual(flow.step, step); flow.next() }
      for step in expected.reversed() { XCTAssertEqual(flow.step, step); flow.back() }
    }
  }
  func testStatusRequiresCurrentVerifiedReading() throws {
    var s = UsageSnapshot(provider: .codex)
    XCTAssertEqual(SetupStatus(snapshot: s), .checking)
    s.apply(.fail(.notInstalled)); XCTAssertEqual(SetupStatus(snapshot: s), .notInstalled)
    s.apply(.fail(.signedOut)); XCTAssertEqual(SetupStatus(snapshot: s), .signedOut)
    s.apply(.fail(.incompatible)); XCTAssertEqual(SetupStatus(snapshot: s), .unavailable)
    s.apply(.success(.init(binding: "synthetic", windows: [], date: Date())))
    XCTAssertEqual(SetupStatus(snapshot: s), .ready)
    s.apply(.fail(.timeout, binding: "synthetic"))
    XCTAssertEqual(SetupStatus(snapshot: s), .unavailable)
    s.state = .live; s.reading = nil
    XCTAssertEqual(SetupStatus(snapshot: s), .unavailable)
  }
  func testOnboardingSettingsUseExistingPreferencesAndDoNotCompleteEarly() {
    suite { d in
      let flow = SetupFlow(defaults: d), preferences = Preferences(defaults: d)
      SetupCompletion.recognizeExisting(defaults: d, old: [:])
      preferences.notchEnabled = false; preferences.menuEnabled = false; preferences.appearance = .light
      let restored = Preferences(defaults: d)
      XCTAssertFalse(restored.notchEnabled); XCTAssertFalse(restored.menuEnabled)
      XCTAssertEqual(restored.appearance, .light)
      XCTAssertTrue(flow.needsAutomaticSetup)
      SetupCompletion.recognizeExisting(defaults: d, old: [:])
      XCTAssertTrue(SetupFlow(defaults: d).needsAutomaticSetup)
    }
  }
  func testVerifiedCopyOnlyCommandConstants() {
    XCTAssertEqual(ProviderSetup.install(.codex), "brew install --cask codex")
    XCTAssertEqual(ProviderSetup.login(.codex), "codex login")
    XCTAssertEqual(ProviderSetup.install(.claude), "curl -fsSL https://claude.ai/install.sh | bash")
    XCTAssertEqual(ProviderSetup.login(.claude), "claude auth login")
  }
  func testNotchAppearanceOverridesAndSystemInheritance() {
    _ = NSApplication.shared
    let previous = NSApp.appearance
    defer { NSApp.appearance = previous }
    let notch = NotchController()
    defer { notch.close() }
    for preference in [AppAppearance.light, .dark] {
      notch.applyAppearance(preference)
      XCTAssertEqual(notch.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]), preference.native?.name)
    }
    notch.applyAppearance(.system)
    for name in [NSAppearance.Name.aqua, .darkAqua] {
      NSApp.appearance = NSAppearance(named: name)
      XCTAssertEqual(notch.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]), name)
    }
  }
}
import Darwin

@MainActor final class MigrationTests: XCTestCase {
  private func suite(_ body: (UserDefaults) throws -> Void) rethrows {
    let name = "AgentMeter-migration-" + UUID().uuidString
    let defaults = UserDefaults(suiteName: name)!
    defer { defaults.removePersistentDomain(forName: name) }
    try body(defaults)
  }
  func testAllowlistOnlyAndNoLoginRegistrationCopy() {
    suite { d in
      BetaPreferences.migrate(from: ["notchEnabled": false, "menuEnabled": true,
        "appearance": "dark", "loginEnabled": true, "token": "synthetic", "usage": 9], to: d)
      XCTAssertEqual(d.object(forKey: "notchEnabled") as? Bool, false)
      XCTAssertEqual(d.object(forKey: "menuEnabled") as? Bool, true)
      XCTAssertEqual(d.string(forKey: "appearance"), "dark")
      for key in ["loginEnabled", "token", "usage"] { XCTAssertNil(d.object(forKey: key)) }
      XCTAssertTrue(d.bool(forKey: BetaPreferences.completion))
    }
  }
  func testMalformedValuesUseDefaults() {
    suite { d in
      BetaPreferences.migrate(from: ["notchEnabled": 1, "menuEnabled": "false",
        "appearance": "unknown"], to: d)
      let p = Preferences(defaults: d)
      XCTAssertTrue(p.notchEnabled); XCTAssertTrue(p.menuEnabled)
      XCTAssertEqual(p.appearance, .light)
    }
  }
  func testProductionValuesWinAndMigrationRunsOnce() {
    suite { d in
      d.set("light", forKey: "appearance")
      BetaPreferences.migrate(from: ["appearance": "dark", "menuEnabled": false], to: d)
      XCTAssertEqual(d.string(forKey: "appearance"), "light")
      d.set(true, forKey: "menuEnabled")
      BetaPreferences.migrate(from: ["menuEnabled": false, "notchEnabled": false], to: d)
      XCTAssertTrue(d.bool(forKey: "menuEnabled"))
      XCTAssertNil(d.object(forKey: "notchEnabled"))
    }
  }
  func testAllAppearancesAndMissingBeta() {
    for value in ["system", "light", "dark"] {
      suite { d in
        BetaPreferences.migrate(from: ["appearance": value], to: d)
        XCTAssertEqual(d.string(forKey: "appearance"), value)
        XCTAssertEqual(Preferences(defaults: d).appearance, .light)
      }
    }
    suite { d in
      BetaPreferences.migrate(from: [:], to: d)
      XCTAssertTrue(d.bool(forKey: BetaPreferences.completion))
    }
  }
}

@MainActor final class SingleInstanceTests: XCTestCase {
  private func directory() throws -> URL {
    let d = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: d, withIntermediateDirectories: true,
      attributes: [.posixPermissions: 0o700])
    return d
  }
  func testExclusiveUntilReleaseAndPersistentInodeReacquired() throws {
    let d = try directory(); defer { try? FileManager.default.removeItem(at: d) }
    var first = try InstanceLease.acquire(directory: d)
    XCTAssertNotNil(first)
    for _ in 0..<20 { XCTAssertNil(try InstanceLease.acquire(directory: d)) }
    first = nil
    XCTAssertNotNil(try InstanceLease.acquire(directory: d))
    XCTAssertTrue(FileManager.default.fileExists(atPath: d.appendingPathComponent("instance.lock").path))
  }
  func testSymlinkAndHardLinkRejectedWithoutModifyingTarget() throws {
    let d = try directory(); defer { try? FileManager.default.removeItem(at: d) }
    let target = d.appendingPathComponent("target")
    try Data("unchanged".utf8).write(to: target)
    let lock = d.appendingPathComponent("instance.lock")
    try FileManager.default.createSymbolicLink(at: lock, withDestinationURL: target)
    XCTAssertThrowsError(try InstanceLease.acquire(directory: d))
    try FileManager.default.removeItem(at: lock)
    XCTAssertEqual(link(target.path, lock.path), 0)
    XCTAssertThrowsError(try InstanceLease.acquire(directory: d))
    XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "unchanged")
  }
  func testUnsafeDirectoryAndFilePermissionsFailClosed() throws {
    let d = try directory(); defer { try? FileManager.default.removeItem(at: d) }
    XCTAssertEqual(chmod(d.path, 0o777), 0)
    XCTAssertThrowsError(try InstanceLease.acquire(directory: d))
    XCTAssertEqual(chmod(d.path, 0o700), 0)
    let lease = try InstanceLease.acquire(directory: d)
    XCTAssertNotNil(lease)
    XCTAssertEqual(chmod(d.appendingPathComponent("instance.lock").path, 0o666), 0)
    XCTAssertThrowsError(try InstanceLease.acquire(directory: d))
    withExtendedLifetime(lease) {}
  }
}

@MainActor final class Phase2Tests: XCTestCase {
  func testNotchAcceptsFirstClickWhileAppIsInBackground() {
    let surface = TrackingSurface()
    XCTAssertTrue(surface.acceptsFirstMouse(for: nil))
    var opened = 0
    surface.clicked = { opened += 1 }
    XCTAssertTrue(surface.accessibilityPerformPress())
    XCTAssertEqual(opened, 1)
  }
  private func activity(_ codex: Bool, _ claude: Bool) -> ActivitySnapshot {
    .init(codex: .init(cli: codex), claude: .init(cli: claude))
  }
  func testForwardAndReverseActivityTransitions() {
    for order in [
      [(false, false), (true, false), (true, true), (false, true), (false, false)],
      [(false, false), (false, true), (true, true), (true, false), (false, false)],
    ] {
      var state = NotchState()
      for (c, a) in order {
        state.reconcile(activity: activity(c, a), enabled: true)
        XCTAssertEqual(state.providers, activity(c, a).providers)
        XCTAssertEqual(state.phase, c || a ? .compact : .hidden)
      }
    }
  }
  func testHoverRetainsExpansionWhenProvidersChange() {
    var state = NotchState()
    state.reconcile(activity: activity(true, false), enabled: true)
    state.hover(true)
    XCTAssertEqual(state.phase, .expanded)
    state.reconcile(activity: activity(true, true), enabled: true)
    XCTAssertEqual(state.phase, .expanded)
    XCTAssertEqual(state.providers.count, 2)
    state.reconcile(activity: activity(false, true), enabled: true)
    XCTAssertEqual(state.phase, .expanded)
    XCTAssertEqual(state.providers, [.claude])
    state.hover(false)
    XCTAssertEqual(state.phase, .compact)
  }
  func testEndingActivityWhileHoveredHidesCompletely() {
    var state = NotchState()
    state.reconcile(activity: activity(true, true), enabled: true)
    state.hover(true)
    state.reconcile(activity: activity(false, false), enabled: true)
    state.hover(true)
    XCTAssertEqual(state.phase, .hidden)
    XCTAssertTrue(state.providers.isEmpty)
  }
  func testDisablingNotchDoesNotChangeActivityAndReenableReflectsTruth() {
    let active = activity(true, true)
    var state = NotchState()
    state.reconcile(activity: active, enabled: true)
    state.hover(true)
    state.reconcile(activity: active, enabled: false)
    XCTAssertEqual(state.phase, .hidden)
    XCTAssertTrue(state.providers.isEmpty)
    XCTAssertEqual(active.providers.count, 2)
    state.reconcile(activity: active, enabled: true)
    XCTAssertEqual(state.phase, .compact)
    XCTAssertEqual(state.providers.count, 2)
  }
  func testSurfaceDeduplicationInNotch() {
    var state = NotchState()
    state.reconcile(
      activity: .init(
        codex: .init(cli: true, desktop: true), claude: .init(cli: true, desktop: true)),
      enabled: true)
    XCTAssertEqual(state.providers, [.codex, .claude])
  }
  func testPreferencesDefaultAndPersistenceWithoutUsageOrActivity() {
    let name = "AgentMeter-tests-\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: name)!
    defer { defaults.removePersistentDomain(forName: name) }
    let prefs = Preferences(defaults: defaults)
    XCTAssertTrue(prefs.notchEnabled)
    XCTAssertTrue(prefs.menuEnabled)
    XCTAssertEqual(prefs.appearance, .light)
    var changes = 0
    prefs.changed = { changes += 1 }
    prefs.notchEnabled = false
    prefs.menuEnabled = false
    prefs.appearance = .dark
    let restored = Preferences(defaults: defaults)
    XCTAssertFalse(restored.notchEnabled)
    XCTAssertFalse(restored.menuEnabled)
    XCTAssertEqual(restored.appearance, .dark)
    XCTAssertEqual(changes, 3)
    XCTAssertEqual(Set(defaults.persistentDomain(forName: name)?.keys.map { $0 } ?? []),
      ["notchEnabled", "menuEnabled", "appearance", SetupCompletion.key,
       Preferences.lightMigrationKey])
  }
  func testAppearanceSystemAndOverrides() {
    XCTAssertNil(AppAppearance.system.native)
    XCTAssertEqual(AppAppearance.light.native?.name.rawValue, "NSAppearanceNameAqua")
    XCTAssertEqual(AppAppearance.dark.native?.name.rawValue, "NSAppearanceNameDarkAqua")
  }
  func testLoadingAndUnknownGlancesNeverInventQuota() {
    var snapshot = UsageSnapshot(provider: .codex)
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "…")
    snapshot.apply(.fail(.notInstalled))
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "--")
    XCTAssertTrue(ProviderGlance(snapshot: snapshot).accessibility.contains("Not installed"))
  }
  func testPrimaryCoreSelectionAndStaleGlance() throws {
    var snapshot = UsageSnapshot(provider: .codex)
    let core = try UsageWindow(
      id: "core", bucket: "codex", label: "Main", durationMinutes: 10080, used: 5,
      reset: Date(timeIntervalSince1970: 2_000_000_000))
    let spark = try UsageWindow(
      id: "spark", bucket: "spark", label: "Spark", durationMinutes: 10080, used: 0, reset: nil)
    snapshot.apply(.success(.init(binding: "synthetic", windows: [spark, core], date: Date())))
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "95%")
    snapshot.apply(.fail(.timeout, binding: "synthetic"))
    XCTAssertEqual(snapshot.state, .stale)
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "95%")
    snapshot.apply(.fail(.accountChanged))
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "--")
  }
  func testClaudeFiveHourAndMixedUnknownGlances() throws {
    var snapshot = UsageSnapshot(provider: .claude)
    let short = try UsageWindow(
      id: "five_hour", bucket: "claude", label: "", durationMinutes: 300, used: 0, reset: nil)
    let long = try UsageWindow(
      id: "seven_day", bucket: "claude", label: "", durationMinutes: 10080, used: 12, reset: nil)
    snapshot.apply(.success(.init(binding: "synthetic", windows: [short, long], date: Date())))
    let rows = [
      ProviderGlance(snapshot: UsageSnapshot(provider: .codex)), ProviderGlance(snapshot: snapshot),
    ]
    XCTAssertEqual(rows.map(\.percentage), ["…", "100%"])
    XCTAssertTrue(rows[1].accessibility.contains("Claude, 100% remaining"))
  }
  func testCheckSetupRoutesToSettingsAndSupportsRepeatedRequests() {
    let model = Presentation()
    XCTAssertEqual(model.setupCheckRequest, 0)
    model.showSetupChecks()
    XCTAssertEqual(model.destination, .settings)
    XCTAssertEqual(model.setupCheckRequest, 1)
    model.destination = .about
    model.showSetupChecks()
    XCTAssertEqual(model.destination, .settings)
    XCTAssertEqual(model.setupCheckRequest, 2)
    XCTAssertFalse(model.manuallyRefreshing)
  }

}

@MainActor final class LlumiMigrationTests: XCTestCase {
  private func suite(_ body: (UserDefaults) throws -> Void) rethrows {
    let name = "Llumi-migration-" + UUID().uuidString
    let d = UserDefaults(suiteName: name)!
    defer { d.removePersistentDomain(forName: name) }
    try body(d)
  }
  func testNewDomainAndExplicitLegacySources() {
    XCTAssertEqual(InstanceLease.identifier, "io.github.praneshsivasankaran.llumi")
    XCTAssertEqual(BetaPreferences.legacyDomains, ["io.github.praneshsivasankaran.agentmeter", "local.agentmeter.mac"])
  }
  func testNewestValidLegacyWinsAndOnlyKnownValuesMigrate() {
    suite { d in
      BetaPreferences.migrate(sources: [["appearance":"dark", "setupCompleted":true, "notchEnabled":"wrong"],
        ["appearance":"light", "notchEnabled":false, "token":"synthetic", "loginEnabled":true]], to:d)
      XCTAssertEqual(d.string(forKey:"appearance"), "dark")
      XCTAssertEqual(d.object(forKey:"notchEnabled") as? Bool, false)
      XCTAssertTrue(SetupCompletion.isComplete(d))
      XCTAssertNil(d.object(forKey:"token")); XCTAssertNil(d.object(forKey:"loginEnabled"))
    }
  }
  func testCurrentCompletionFalseWinsAndMigrationIsIdempotent() {
    suite { d in
      d.set(false, forKey:SetupCompletion.key);d.set("system",forKey:"appearance")
      BetaPreferences.migrate(sources:[["setupCompleted":true,"appearance":"dark"]],to:d)
      XCTAssertFalse(SetupCompletion.isComplete(d));XCTAssertEqual(d.string(forKey:"appearance"),"system")
      BetaPreferences.migrate(sources:[["menuEnabled":false]],to:d)
      XCTAssertNil(d.object(forKey:"menuEnabled"))
    }
  }
  func testMalformedCompletionCannotSkipMigrationOrSetup() {
    suite { d in
      d.set("true",forKey:BetaPreferences.completion)
      d.set("true",forKey:SetupCompletion.key)
      BetaPreferences.migrate(sources:[["setupCompleted":1,"notchEnabled":1]],to:d)
      XCTAssertFalse(SetupCompletion.isComplete(d))
      XCTAssertEqual(Preferences(defaults:d).appearance,.light)
    }
  }
  func testLegacyAndLlumiLeasesExcludeBothDirectionsAndReleaseOnFailure() throws {
    let root=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at:root) }
    var old=try InstanceLease.acquire(directory:root.appendingPathComponent(InstanceLease.legacyIdentifier))
    XCTAssertNotNil(old);XCTAssertNil(try InstanceLease.acquireProductLeases(root:root))
    old=nil
    var current=try InstanceLease.acquireProductLeases(root:root)
    XCTAssertEqual(current?.count,2)
    XCTAssertNil(try InstanceLease.acquire(directory:root.appendingPathComponent(InstanceLease.legacyIdentifier)))
    XCTAssertNil(try InstanceLease.acquireProductLeases(root:root))
    current=nil
    XCTAssertNotNil(try InstanceLease.acquireProductLeases(root:root))
  }
}
