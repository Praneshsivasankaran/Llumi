import AppKit
import Foundation
import XCTest
import SwiftUI

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
  func testSignInReadinessDoesNotRequireAnAllowance() throws {
    var s = UsageSnapshot(provider: .codex)
    XCTAssertEqual(SetupStatus(snapshot: s), .notChecked)
    s.apply(.fail(.notInstalled)); XCTAssertEqual(SetupStatus(snapshot: s), .notInstalled)
    s.apply(.fail(.signedOut)); XCTAssertEqual(SetupStatus(snapshot: s), .signedOut)
    s.apply(.fail(.incompatible)); XCTAssertEqual(SetupStatus(snapshot: s), .unavailable)
    s.apply(.success(.init(binding: "synthetic", windows: [], date: Date())))
    XCTAssertEqual(SetupStatus(snapshot: s), .ready)
    XCTAssertEqual(SetupMonitoring.text(s), "Allowances not reported")
    let window = try UsageWindow(id: "codex:primary", bucket: "codex", label: "Main",
      durationMinutes: 300, used: 0, reset: nil)
    s.apply(.success(.init(binding: "synthetic", windows: [window], date: Date())))
    XCTAssertEqual(SetupStatus(snapshot: s), .ready)
    s.apply(.success(.init(binding: "synthetic", windows: [], date: Date())))
    XCTAssertEqual(SetupStatus(snapshot: s), .ready)
    s.apply(.fail(.timeout, binding: "synthetic"))
    XCTAssertEqual(SetupStatus(snapshot: s), .ready)
    XCTAssertEqual(SetupMonitoring.text(s), "Allowances unavailable")
    s.apply(.fail(.timeout))
    s.state = .live; s.reading = nil
    XCTAssertEqual(SetupStatus(snapshot: s), .unavailable)
  }
  func testUnsupportedAllowanceAndKnownBillingModeRemainDistinctFromAuthentication() {
    for provider in ProviderID.allCases {
      var snapshot = UsageSnapshot(provider: provider)
      snapshot.apply(.success(.init(binding: "fixture", windows: [], date: Date(),
        availability: .unsupportedAllowance)))
      XCTAssertEqual(SetupStatus(snapshot: snapshot), .ready)
      XCTAssertEqual(SetupMonitoring.text(snapshot), "Allowance format not supported")
      XCTAssertEqual(SetupDiagnostic(snapshot).authentication, "verified")
      snapshot.apply(.fail(.unsupportedBilling))
      XCTAssertEqual(SetupStatus(snapshot: snapshot), .unsupportedBilling)
      XCTAssertEqual(SetupMonitoring.text(snapshot), "This billing mode can’t be monitored")
      XCTAssertEqual(SetupDiagnostic(snapshot).authentication, "mode-detected")
      XCTAssertFalse(snapshot.authenticationVerified)
    }
  }
  func testVerifiedRetrievalFailureRetainsReadinessOnlyAndUnverifiedFailureClearsIt() throws {
    let window = try UsageWindow(id: "codex:primary", bucket: "codex", label: "", durationMinutes: 300,
      used: 20, reset: nil)
    for failure in [Failure.timeout, .rateLimited, .malformed, .unavailable] {
      var snapshot = UsageSnapshot(provider: .codex)
      snapshot.apply(.success(.init(binding: "fixture", windows: [window], date: Date())))
      snapshot.apply(.fail(failure, binding: "fixture"))
      XCTAssertEqual(snapshot.state, .stale)
      XCTAssertEqual(SetupStatus(snapshot: snapshot), .ready)
      XCTAssertEqual(SetupMonitoring.text(snapshot), "Last known allowances — stale")
      XCTAssertEqual(SetupDiagnostic(snapshot).authentication, "verified")
      snapshot.apply(.fail(failure))
      XCTAssertEqual(SetupStatus(snapshot: snapshot), .unavailable)
      XCTAssertEqual(SetupDiagnostic(snapshot).authentication, "unknown")
      XCTAssertNil(snapshot.reading)
    }
    for failure in [Failure.signedOut, .notInstalled, .accountChanged] {
      var snapshot = UsageSnapshot(provider: .codex)
      snapshot.apply(.success(.init(binding: "fixture", windows: [window], date: Date())))
      snapshot.apply(.fail(failure, binding: "fixture"))
      XCTAssertFalse(snapshot.authenticationVerified)
      XCTAssertNil(snapshot.reading)
    }
  }
  func testReadinessDoesNotInferAuthenticationFromAbsentOrOldReading() throws {
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.apply(.success(.init(binding: "", windows: [], date: Date())))
    XCTAssertFalse(snapshot.authenticationVerified)
    XCTAssertEqual(SetupStatus(snapshot: snapshot), .unavailable)
    snapshot.apply(.fail(.timeout, binding: "new-verified-account"))
    XCTAssertEqual(SetupStatus(snapshot: snapshot), .ready)
    XCTAssertNil(snapshot.reading)
    snapshot.refreshStatus = .init(checking: true)
    XCTAssertEqual(SetupStatus(snapshot: snapshot), .checking)
    XCTAssertEqual(SetupMonitoring.text(snapshot), "Checking allowances…")
  }
  func testRetryPresentationExplainsCooldownWithoutClaimingAQueryRan() {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.refreshStatus = .init(retryAt: now.addingTimeInterval(900), retryReason: .cooldown)
    XCTAssertTrue(SetupRetryPresentation.canRetry(snapshot, at: now))
    XCTAssertEqual(SetupRetryPresentation.message(snapshot, at: now),
      "Automatic retry in 15m. Retry checks now.")
    snapshot.refreshStatus = .init(retryAt: now.addingTimeInterval(60), retryReason: .rateLimited)
    XCTAssertEqual(SetupStatus(snapshot: snapshot), .notChecked)
    XCTAssertFalse(SetupRetryPresentation.canRetry(snapshot, at: now))
    XCTAssertEqual(SetupRetryPresentation.message(snapshot, at: now), "Rate limited. Retry in 1m.")
    XCTAssertEqual(SetupRetryPresentation.message(snapshot, at: now.addingTimeInterval(51)),
      "Rate limited. Retry in 9s.")
    XCTAssertTrue(SetupRetryPresentation.canRetry(snapshot, at: now.addingTimeInterval(60)))
    XCTAssertNil(SetupRetryPresentation.message(snapshot, at: now.addingTimeInterval(60)))
    XCTAssertFalse(SetupRetryPresentation.canRetry(snapshot, at: now.addingTimeInterval(-3600)))
    XCTAssertTrue(SetupRetryPresentation.message(snapshot, at: now.addingTimeInterval(-3600))?
      .hasPrefix("Rate limited. Retry after ") == true)
    snapshot.refreshStatus = .init(retryAt: now.addingTimeInterval(10), retryReason: .manualCooldown)
    XCTAssertFalse(SetupRetryPresentation.canRetry(snapshot, at: now))
    XCTAssertEqual(SetupRetryPresentation.message(snapshot, at: now), "Retry in 10s.")
    snapshot.refreshStatus = .init(checking: true)
    XCTAssertFalse(SetupRetryPresentation.canRetry(snapshot, at: now))
    XCTAssertEqual(SetupRetryPresentation.message(snapshot, at: now), "Checking…")
  }
  func testSetupDiagnosticsExcludeRetryDeadlinesAndAccountBindings() {
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.apply(.fail(.rateLimited, binding: "private-secret-account"))
    snapshot.refreshStatus = .init(retryAt: Date(timeIntervalSince1970: 2_000_000_000), retryReason: .rateLimited)
    let report = SetupDiagnostics.report([.codex: snapshot], version: "1.1.3", build: "3")
    XCTAssertTrue(report.contains("Authentication: verified"))
    XCTAssertTrue(report.contains("Usage: unavailable"))
    for secret in ["private-secret-account", "2000000000", "retryAt"] { XCTAssertFalse(report.contains(secret)) }
  }
  func testIsolatedSetupStatusScreens() async throws {
    let directory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
      .appendingPathComponent("Llumi113Review/SetupRetryFix/Screens", isDirectory: true)
    guard FileManager.default.fileExists(atPath: directory.appendingPathComponent("capture-request").path)
    else { throw XCTSkip("Setup status fixture renders require local review opt-in.") }
    let name = "Llumi-setup-review-" + UUID().uuidString
    let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
    defer { defaults.removePersistentDomain(forName: name) }
    let model = Presentation(defaults: defaults)
    var codex = UsageSnapshot(provider: .codex), claude = UsageSnapshot(provider: .claude)
    codex.apply(.success(.init(binding: "fixture-a", windows: [], date: Date(), availability: .notReported)))
    claude.apply(.fail(.unsupportedBilling))
    model.usage = [.codex: codex, .claude: claude]
    try await renderSetup(model, directory: directory, name: "missing-and-unsupported")
    codex.apply(.fail(.rateLimited, binding: "fixture-a"))
    codex.refreshStatus = .init(retryAt: Date().addingTimeInterval(60), retryReason: .rateLimited)
    claude.apply(.fail(.signedOut))
    claude.refreshStatus = .init(retryAt: Date().addingTimeInterval(600), retryReason: .cooldown)
    model.usage = [.codex: codex, .claude: claude]
    try await renderSetup(model, directory: directory, name: "retry-waiting")
  }
  private func renderSetup(_ model: Presentation, directory: URL, name: String) async throws {
    _ = NSApplication.shared
    let view = CheckSetupView(model: model).frame(width: 540)
      .background(Color(nsColor: .windowBackgroundColor)).preferredColorScheme(.light)
    let host = NSHostingView(rootView: view)
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 380),
      styleMask: [.titled], backing: .buffered, defer: false)
    window.appearance = NSAppearance(named: .aqua)
    window.isReleasedWhenClosed = false
    window.contentView = host
    host.frame = NSRect(x: 0, y: 0, width: 540, height: 380)
    defer { window.close() }
    window.layoutIfNeeded(); host.layoutSubtreeIfNeeded()
    try await Task.sleep(for: .milliseconds(250))
    host.layoutSubtreeIfNeeded()
    let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
    host.cacheDisplay(in: host.bounds, to: bitmap)
    let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    try png.write(to: directory.appendingPathComponent(name + ".png"), options: .atomic)
    window.orderOut(nil)
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

