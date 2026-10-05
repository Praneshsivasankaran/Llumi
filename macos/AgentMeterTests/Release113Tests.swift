import Foundation
import XCTest

@MainActor final class Release113PreferenceTests: XCTestCase {
  private func suite(_ body: (UserDefaults) -> Void) {
    let name = "Llumi-113-preferences-" + UUID().uuidString
    let defaults = UserDefaults(suiteName: name)!
    defer { defaults.removePersistentDomain(forName: name) }
    body(defaults)
  }
  func testFreshInstallIsLightAndStillRequiresSetup() {
    suite { defaults in
      let preferences = Preferences(defaults: defaults)
      XCTAssertEqual(preferences.appearance, .light)
      XCTAssertTrue(SetupFlow(defaults: defaults, preferences: preferences).needsAutomaticSetup)
      XCTAssertEqual(preferences.enabledProviders, Set(ProviderID.allCases))
    }
  }
  func testExistingDarkAppearanceChangesOnceAndLaterChoicePersists() {
    suite { defaults in
      defaults.set("dark", forKey: "appearance")
      let preferences = Preferences(defaults: defaults)
      XCTAssertEqual(preferences.appearance, .light)
      XCTAssertFalse(SetupFlow(defaults: defaults, preferences: preferences).needsAutomaticSetup)
      preferences.appearance = .system
      XCTAssertEqual(Preferences(defaults: defaults).appearance, .system)
    }
  }
  func testProviderSwitchesPersistAndSetupSharesThem() {
    suite { defaults in
      let preferences = Preferences(defaults: defaults)
      let setup = SetupFlow(defaults: defaults, preferences: preferences)
      preferences.setEnabled(.claude, false)
      XCTAssertEqual(setup.selected, [.codex])
      XCTAssertFalse(setup.steps.contains(.claude))
      setup.selected = []
      XCTAssertTrue(preferences.enabledProviders.isEmpty)
      XCTAssertTrue(Preferences(defaults: defaults).enabledProviders.isEmpty)
      preferences.setEnabled(.claude, true)
      XCTAssertEqual(setup.selected, [.claude])
      setup.next(); setup.next()
      XCTAssertEqual(setup.step, .claude)
      preferences.setEnabled(.claude, false)
      setup.next()
      XCTAssertEqual(setup.step, .verify)
    }
  }
  func testAutomaticUpdateMigrationEnablesOnceIncludingPreviousOptOut() {
    suite { defaults in
      var enabled = false
      AutomaticUpdatesMigration.apply(defaults: defaults) { enabled = true }
      XCTAssertTrue(enabled)
      enabled = false
      AutomaticUpdatesMigration.apply(defaults: defaults) { enabled = true }
      XCTAssertFalse(enabled)
    }
  }
  func testUpdateDismissalIsPerVersionAndPerSession() {
    let updates = UpdatePresentation()
    updates.showAvailableUpdate("1.1.3")
    XCTAssertEqual(updates.availableVersion, "1.1.3")
    updates.dismissAction()
    updates.showAvailableUpdate("1.1.3")
    XCTAssertNil(updates.availableVersion)
    updates.showAvailableUpdate("1.1.4")
    XCTAssertEqual(updates.availableVersion, "1.1.4")
    let nextSession = UpdatePresentation()
    nextSession.showAvailableUpdate("1.1.3")
    XCTAssertEqual(nextSession.availableVersion, "1.1.3")
  }
  func testNativeUpdateUIAndOptOutSuppressBanner() {
    let updates = UpdatePresentation()
    updates.showAvailableUpdate("1.1.3")
    updates.nativePresentationBegan()
    XCTAssertNil(updates.availableVersion)
    updates.showAvailableUpdate("1.1.4")
    XCTAssertNil(updates.availableVersion)
    updates.nativePresentationEnded()
    updates.automaticChecks = false
    updates.showAvailableUpdate("1.1.4")
    XCTAssertNil(updates.availableVersion)
  }
}

// This source intentionally ignores cancellation until finished by the test.
// It verifies late-result rejection rather than merely checking cooperative tasks.
private actor Controlled113Source: UsageSource {
  private(set) var calls = 0
  private(set) var maximumActive = 0
  private var active = 0
  private var pending: [CheckedContinuation<QueryResult, Never>] = []
  private var waiters: [(Int, CheckedContinuation<Void, Never>)] = []
  func query() async -> QueryResult {
    calls += 1
    active += 1
    maximumActive = max(maximumActive, active)
    let ready = waiters.filter { $0.0 <= calls }
    waiters.removeAll { $0.0 <= calls }
    for (_, waiter) in ready { waiter.resume() }
    let result = await withCheckedContinuation { pending.append($0) }
    active -= 1
    return result
  }
  func waitForCalls(_ count: Int) async {
    guard calls < count else { return }
    await withCheckedContinuation { waiters.append((count, $0)) }
  }
  func finishNext(_ result: QueryResult) { pending.removeFirst().resume(returning: result) }
}

