import Foundation
import XCTest

private let alertTestNow = Date(timeIntervalSince1970: 2_000_000_000)

@MainActor private struct AlertTestStorage {
  let suite = "llumi-alert-tests-" + UUID().uuidString
  let defaults: UserDefaults
  init() { defaults = UserDefaults(suiteName: suite)! }
  func remove() { defaults.removePersistentDomain(forName: suite) }
}

@MainActor final class UsageAlertTests: XCTestCase {
  private func settings() -> UsageAlertSettings {
    var settings = UsageAlertSettings()
    settings.enabled = true
    return settings
  }
  private func window(
    _ id: String = "primary", used: Double? = 80,
    reset: Date? = alertTestNow.addingTimeInterval(3600),
    minutes: Int? = 300, scope: UsageScope = .general
  ) throws -> UsageWindow {
    try UsageWindow(id: id, bucket: "codex", label: "synthetic-private-window-label",
      durationMinutes: minutes, used: used, reset: reset, scope: scope)
  }
  private func snapshot(
    provider: ProviderID = .codex, binding: String = "synthetic-private-account-A",
    used: Double? = 80, reset: Date? = alertTestNow.addingTimeInterval(3600),
    date: Date = alertTestNow, windows: [UsageWindow]? = nil
  ) throws -> UsageSnapshot {
    var snapshot = UsageSnapshot(provider: provider)
    snapshot.apply(.success(Reading(binding: binding,
      windows: try windows ?? [window(used: used, reset: reset)], date: date)))
    return snapshot
  }
  private func evaluate(
    _ evaluator: UsageAlertEvaluator, _ snapshot: UsageSnapshot,
    settings: UsageAlertSettings? = nil, enabledProviders: Set<ProviderID> = Set(ProviderID.allCases),
    at now: Date = alertTestNow
  ) -> [UsageAlertNotice] {
    evaluator.evaluate(snapshots: [snapshot.provider: snapshot], settings: settings ?? self.settings(),
      enabledProviders: enabledProviders, at: now)
  }

  func testDefaultsAreOffWithEditablePerProviderThresholds() {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let defaults = UsageAlertPersistence.loadSettings(defaults: storage.defaults)
    XCTAssertFalse(defaults.enabled)
    for provider in ProviderID.allCases {
      XCTAssertTrue(defaults.provider(provider).enabled)
      XCTAssertEqual(defaults.provider(provider).thresholds.map(\.percent), [75, 90, 95])
      XCTAssertTrue(defaults.provider(provider).thresholds.allSatisfy(\.enabled))
    }
    var changed = defaults
    changed.enabled = true
    changed.providers[.codex]?.thresholds[0].percent = 70
    changed.providers[.claude]?.enabled = false
    XCTAssertTrue(UsageAlertPersistence.saveSettings(changed, defaults: storage.defaults))
    XCTAssertEqual(UsageAlertPersistence.loadSettings(defaults: storage.defaults), changed)
  }

  func testInvalidAndCorruptSettingsFailClosed() {
    let storage = AlertTestStorage(); defer { storage.remove() }
    var invalid = settings()
    for value in [-1, 0, 101, Int.max] {
      invalid.providers[.codex]?.thresholds[0].percent = value
      XCTAssertFalse(invalid.isValid)
      XCTAssertFalse(invalid.sanitized().enabled)
      XCTAssertTrue(UsageAlertPersistence.saveSettings(invalid, defaults: storage.defaults))
      XCTAssertFalse(UsageAlertPersistence.loadSettings(defaults: storage.defaults).enabled)
    }
    invalid = settings()
    invalid.providers[.codex]?.thresholds[0].percent = 90
    XCTAssertFalse(invalid.isValid)
    invalid = settings()
    let duplicateID = invalid.provider(.codex).thresholds[1].id
    invalid.providers[.codex]?.thresholds[0].id = duplicateID
    XCTAssertFalse(invalid.isValid)
    invalid = settings()
    invalid.providers[.codex]?.thresholds = (1...9).map { UsageAlertThreshold(percent: $0) }
    XCTAssertFalse(invalid.isValid)
    for object: Any in ["true", Data("{\"enabled\":true}".utf8),
      Data(repeating: 0, count: UsageAlertPersistence.maximumSettingsBytes + 1)] {
      storage.defaults.set(object, forKey: UsageAlertPersistence.settingsKey)
      XCTAssertFalse(UsageAlertPersistence.loadSettings(defaults: storage.defaults).enabled)
    }
  }