private final class Scheduler113Clock: @unchecked Sendable {
  private let lock = NSLock()
  private var value = Date(timeIntervalSince1970: 2_000_000_000)
  func now() -> Date { lock.withLock { value } }
  func advance(_ seconds: Double) { lock.withLock { value.addTimeInterval(seconds) } }
}

private actor Scheduler113Timers {
  private var pending: [UUID: CheckedContinuation<Void, Error>] = [:]
  private(set) var maximumPending = 0
  var count: Int { pending.count }
  func sleep(_ seconds: Double) async throws {
    let id = UUID()
    try await withTaskCancellationHandler {
      try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
        if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
        else {
          pending[id] = continuation
          maximumPending = max(maximumPending, pending.count)
        }
      }
    } onCancel: {
      Task { await self.cancel(id) }
    }
  }
  private func cancel(_ id: UUID) {
    pending.removeValue(forKey: id)?.resume(throwing: CancellationError())
  }
  func fire() {
    let work = Array(pending.values)
    pending.removeAll()
    for continuation in work { continuation.resume() }
  }
}

private actor Scheduler113Source: UsageSource {
  private var results: [(QueryResult, Double)] = []
  private(set) var calls = 0
  private(set) var maximumActive = 0
  private var active = 0
  func append(_ result: QueryResult, delay: Double = 0) { results.append((result, delay)) }
  func query() async -> QueryResult {
    calls += 1
    active += 1
    maximumActive = max(maximumActive, active)
    defer { active -= 1 }
    guard !results.isEmpty else { return .fail(.unavailable) }
    let (result, delay) = results.removeFirst()
    do {
      if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
      return result
    } catch { return .fail(.cancelled) }
  }
}