@MainActor final class Release113ProviderTests: XCTestCase {
  private func result(_ binding: String) -> QueryResult {
    .success(.init(binding: binding, windows: [], date: Date()))
  }
  func testDisabledProviderNeverQueriesAndReenablingQueriesImmediately() async {
    let source = Controlled113Source()
    let store = UsageStore(sources: [.codex: source], enabledProviders: []) { _ in }
    await store.refresh()
    let disabledCalls = await source.calls
    XCTAssertEqual(disabledCalls, 0)
    await store.setEnabledProviders([.codex])
    await source.waitForCalls(1)
    await source.finishNext(result("enabled"))
    await store.waitForIdle()
    let snapshot = await store.snapshot()
    XCTAssertEqual(snapshot[.codex]?.reading?.binding, "enabled")
    await store.stop()
  }
  func testLateDisabledResultIsRejectedAndRapidReenableDoesNotOverlap() async {
    let source = Controlled113Source()
    let store = UsageStore(sources: [.codex: source]) { _ in }
    await store.refresh(.codex)
    await source.waitForCalls(1)
    await store.setEnabledProviders([])
    await store.setEnabledProviders([.codex])
    await source.finishNext(result("previous-selection"))
    await source.waitForCalls(2)
    let during = await store.snapshot()
    XCTAssertNil(during[.codex]?.reading)
    await source.finishNext(result("current-selection"))
    await store.waitForIdle()
    let snapshot = await store.snapshot()
    let maximumActive = await source.maximumActive
    XCTAssertEqual(snapshot[.codex]?.reading?.binding, "current-selection")
    XCTAssertEqual(maximumActive, 1)
    await store.stop()
  }
}