  func testCrossingReturnsHighestAndAcknowledgesLowerThresholds() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 74)).isEmpty)
    let crossed = evaluate(evaluator, try snapshot(used: 96))
    XCTAssertEqual(crossed.map(\.thresholdPercent), [95])
    XCTAssertEqual(crossed.first?.windowLabel, "5-hour window")
    XCTAssertFalse(crossed.first!.body.contains("synthetic-private"))
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96)).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 80)).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 94)).isEmpty)
  }

  func testExactThresholdAndLaterHigherThresholdCrossings() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 74.99)).isEmpty)
    for percent in [75, 90, 95] {
      XCTAssertEqual(evaluate(evaluator, try snapshot(used: Double(percent)))
        .map(\.thresholdPercent), [percent])
      XCTAssertTrue(evaluate(evaluator, try snapshot(used: Double(percent))).isEmpty)
    }
  }

  func testFirstOptInIsOneTotalNoticeAndConsumesAllOtherCandidates() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let windows = try [window("short", used: 96), window("weekly", used: 96, minutes: 10080)]
    let snapshots: [ProviderID: UsageSnapshot] = [
      .codex: try snapshot(windows: windows), .claude: try snapshot(provider: .claude, used: 96),
    ]
    let notices = evaluator.evaluate(snapshots: snapshots, settings: settings(),
      enabledProviders: Set(ProviderID.allCases), at: alertTestNow)
    XCTAssertEqual(notices.count, 1)
    XCTAssertEqual(notices.first?.thresholdPercent, 95)
    XCTAssertEqual(notices.first?.provider, .claude)
    XCTAssertTrue(evaluator.evaluate(snapshots: snapshots, settings: settings(),
      enabledProviders: Set(ProviderID.allCases), at: alertTestNow).isEmpty)
    let restarted = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(restarted.evaluate(snapshots: snapshots, settings: settings(),
      enabledProviders: Set(ProviderID.allCases), at: alertTestNow).isEmpty)
  }

  func testFirstOptInWaitsForAnEligibleFreshReadingBeforeConsumingItsSingleNotice() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    var stale = try snapshot(used: 96); stale.state = .stale
    XCTAssertTrue(evaluate(evaluator, stale).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96,
      date: alertTestNow.addingTimeInterval(-121))).isEmpty)
    let snapshots: [ProviderID: UsageSnapshot] = [
      .codex: try snapshot(windows: [window("short", used: 96), window("weekly", used: 96, minutes: 10080)]),
      .claude: try snapshot(provider: .claude, used: 96),
    ]
    XCTAssertEqual(evaluator.evaluate(snapshots: snapshots, settings: settings(),
      enabledProviders: Set(ProviderID.allCases), at: alertTestNow).count, 1)
    XCTAssertTrue(evaluator.evaluate(snapshots: snapshots, settings: settings(),
      enabledProviders: Set(ProviderID.allCases), at: alertTestNow).isEmpty)
  }

  func testRegularRefreshTracksEachWindowAndProviderIndependently() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let windows = try [window("short", used: 0), window("weekly", used: 0, minutes: 10080)]
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: windows)).isEmpty)
    let crossed = try [window("short", used: 90), window("weekly", used: 75, minutes: 10080)]
    let notices = evaluator.evaluate(snapshots: [
      .codex: try snapshot(windows: crossed), .claude: try snapshot(provider: .claude, used: 96),
    ], settings: settings(), enabledProviders: Set(ProviderID.allCases), at: alertTestNow)
    XCTAssertEqual(notices.count, 3)
    XCTAssertEqual(Set(notices.filter { $0.provider == .codex }.map(\.thresholdPercent)), [75, 90])
    XCTAssertEqual(Set(notices.filter { $0.provider == .codex }.map(\.windowLabel)),
      ["5-hour window", "7-day window"])
  }

  func testRestartsAndAccountSwitchesRetainReceiptsWithoutRawBindings() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let a = try snapshot(used: 90)
    XCTAssertEqual(evaluate(evaluator, a).map(\.thresholdPercent), [90])
    let b = try snapshot(binding: "synthetic-private-account-B", used: 95)
    XCTAssertEqual(evaluate(evaluator, b).map(\.thresholdPercent), [95])
    XCTAssertTrue(evaluate(evaluator, a).isEmpty)
    let restarted = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(restarted, a).isEmpty)
    XCTAssertTrue(evaluate(restarted, b).isEmpty)
    let data = try XCTUnwrap(storage.defaults.data(forKey: UsageAlertPersistence.receiptsKey))
    let stored = String(decoding: data, as: UTF8.self)
    for privateValue in ["synthetic-private-account", "synthetic-private-window-label", "primary", "codex"] {
      XCTAssertFalse(stored.contains(privateValue))
    }
    XCTAssertLessThanOrEqual(data.count, UsageAlertEvaluator.maximumReceiptBytes)
  }

  func testDisableReenableDoesNotRepeatPreviouslyAcknowledgedThresholds() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 90)).map(\.thresholdPercent), [90])
    var off = settings(); off.enabled = false
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 97), settings: off).isEmpty)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 97)).map(\.thresholdPercent), [95])
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 97), settings: off).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 97)).isEmpty)
  }

  func testDisabledIndividualThresholdsAndLowerEditsCannotCauseDelayedBurst() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    var control = settings()
    control.providers[.codex]?.thresholds[2].enabled = false
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96), settings: control)
      .map(\.thresholdPercent), [90])
    control.providers[.codex]?.thresholds[2].enabled = true
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96), settings: control).isEmpty)
    control.providers[.codex]?.thresholds[2].percent = 92
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96), settings: control).isEmpty)
    control.providers[.codex]?.thresholds[2].percent = 98
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 98), settings: control)
      .map(\.thresholdPercent), [98])
  }

  func testAllIndividualThresholdsOffStillAcknowledgesCrossedValues() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    var control = settings()
    let disabledThresholds = control.provider(.codex).thresholds.map {
      var threshold = $0; threshold.enabled = false; return threshold
    }
    control.providers[.codex]?.thresholds = disabledThresholds
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96), settings: control).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96)).isEmpty)
  }

  func testProviderControlsAndMonitoringSelectionAreRespected() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    var control = settings(); control.providers[.codex]?.enabled = false
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96), settings: control).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96), enabledProviders: []).isEmpty)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96)).count, 1)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96), settings: control).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96)).isEmpty)
  }

  func testStaleFailedAndUnverifiedSnapshotsNeverAlert() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    for state in [ProviderState.stale, .loading, .signedOut, .unavailable, .notReported] {
      var changed = try snapshot(used: 96); changed.state = state
      XCTAssertTrue(evaluate(evaluator, changed).isEmpty)
    }
    var changed = try snapshot(used: 96); changed.authenticationVerified = false
    XCTAssertTrue(evaluate(evaluator, changed).isEmpty)
    changed = try snapshot(used: 96); changed.failure = .timeout
    XCTAssertTrue(evaluate(evaluator, changed).isEmpty)
    changed = try snapshot(used: 96); changed.refreshStatus.checking = true
    XCTAssertTrue(evaluate(evaluator, changed).isEmpty)
    changed = try snapshot(used: 96)
    changed.apply(.fail(.timeout, binding: "synthetic-private-account-A"))
    XCTAssertEqual(changed.state, .stale)
    XCTAssertTrue(evaluate(evaluator, changed).isEmpty)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96)).count, 1)
  }

  func testFreshAgeAndFutureClockToleranceHaveExplicitBounds() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96,
      date: alertTestNow.addingTimeInterval(-121))).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96,
      date: alertTestNow.addingTimeInterval(6))).isEmpty)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96,
      date: alertTestNow.addingTimeInterval(-120))).count, 1)
    XCTAssertEqual(evaluate(evaluator, try snapshot(binding: "synthetic-clock-B", used: 96,
      date: alertTestNow.addingTimeInterval(5))).count, 1)
  }

  func testUnknownElapsedUnsupportedAndAmbiguousWindowsNeverAlert() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    for reset in [nil, alertTestNow, alertTestNow.addingTimeInterval(-1),
      alertTestNow.addingTimeInterval(367 * 24 * 60 * 60)] {
      XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96, reset: reset)).isEmpty)
    }
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: nil)).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(binding: "", used: 96)).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: [window(used: 96, scope: .unknown)])).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: [window(used: 96), window(used: 90)])).isEmpty)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96)).count, 1)
  }

  func testFutureResetJitterAndBackwardResetDoNotRearm() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let reset = alertTestNow.addingTimeInterval(3600)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 80, reset: reset))
      .map(\.thresholdPercent), [75])
    for changed in [reset.addingTimeInterval(1), reset.addingTimeInterval(-1)] {
      XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96, reset: changed)).isEmpty)
    }
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96, reset: reset))
      .map(\.thresholdPercent), [95])
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96, reset: reset)).isEmpty)
  }

  func testOnlyFreshProviderSuccessorAfterOldBoundaryRearms() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let reset = alertTestNow.addingTimeInterval(10), nextReset = alertTestNow.addingTimeInterval(3610)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96, reset: reset)).count, 1)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96, reset: nextReset)).isEmpty)
    let after = reset.addingTimeInterval(1)
    XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96, reset: nextReset,
      date: reset.addingTimeInterval(-1)), at: after).isEmpty)
    let restarted = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(restarted, try snapshot(used: 96, reset: nextReset,
      date: reset.addingTimeInterval(-1)), at: after).isEmpty)
    XCTAssertEqual(evaluate(restarted, try snapshot(used: 96, reset: nextReset,
      date: after), at: after).map(\.thresholdPercent), [95])
    XCTAssertTrue(evaluate(restarted, try snapshot(used: 96, reset: nextReset,
      date: after), at: after).isEmpty)
  }

  func testSameWindowShapeChangeCannotRearmAtSameReset() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 80)).count, 1)
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: [window(used: 96, minutes: 10080)])).isEmpty)
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: [window(used: 96, scope: .model("Synthetic"))])).isEmpty)
    XCTAssertEqual(evaluate(evaluator, try snapshot(used: 96)).map(\.thresholdPercent), [95])
  }

  func testCodexLegacyAliasChangeFromUnrelatedSparkBucketCannotRepeatCoreAlert() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let legacy = #"{"rateLimits":{"primary":{"usedPercent":96,"windowDurationMins":300,"resetsAt":2000003600},"secondary":{"usedPercent":96,"windowDurationMins":10080,"resetsAt":2000007200}}}"#
    let withSpark = #"{"rateLimitsByLimitId":{"codex_bengalfox":{"limitName":"Spark","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":2000003600}}},"rateLimits":{"primary":{"usedPercent":96,"windowDurationMins":300,"resetsAt":2000003600},"secondary":{"usedPercent":96,"windowDurationMins":10080,"resetsAt":2000007200}}}"#
    let before = try Parsers.codex(J.parse(Data(legacy.utf8)))
    let after = try Parsers.codex(J.parse(Data(withSpark.utf8)))
    XCTAssertEqual(Set(before.map(\.id)), ["codex:primary", "codex:secondary"])
    XCTAssertTrue(after.contains { $0.id == "codex:legacy:primary" })
    XCTAssertEqual(evaluate(evaluator, try snapshot(windows: before)).count, 1)
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: after)).isEmpty)
    let restarted = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(restarted, try snapshot(windows: after)).isEmpty)
    XCTAssertTrue(evaluate(restarted, try snapshot(windows: before)).isEmpty)
  }

  func testCodexSameDurationSlotsAndEscapedBucketNamesRemainSeparate() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let below = #"{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":2000003600},"secondary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":2000003600}}}"#
    let crossed = #"{"rateLimits":{"primary":{"usedPercent":90,"windowDurationMins":300,"resetsAt":2000003600},"secondary":{"usedPercent":95,"windowDurationMins":300,"resetsAt":2000003600}}}"#
    XCTAssertTrue(evaluate(evaluator, try snapshot(windows: Parsers.codex(J.parse(Data(below.utf8))))).isEmpty)
    let notices = evaluate(evaluator, try snapshot(windows: Parsers.codex(J.parse(Data(crossed.utf8)))))
    XCTAssertEqual(notices.count, 2)
    XCTAssertEqual(Set(notices.map(\.thresholdPercent)), [90, 95])
    let separateBuckets = #"{"rateLimitsByLimitId":{"foo:legacy":{"primary":{"usedPercent":90,"windowDurationMins":300,"resetsAt":2000003600}},"foo%3Alegacy":{"primary":{"usedPercent":95,"windowDurationMins":300,"resetsAt":2000003600}}},"rateLimits":{"limitId":"foo","primary":{"usedPercent":90,"windowDurationMins":300,"resetsAt":2000003600}}}"#
    XCTAssertEqual(evaluate(evaluator, try snapshot(windows: Parsers.codex(J.parse(Data(separateBuckets.utf8))))).count, 3)
  }

  func testCorruptAndOversizedReceiptsSuppressDeliveryWithoutDiscardingHistory() throws {
    for corrupt in [Data("{\"version\":1,\"records\":{\"raw-private-binding\":{\"shape\":\"bad\",\"reset\":1,\"consumedThrough\":0}}}".utf8),
      Data("{\"version\":2,\"records\":{}}".utf8),
      Data(repeating: 0, count: UsageAlertEvaluator.maximumReceiptBytes + 1)] {
      let storage = AlertTestStorage(); defer { storage.remove() }
      storage.defaults.set(corrupt, forKey: UsageAlertPersistence.receiptsKey)
      let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
      XCTAssertFalse(evaluator.persistenceAvailable)
      XCTAssertNotNil(evaluator.statusMessage)
      XCTAssertTrue(evaluate(evaluator, try snapshot(used: 96)).isEmpty)
      XCTAssertEqual(storage.defaults.data(forKey: UsageAlertPersistence.receiptsKey), corrupt)
    }
  }

  func testReceiptCapacityDoesNotEvictActiveAccountsOrRepeatKnownAlerts() throws {
    let storage = AlertTestStorage(); defer { storage.remove() }
    let evaluator = UsageAlertEvaluator(defaults: storage.defaults)
    let windows = try (0..<UsageAlertEvaluator.maximumRecords).map {
      try window("synthetic-window-\($0)", used: 80)
    }
    XCTAssertEqual(evaluate(evaluator, try snapshot(windows: windows)).count, 1)
    XCTAssertTrue(evaluate(evaluator, try snapshot(binding: "synthetic-new-account", used: 96)).isEmpty)
    XCTAssertNotNil(evaluator.statusMessage)
    let existing = try [window("synthetic-window-0", used: 96)]
    XCTAssertEqual(evaluate(evaluator, try snapshot(windows: existing)).map(\.thresholdPercent), [95])
    let restarted = UsageAlertEvaluator(defaults: storage.defaults)
    XCTAssertTrue(evaluate(restarted, try snapshot(windows: existing)).isEmpty)
    let after = alertTestNow.addingTimeInterval(3600 + UsageAlertEvaluator.maximumReadingAge + 6)
    XCTAssertEqual(evaluate(restarted, try snapshot(binding: "synthetic-new-account", used: 96,
      reset: after.addingTimeInterval(3600), date: after), at: after).count, 1)
    XCTAssertLessThanOrEqual(storage.defaults.data(forKey: UsageAlertPersistence.receiptsKey)!.count,
      UsageAlertEvaluator.maximumReceiptBytes)
  }
}