@MainActor final class Allowance113SchedulerTests: XCTestCase {
  private func reading(_ binding: String, at date: Date, reset: Date? = nil) throws -> QueryResult {
    .success(.init(binding: binding, windows: [try UsageWindow(
      id: "codex:primary", bucket: "codex", label: "Main", durationMinutes: 300,
      used: 40, reset: reset)], date: date))
  }
  private func waitForTimers(_ timers: Scheduler113Timers, count: Int) async {
    for _ in 0..<1000 {
      if await timers.count == count { return }
      await Task.yield()
    }
    let actual = await timers.count
    XCTAssertEqual(actual, count)
  }
  private func waitForCalls(_ source: Scheduler113Source, count: Int) async {
    for _ in 0..<1000 {
      if await source.calls >= count { return }
      await Task.yield()
    }
    let actual = await source.calls
    XCTAssertEqual(actual, count)
  }
  func testBackoffIsBoundedAndSuccessResetsIt() throws {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    var policy = UsageRefreshPolicy()
    policy.record(.fail(.unavailable), at: now)
    XCTAssertEqual(policy.retryAfter, now.addingTimeInterval(30))
    XCTAssertFalse(policy.allowsRefresh(at: now.addingTimeInterval(29)))
    XCTAssertTrue(policy.allowsRefresh(at: now.addingTimeInterval(30)))
    policy.record(.fail(.rateLimited), at: now)
    XCTAssertEqual(policy.retryAfter, now.addingTimeInterval(120))
    for _ in 0..<50 { policy.record(.fail(.timeout), at: now) }
    XCTAssertEqual(policy.failureCount, 6)
    XCTAssertEqual(policy.retryAfter, now.addingTimeInterval(900))
    policy.record(try reading("A", at: now), at: now)
    XCTAssertNil(policy.retryAfter)
    XCTAssertEqual(policy.failureCount, 0)
  }
  func testNearestSupportedResetAndPastTimestampsDoNotImplyRefill() throws {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    let reset = now.addingTimeInterval(20)
    var snapshot = UsageSnapshot(provider: .codex)
    let windows = try [
      UsageWindow(id: "codex:primary", bucket: "codex", label: "Main", durationMinutes: 300,
        used: 40, reset: reset),
      UsageWindow(id: "codex:secondary", bucket: "codex", label: "Main", durationMinutes: 10080,
        used: 70, reset: now.addingTimeInterval(100)),
      UsageWindow(id: "opaque", bucket: "opaque", label: "Opaque", durationMinutes: 1,
        used: 0, reset: now.addingTimeInterval(1), scope: .unknown),
    ]
    snapshot.apply(.success(.init(binding: "A", windows: windows, date: now)))
    var policy = UsageRefreshPolicy()
    XCTAssertEqual(policy.nextReset(in: snapshot, at: now), reset)
    policy.didRefreshReset(reset, at: now)
    XCTAssertEqual(policy.nextReset(in: snapshot, at: now), now.addingTimeInterval(100))
    XCTAssertNil(policy.nextReset(in: snapshot, at: now.addingTimeInterval(100)))
    XCTAssertEqual(snapshot.primary?.remaining, 60)
  }
  func testMovingNearFutureResetsRespectCooldownAcrossSuccessAndToggle() throws {
    let clock = Scheduler113Clock()
    var policy = UsageRefreshPolicy()
    for _ in 0..<50 {
      let current = clock.now(), reset = current.addingTimeInterval(1)
      policy.didRefreshReset(reset, at: current)
      policy.record(try reading("A", at: current), at: current)
      policy.clearResetHistory()  // Provider disable/account changes retain cadence only.
      XCTAssertEqual(policy.resetDeadline(for: reset.addingTimeInterval(1), at: current),
        current.addingTimeInterval(30))
      clock.advance(30)
    }
    let current = clock.now()
    policy.record(.fail(.rateLimited), at: current)
    XCTAssertEqual(policy.resetDeadline(for: current.addingTimeInterval(1), at: current),
      current.addingTimeInterval(60))
  }
  func testManualAndActivityRequestsRespectRateLimitPerProvider() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers()
    let codex = Scheduler113Source(), claude = Scheduler113Source()
    await codex.append(try reading("A", at: clock.now()))
    await codex.append(.fail(.rateLimited, binding: "A"))
    await codex.append(try reading("B", at: clock.now()))
    await claude.append(.fail(.unavailable))
    let store = UsageStore(sources: [.codex: codex, .claude: claude], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(.codex); await store.waitForIdle()
    await store.refresh(.codex); await store.waitForIdle()
    let stale = await store.snapshot()
    XCTAssertEqual(stale[.codex]?.state, .stale)
    for _ in 0..<20 {
      await store.refresh(.codex)
      await store.refresh(.codex, onlyIfOlderThan: 15)
    }
    await store.refresh(.claude); await store.waitForIdle()
    let limitedCalls = await codex.calls, independentCalls = await claude.calls
    XCTAssertEqual(limitedCalls, 2)
    XCTAssertEqual(independentCalls, 1)
    clock.advance(60)
    await store.refresh(.codex); await store.waitForIdle()
    let changed = await store.snapshot()
    XCTAssertEqual(changed[.codex]?.reading?.binding, "B")
    await store.stop()
    await waitForTimers(timers, count: 0)
  }
  func testResetTimerRefreshesOnceAndReusesNoPastReset() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    let reset = clock.now().addingTimeInterval(10)
    await source.append(try reading("A", at: clock.now(), reset: reset))
    await source.append(try reading("A", at: clock.now(), reset: reset))
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle()
    await waitForTimers(timers, count: 1)
    clock.advance(11)
    await timers.fire()
    await waitForCalls(source, count: 2)
    await store.waitForIdle()
    await waitForTimers(timers, count: 0)
    let snapshot = await store.snapshot(), calls = await source.calls
    XCTAssertEqual(calls, 2)
    XCTAssertEqual(snapshot[.codex]?.primary?.remaining, 60)
    await store.stop()
  }
  func testResetDuringFlightQueuesOnlyOnePostResetQuery() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    let reset = clock.now().addingTimeInterval(10)
    await source.append(try reading("A", at: clock.now(), reset: reset))
    await source.append(try reading("A", at: clock.now(), reset: reset), delay: 0.01)
    await source.append(try reading("A", at: reset.addingTimeInterval(1), reset: reset))
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle()
    await waitForTimers(timers, count: 1)
    await store.refresh(.codex)
    await waitForCalls(source, count: 2)
    clock.advance(11); await timers.fire()
    await store.waitForIdle()
    let calls = await source.calls, maximum = await source.maximumActive
    XCTAssertEqual(calls, 3)
    XCTAssertEqual(maximum, 1)
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testRetryTimerCoalescesAndDisableCancelsScheduledAccountData() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    await source.append(.fail(.unavailable))
    await source.append(try reading("new-account", at: clock.now()), delay: 0.01)
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle()
    await waitForTimers(timers, count: 1)
    for _ in 0..<20 { await store.refresh() }
    let timerMaximum = await timers.maximumPending
    XCTAssertEqual(timerMaximum, 1)
    clock.advance(30)
    await timers.fire()
    await waitForCalls(source, count: 2)
    for _ in 0..<20 { await store.refresh(.codex) }
    await store.waitForIdle()
    let maximum = await source.maximumActive
    XCTAssertEqual(maximum, 1)
    await source.append(.fail(.unavailable, binding: "new-account"))
    await store.refresh(); await store.waitForIdle()
    await waitForTimers(timers, count: 1)
    await store.setEnabledProviders([])
    await waitForTimers(timers, count: 0)
    await store.setEnabledProviders([.codex])
    await store.waitForIdle()
    let throttledReenable = await source.calls
    XCTAssertEqual(throttledReenable, 3)
    await waitForTimers(timers, count: 1)
    await store.setEnabledProviders([])
    await waitForTimers(timers, count: 0)
    clock.advance(900); await timers.fire(); await store.refresh()
    let disabled = await store.snapshot(), calls = await source.calls
    XCTAssertNil(disabled[.codex]?.reading)
    XCTAssertEqual(calls, 3)
    await store.stop()
  }
  func testSleepCancelsTimersAndWakePreservesRetryDeadline() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    await source.append(.fail(.rateLimited))
    await source.append(try reading("A", at: clock.now()))
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle()
    await waitForTimers(timers, count: 1)
    await store.suspend(); await waitForTimers(timers, count: 0)
    await store.resume(); await store.waitForIdle()
    let early = await source.calls
    XCTAssertEqual(early, 1)
    await waitForTimers(timers, count: 1)
    clock.advance(60); await timers.fire()
    await waitForCalls(source, count: 2)
    await store.waitForIdle()
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testStoreTimeoutClearsDataWithoutVerifiedBinding() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    await source.append(try reading("A", at: clock.now()))
    await source.append(try reading("A", at: clock.now()), delay: 10)
    let store = UsageStore(sources: [.codex: source], timeout: 0.01, now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle()
    await store.refresh(); await store.waitForIdle()
    let snapshot = await store.snapshot()
    XCTAssertEqual(snapshot[.codex]?.failure, .timeout)
    XCTAssertNil(snapshot[.codex]?.reading)
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testExplicitRetryFindsInstallationOrLoginDuringMaximumBackgroundBackoff() async throws {
    for failure in [Failure.notInstalled, .signedOut] {
      let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
      let store = UsageStore(sources: [.codex: source], now: { clock.now() },
        timerSleep: { try await timers.sleep($0) }) { _ in }
      for attempt in 0..<6 {
        await source.append(.fail(failure))
        await store.refresh(.codex); await store.waitForIdle()
        let snapshot = await store.snapshot()
        let deadline = try XCTUnwrap(snapshot[.codex]?.refreshStatus.retryAt)
        if attempt < 5 { clock.advance(deadline.timeIntervalSince(clock.now())) }
      }
      let before = await store.snapshot()
      XCTAssertEqual(before[.codex]?.refreshStatus.retryReason, .cooldown)
      XCTAssertEqual(before[.codex]?.refreshStatus.retryAt, clock.now().addingTimeInterval(900))
      await source.append(try reading("new-login", at: clock.now()))
      await store.refresh(.codex, intent: .userInitiated); await store.waitForIdle()
      let after = await store.snapshot(), calls = await source.calls
      XCTAssertEqual(calls, 7)
      XCTAssertEqual(after[.codex]?.state, .live)
      XCTAssertEqual(after[.codex]?.reading?.binding, "new-login")
      XCTAssertEqual(after[.codex]?.refreshStatus.retryReason, .manualCooldown)
      await waitForTimers(timers, count: 0)
      clock.advance(900); await timers.fire(); await store.waitForIdle()
      let noObsoleteRetry = await source.calls
      XCTAssertEqual(noObsoleteRetry, 7)
      await store.stop()
    }
  }
  func testExplicitRetryCoalescesAndUsesIndependentTenSecondClickCadence() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    await source.append(try reading("A", at: clock.now()), delay: 0.01)
    await source.append(try reading("B", at: clock.now()))
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(.codex, intent: .userInitiated)
    let checking = await store.snapshot()
    XCTAssertTrue(checking[.codex]?.refreshStatus.checking == true)
    for _ in 0..<20 { await store.refresh(.codex, intent: .userInitiated) }
    await store.waitForIdle()
    for _ in 0..<20 { await store.refresh(.codex, intent: .userInitiated) }
    let limited = await store.snapshot(), firstCalls = await source.calls
    XCTAssertEqual(firstCalls, 1)
    XCTAssertFalse(limited[.codex]?.refreshStatus.checking == true)
    XCTAssertEqual(limited[.codex]?.refreshStatus.retryReason, .manualCooldown)
    XCTAssertEqual(limited[.codex]?.refreshStatus.retryAt, clock.now().addingTimeInterval(10))
    clock.advance(9); await store.refresh(.codex, intent: .userInitiated)
    let beforeDeadline = await source.calls
    XCTAssertEqual(beforeDeadline, 1)
    clock.advance(1); await store.refresh(.codex, intent: .userInitiated)
    await store.waitForIdle()
    let calls = await source.calls, maximum = await source.maximumActive
    XCTAssertEqual(calls, 2); XCTAssertEqual(maximum, 1)
    let fresh = await store.snapshot()
    XCTAssertEqual(fresh[.codex]?.reading?.binding, "B")
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testHardRateLimitDeadlineSurvivesGenericFailuresAndSuccessUntilExpired() throws {
    let now = Date(timeIntervalSince1970: 2_000_000_000)
    var policy = UsageRefreshPolicy()
    policy.record(.fail(.rateLimited), at: now)
    let hardDeadline = now.addingTimeInterval(60)
    policy.record(.fail(.notInstalled), at: now.addingTimeInterval(1))
    XCTAssertEqual(policy.rateLimitAfter, hardDeadline)
    policy.record(try reading("A", at: now.addingTimeInterval(2)), at: now.addingTimeInterval(2))
    XCTAssertNil(policy.retryAfter)
    XCTAssertEqual(policy.rateLimitAfter, hardDeadline)
    XCTAssertEqual(policy.blockedStatus(at: now.addingTimeInterval(59), intent: .userInitiated),
      UsageRefreshStatus(retryAt: hardDeadline, retryReason: .rateLimited))
    XCTAssertFalse(policy.allowsRefresh(at: now.addingTimeInterval(59)))
    XCTAssertEqual(policy.automaticRetryAt(at: now), hardDeadline)
    XCTAssertTrue(policy.allowsRefresh(at: hardDeadline, intent: .userInitiated))
    policy.record(try reading("A", at: hardDeadline), at: hardDeadline)
    XCTAssertNil(policy.rateLimitAfter)
  }
  func testBackgroundCooldownCannotHideTheActiveExplicitRetryClickCadence() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    await source.append(.fail(.unavailable))
    await source.append(try reading("A", at: clock.now()))
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(.codex, intent: .userInitiated); await store.waitForIdle()
    await store.refresh(.codex)
    let initiallyBlocked = await store.snapshot()
    XCTAssertEqual(initiallyBlocked[.codex]?.refreshStatus.retryReason, .manualCooldown)
    XCTAssertEqual(initiallyBlocked[.codex]?.refreshStatus.retryAt, clock.now().addingTimeInterval(10))
    clock.advance(9); await store.refresh(.codex)
    let stillBlocked = await store.snapshot()
    XCTAssertEqual(stillBlocked[.codex]?.refreshStatus.retryReason, .manualCooldown)
    let firstCalls = await source.calls
    XCTAssertEqual(firstCalls, 1)
    clock.advance(1); await store.refresh(.codex)
    let backgroundWaiting = await store.snapshot()
    XCTAssertEqual(backgroundWaiting[.codex]?.refreshStatus.retryReason, .cooldown)
    await store.refresh(.codex, intent: .userInitiated); await store.waitForIdle()
    let finalCalls = await source.calls
    XCTAssertEqual(finalCalls, 2)
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testExplicitRetryHonorsRateLimitAcrossProviderToggleAndSleepWithoutBlockingOtherProvider() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers()
    let codex = Scheduler113Source(), claude = Scheduler113Source()
    await codex.append(.fail(.rateLimited))
    await codex.append(try reading("new-account", at: clock.now()))
    await claude.append(try reading("independent", at: clock.now()))
    let store = UsageStore(sources: [.codex: codex, .claude: claude], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(.codex); await store.waitForIdle()
    await store.refresh(intent: .userInitiated); await store.waitForIdle()
    let waiting = await store.snapshot()
    XCTAssertEqual(waiting[.codex]?.refreshStatus.retryReason, .rateLimited)
    XCTAssertEqual(waiting[.codex]?.refreshStatus.retryAt, clock.now().addingTimeInterval(60))
    let codexCalls = await codex.calls, claudeCalls = await claude.calls
    XCTAssertEqual(codexCalls, 1); XCTAssertEqual(claudeCalls, 1)
    await store.setEnabledProviders([.claude])
    let disabled = await store.snapshot()
    XCTAssertNil(disabled[.codex]?.reading)
    XCTAssertEqual(disabled[.codex]?.refreshStatus, UsageRefreshStatus())
    await store.setEnabledProviders([.codex, .claude])
    for _ in 0..<20 { await store.refresh(.codex, intent: .userInitiated) }
    await store.suspend(); await waitForTimers(timers, count: 0)
    await store.resume(); await store.waitForIdle()
    await store.refresh(.codex, intent: .userInitiated)
    let preserved = await store.snapshot(), beforeDeadline = await codex.calls
    XCTAssertEqual(beforeDeadline, 1)
    XCTAssertEqual(preserved[.codex]?.refreshStatus.retryReason, .rateLimited)
    clock.advance(60)
    await store.refresh(.codex, intent: .userInitiated); await store.waitForIdle()
    let after = await store.snapshot(), finalCalls = await codex.calls
    XCTAssertEqual(finalCalls, 2)
    XCTAssertEqual(after[.codex]?.reading?.binding, "new-account")
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testExplicitRetryAndExpiredErrorTimerCannotStartConcurrentQueries() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    await source.append(.fail(.unavailable))
    await source.append(try reading("A", at: clock.now()), delay: 0.01)
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle(); await waitForTimers(timers, count: 1)
    clock.advance(30)
    await store.refresh(.codex, intent: .userInitiated)
    await timers.fire()
    for _ in 0..<20 { await store.refresh(.codex, intent: .userInitiated) }
    await store.waitForIdle(); await waitForTimers(timers, count: 0)
    let calls = await source.calls, maximum = await source.maximumActive
    XCTAssertEqual(calls, 2); XCTAssertEqual(maximum, 1)
    await store.stop()
  }
  func testExplicitRetryKeepsOneFollowUpWhenReportedResetPassesDuringItsQuery() async throws {
    let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
    let reset = clock.now().addingTimeInterval(10)
    await source.append(try reading("A", at: clock.now(), reset: reset))
    await source.append(try reading("A", at: clock.now(), reset: reset), delay: 0.01)
    await source.append(try reading("A", at: reset.addingTimeInterval(1), reset: reset))
    let store = UsageStore(sources: [.codex: source], now: { clock.now() },
      timerSleep: { try await timers.sleep($0) }) { _ in }
    await store.refresh(); await store.waitForIdle(); await waitForTimers(timers, count: 1)
    await store.refresh(.codex, intent: .userInitiated)
    await waitForCalls(source, count: 2)
    clock.advance(11); await timers.fire()
    await store.waitForIdle()
    let calls = await source.calls, maximum = await source.maximumActive
    XCTAssertEqual(calls, 3); XCTAssertEqual(maximum, 1)
    await store.stop(); await waitForTimers(timers, count: 0)
  }
  func testToggleAndWakeCannotBypassManualMinimumAndDeferOnlyAnActualRequest() async throws {
    for wake in [false, true] {
      let clock = Scheduler113Clock(), timers = Scheduler113Timers(), source = Scheduler113Source()
      await source.append(try reading("A", at: clock.now()))
      await source.append(try reading("B", at: clock.now()))
      let store = UsageStore(sources: [.codex: source], now: { clock.now() },
        timerSleep: { try await timers.sleep($0) }) { _ in }
      await store.refresh(.codex, intent: .userInitiated); await store.waitForIdle()
      // A successful explicit check alone must not create a follow-up timer.
      await waitForTimers(timers, count: 0)
      await store.refresh(.codex, onlyIfOlderThan: 15)
      await waitForTimers(timers, count: 0)
      if wake {
        await store.suspend(); await store.resume()
      } else {
        await store.setEnabledProviders([])
        await store.setEnabledProviders([.codex])
      }
      await store.waitForIdle()
      let blocked = await store.snapshot(), initialCalls = await source.calls
      XCTAssertEqual(initialCalls, 1)
      XCTAssertEqual(blocked[.codex]?.refreshStatus.retryReason, .manualCooldown)
      XCTAssertEqual(blocked[.codex]?.refreshStatus.retryAt, clock.now().addingTimeInterval(10))
      for _ in 0..<20 {
        await store.refresh(.codex)
        await store.refresh(.codex, intent: .userInitiated)
      }
      await waitForTimers(timers, count: 1)
      clock.advance(9); await timers.fire()
      await waitForTimers(timers, count: 1)
      let beforeDeadline = await source.calls
      XCTAssertEqual(beforeDeadline, 1)
      clock.advance(1); await timers.fire(); await waitForCalls(source, count: 2)
      await store.waitForIdle(); await waitForTimers(timers, count: 0)
      let final = await store.snapshot(), calls = await source.calls
      let maximum = await source.maximumActive
      XCTAssertEqual(calls, 2); XCTAssertEqual(maximum, 1)
      XCTAssertEqual(final[.codex]?.reading?.binding, "B")
      clock.advance(900); await timers.fire(); await store.waitForIdle()
      let noRepeatedRefresh = await source.calls
      XCTAssertEqual(noRepeatedRefresh, 2)
      await store.stop()
    }
  }
}