final class Release113AllowancePresentationTests: XCTestCase {
  private let observed = Date(timeIntervalSince1970: 2_000_000_000)
  private func window(_ id: String, provider: ProviderID = .codex, minutes: Int? = 300,
    used: Double? = 24.2, reset: Date? = nil, scope: UsageScope = .general) throws -> UsageWindow {
    try UsageWindow(id: id, bucket: provider.rawValue, label: "private-fixture-label",
      durationMinutes: minutes, used: used, reset: reset, scope: scope)
  }
  private func snapshot(_ provider: ProviderID = .codex, windows: [UsageWindow],
    availability: AllowanceAvailability = .reported) -> UsageSnapshot {
    var snapshot = UsageSnapshot(provider: provider)
    snapshot.apply(.success(.init(binding: "private-fixture-binding", windows: windows,
      date: observed, availability: availability)))
    return snapshot
  }
  func testBothWindowsAndAdditionalScopeShareRemainingAndResetDetails() throws {
    let five = try window("five", reset: observed.addingTimeInterval(3600))
    let weekly = try window("weekly", minutes: 10080, used: 12.8)
    let additional = try window("extra", used: 3, scope: .additional("Spark"))
    let opaque = try window("private-opaque-id", used: 0, scope: .unknown)
    let snapshot = snapshot(windows: [opaque, weekly, additional, five])
    XCTAssertEqual(snapshot.primary?.id, "five")
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "75%")
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).compactText, "75% left")
    XCTAssertEqual(snapshot.consumerWindows.map(\.id), ["five", "weekly", "extra"])
    let details = UsageCopy.detailSummary(snapshot, now: observed)
    XCTAssertEqual(details, ProviderGlance(snapshot: snapshot).accessibility(at: observed))
    for expected in ["General · 5 hours", "General · 7 days", "Spark · 5 hours",
      "75% remaining", "87% remaining", "97% remaining", "Resets in 1h 0m", "Reset:",
      "Reset not reported"] { XCTAssertTrue(details.contains(expected), expected) }
    for hidden in ["private-fixture-label", "private-fixture-binding", "private-opaque-id"] {
      XCTAssertFalse(details.contains(hidden))
    }
  }
  func testWeeklyOnlyClaudeProvidesCompactAndCompleteDetails() throws {
    let weekly = try window("seven_day", provider: .claude, minutes: 10080, used: 36.5)
    let snapshot = snapshot(.claude, windows: [weekly])
    XCTAssertEqual(snapshot.state, .live)
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "63%")
    XCTAssertEqual(snapshot.detailWindows.map(\.id), ["seven_day"])
    let details = UsageCopy.detailSummary(snapshot, now: observed)
    XCTAssertTrue(details.contains("General · 7 days"))
    XCTAssertTrue(details.contains("63% remaining"))
    XCTAssertFalse(details.contains("No general allowance"))
  }
  func testModelOnlyNeverSubstitutesCompactAndOpaqueNamesStayHidden() throws {
    let model = try window("seven_day_sonnet", provider: .claude, minutes: 10080,
      used: 5, scope: .model("Sonnet"))
    let opaque = try window("private-opaque-id", provider: .claude, scope: .unknown)
    let snapshot = snapshot(.claude, windows: [opaque, model])
    XCTAssertEqual(snapshot.state, .live)
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "--")
    let details = UsageCopy.detailSummary(snapshot, now: observed)
    XCTAssertTrue(details.contains("No general allowance reported."))
    XCTAssertTrue(details.contains("Sonnet · 7 days"))
    XCTAssertTrue(details.contains("95% remaining"))
    XCTAssertFalse(details.contains("private-opaque-id"))
  }
  func testEmptyUnknownAndAllMissingPercentAreDistinctFromLive() throws {
    let empty = snapshot(windows: [], availability: .notReported)
    let unknown = snapshot(windows: [try window("private-opaque-id", scope: .unknown)])
    let missing = snapshot(windows: [try window("five", used: nil)])
    XCTAssertEqual(empty.state, .notReported)
    XCTAssertEqual(unknown.state, .unsupportedAllowance)
    XCTAssertEqual(missing.state, .notReported)
    for item in [empty, unknown, missing] {
      let details = UsageCopy.detailSummary(item, now: observed)
      XCTAssertFalse(details.contains("Live"))
      XCTAssertFalse(details.contains("100%"))
      XCTAssertFalse(details.contains("private-opaque-id"))
      XCTAssertEqual(ProviderGlance(snapshot: item).percentage, "--")
      XCTAssertEqual(ProviderGlance(snapshot: item).compactText, "--")
    }
    XCTAssertTrue(UsageCopy.detailSummary(missing, now: observed).contains("Remaining not reported"))
  }
  func testMissingDurationPreservesReportedPercentWithoutCompactInference() throws {
    let snapshot = snapshot(windows: [try window("undated", minutes: nil)])
    XCTAssertEqual(snapshot.state, .live)
    XCTAssertNil(snapshot.primary)
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "--")
    let details = UsageCopy.detailSummary(snapshot, now: observed)
    XCTAssertTrue(details.contains("General time window isn’t reported."))
    XCTAssertTrue(details.contains("General · Window not reported"))
    XCTAssertTrue(details.contains("75% remaining"))
  }
  func testStaleDetailsShowObservationAgeAndAllResetInstants() throws {
    var snapshot = snapshot(windows: [try window("five", reset: observed.addingTimeInterval(60)),
      try window("weekly", minutes: 10080, used: 10)])
    snapshot.apply(.fail(.timeout, binding: "private-fixture-binding"))
    let now = observed.addingTimeInterval(180)
    XCTAssertEqual(snapshot.state, .stale)
    XCTAssertEqual(ProviderGlance(snapshot: snapshot).percentage, "75%")
    let details = UsageCopy.detailSummary(snapshot, now: now)
    XCTAssertTrue(details.contains("Stale · Observed 3m ago"))
    XCTAssertTrue(details.contains("General · 5 hours"))
    XCTAssertTrue(details.contains("General · 7 days"))
    XCTAssertTrue(details.contains("Reset:"))
    XCTAssertEqual(details, ProviderGlance(snapshot: snapshot).accessibility(at: now))
  }
  func testFailedRetrievalBillingAndNotReportedHaveSeparateCopy() {
    var failed = UsageSnapshot(provider: .codex)
    failed.apply(.fail(.timeout))
    var billing = UsageSnapshot(provider: .claude)
    billing.apply(.fail(.unsupportedBilling))
    let absent = snapshot(windows: [], availability: .notReported)
    XCTAssertEqual(UsageCopy.stateMessage(failed), "Couldn’t retrieve allowance. Retry to check.")
    XCTAssertEqual(UsageCopy.stateMessage(billing), "Llumi can’t monitor this billing mode.")
    XCTAssertEqual(UsageCopy.stateMessage(UsageSnapshot(provider: .codex, state: .unsupportedAllowance)),
      "Llumi can’t read this allowance format.")
    XCTAssertEqual(UsageCopy.stateMessage(absent), "No remaining time allowance was reported.")
  }
  func testExpandedHeightAdaptsAndBoundsManyWindowsWithoutTruncatingDetails() throws {
    let small = [ProviderGlance(snapshot: snapshot(windows: [try window("five")]))]
    let manyWindows = try (0..<12).map {
      try window("model-\($0)", minutes: 10080, used: Double($0), scope: .model("Model \($0)"))
    }
    let many = [ProviderGlance(snapshot: snapshot(windows: manyWindows))]
    XCTAssertGreaterThan(NotchDetailLayout.height(rows: many, availableHeight: 800),
      NotchDetailLayout.height(rows: small, availableHeight: 800))
    XCTAssertEqual(NotchDetailLayout.height(rows: many, availableHeight: 800), 420)
    XCTAssertEqual(NotchDetailLayout.height(rows: many, availableHeight: 160), 160)
    let details = many[0].accessibility(at: observed)
    for i in 0..<12 { XCTAssertTrue(details.contains("Model \(i) · 7 days")) }
  }
}
