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
