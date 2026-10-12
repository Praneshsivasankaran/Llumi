import Foundation
import XCTest

@MainActor private final class TestNotificationDelivery: UsageNotificationDelivery {
  var current: UsageNotificationPermission = .unknown
  var answer: UsageNotificationPermission = .allowed
  var requestCount = 0
  var sent: [String] = []
  var sentBodies: [String] = []
  var attemptedCount = 0
  var fail = false
  var holdPermission = false
  var waiting: CheckedContinuation<UsageNotificationPermission, Never>?
  var holdNextSend = false
  var sending: CheckedContinuation<Void, Never>?
  func permission() async -> UsageNotificationPermission {
    if holdPermission { return await withCheckedContinuation { waiting = $0 } }
    return current
  }
  func requestPermission() async -> UsageNotificationPermission {
    requestCount += 1; current = answer; return answer
  }
  func send(id: String, title: String, body: String) async throws {
    attemptedCount += 1
    if holdNextSend {
      holdNextSend = false
      await withCheckedContinuation { sending = $0 }
    }
    try Task.checkCancellation()
    if fail { throw Failure.unavailable }
    sent.append(title)
    sentBodies.append(body)
  }
}

@MainActor final class UsageAlertControllerTests: XCTestCase {
  private func suite(_ body: @MainActor (UserDefaults) async throws -> Void) async throws {
    let name = "Llumi-alert-controller-" + UUID().uuidString
    let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
    defer { defaults.removePersistentDomain(forName: name) }
    try await body(defaults)
  }
  private func reading() throws -> [ProviderID: UsageSnapshot] {
    var value = UsageSnapshot(provider: .codex)
    value.apply(.success(.init(binding: "synthetic-account", windows: [try UsageWindow(
      id: "codex:primary", bucket: "codex", label: "General", durationMinutes: 300,
      used: 96, reset: Date().addingTimeInterval(3600))], date: Date())))
    return [.codex: value]
  }
  private func twoWindowReading(used: Double, reset: Date,
    binding: String = "synthetic-account", date: Date = Date()) throws -> [ProviderID: UsageSnapshot] {
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.apply(.success(.init(binding: binding, windows: [
      try UsageWindow(id: "codex:primary", bucket: "codex", label: "General",
        durationMinutes: 300, used: used, reset: reset),
      try UsageWindow(id: "codex:secondary", bucket: "codex", label: "General",
        durationMinutes: 10080, used: used, reset: reset.addingTimeInterval(3600)),
    ], date: date)))
    return [.codex: snapshot]
  }
  private func heldBatch(defaults: UserDefaults, delivery: TestNotificationDelivery) async throws
    -> (UsageAlertController, [ProviderID: UsageSnapshot], Date) {
    let reset = Date().addingTimeInterval(3600)
    delivery.current = .allowed
    let controller = UsageAlertController(defaults: defaults, delivery: delivery)
    controller.update(snapshots: try twoWindowReading(used: 0, reset: reset), enabledProviders: [.codex])
    controller.settings.enabled = true
    await controller.waitForIdle()
    delivery.holdNextSend = true
    let crossed = try twoWindowReading(used: 96, reset: reset)
    controller.update(snapshots: crossed, enabledProviders: [.codex])
    for _ in 0..<500 where delivery.sending == nil { await Task.yield() }
    XCTAssertNotNil(delivery.sending)
    return (controller, crossed, reset)
  }
  private func releaseSend(_ delivery: TestNotificationDelivery) {
    delivery.sending?.resume(); delivery.sending = nil
  }
  func testDefaultOffNeverRequestsPermissionOrDelivers() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      controller.update(snapshots: try reading(), enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertFalse(controller.settings.enabled)
      XCTAssertEqual(delivery.requestCount, 0)
      XCTAssertTrue(delivery.sent.isEmpty)
    }
  }
  func testOptInRequestsOnceAndDenialDoesNotSend() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery(); delivery.answer = .denied
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      controller.update(snapshots: try reading(), enabledProviders: [.codex])
      controller.settings.enabled = true
      await controller.waitForIdle()
      XCTAssertEqual(delivery.requestCount, 1)
      XCTAssertEqual(controller.permission, .denied)
      XCTAssertTrue(delivery.sent.isEmpty)
      controller.update(snapshots: try reading(), enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertEqual(delivery.requestCount, 1)
    }
  }
  func testAuthorizedDeliveryIsDeduplicatedAndSettingsPersist() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      let snapshot = try reading()
      controller.update(snapshots: snapshot, enabledProviders: [.codex])
      controller.settings.enabled = true
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 1)
      controller.update(snapshots: snapshot, enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 1)
      let restarted = UsageAlertController(defaults: defaults, delivery: delivery)
      XCTAssertTrue(restarted.settings.enabled)
      restarted.update(snapshots: snapshot, enabledProviders: [.codex])
      await restarted.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 1)
    }
  }
  func testUnavailableDeliveryReportsSafeErrorWithoutRepeatingReceipt() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery(); delivery.current = .allowed; delivery.fail = true
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      let snapshot = try reading()
      controller.update(snapshots: snapshot, enabledProviders: [.codex])
      controller.settings.enabled = true
      await controller.waitForIdle()
      XCTAssertNotNil(controller.message)
      delivery.fail = false
      controller.update(snapshots: snapshot, enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertTrue(delivery.sent.isEmpty)
    }
  }
  func testDisablingBeforePermissionCheckCompletesCannotPrompt() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery(); delivery.holdPermission = true
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      controller.settings.enabled = true
      for _ in 0..<100 where delivery.waiting == nil { await Task.yield() }
      XCTAssertNotNil(delivery.waiting)
      controller.settings.enabled = false
      delivery.waiting?.resume(returning: .unknown); delivery.waiting = nil
      await controller.waitForIdle()
      XCTAssertEqual(delivery.requestCount, 0)
      XCTAssertTrue(delivery.sent.isEmpty)
    }
  }
  func testQuitCancelsPendingTestNotification() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery(); delivery.holdPermission = true
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      controller.sendTestNotification()
      for _ in 0..<100 where delivery.waiting == nil { await Task.yield() }
      XCTAssertNotNil(delivery.waiting)
      controller.stop()
      delivery.waiting?.resume(returning: .allowed); delivery.waiting = nil
      await controller.waitForIdle()
      XCTAssertTrue(delivery.sent.isEmpty)
      XCTAssertNil(controller.testMessage)
    }
  }
  func testTestResultSurvivesProviderRefreshWithoutClaimingBanner() async throws {
    for failing in [false, true] {
      try await suite { defaults in
        let delivery = TestNotificationDelivery(); delivery.current = .allowed
        let controller = UsageAlertController(defaults: defaults, delivery: delivery)
        let reset = Date().addingTimeInterval(3600)
        controller.update(snapshots: try twoWindowReading(used: 0, reset: reset), enabledProviders: [.codex])
        controller.settings.enabled = true
        await controller.waitForIdle()
        delivery.fail = failing
        controller.sendTestNotification()
        await controller.waitForIdle()
        let result = try XCTUnwrap(controller.testMessage)
        if failing {
          XCTAssertTrue(result.contains("request failed"))
          XCTAssertTrue(delivery.sent.isEmpty)
        } else {
          XCTAssertTrue(result.contains("accepted by macOS"))
          XCTAssertTrue(result.contains("Focus"))
          XCTAssertTrue(result.contains("sharing the display"))
          XCTAssertFalse(result.contains("notification sent"))
          XCTAssertEqual(delivery.sent.count, 1)
        }
        delivery.fail = false
        controller.update(snapshots: try twoWindowReading(used: 1, reset: reset), enabledProviders: [.codex])
        await controller.waitForIdle()
        XCTAssertNil(controller.message)
        XCTAssertEqual(controller.testMessage, result)
        XCTAssertTrue(controller.settings.enabled)
      }
    }
  }
  func testDeniedTestReportsActionWithoutSendingOrEnablingAlerts() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery(); delivery.answer = .denied
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      controller.sendTestNotification()
      await controller.waitForIdle()
      XCTAssertEqual(delivery.requestCount, 1)
      XCTAssertEqual(controller.permission, .denied)
      XCTAssertEqual(delivery.attemptedCount, 0)
      XCTAssertTrue(try XCTUnwrap(controller.testMessage).contains("Enable notifications in System Settings"))
      XCTAssertFalse(controller.settings.enabled)
      let result = controller.testMessage
      controller.update(snapshots: try reading(), enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertEqual(controller.testMessage, result)
      XCTAssertTrue(delivery.sent.isEmpty)
    }
  }
  func testQuitDuringTestSendCannotReportAcceptedOrFailure() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery(); delivery.current = .allowed; delivery.holdNextSend = true
      let controller = UsageAlertController(defaults: defaults, delivery: delivery)
      controller.sendTestNotification()
      for _ in 0..<500 where delivery.sending == nil { await Task.yield() }
      XCTAssertNotNil(delivery.sending)
      controller.stop()
      releaseSend(delivery)
      await controller.waitForIdle()
      XCTAssertNil(controller.testMessage)
      XCTAssertTrue(delivery.sent.isEmpty)
    }
  }
  func testCommittedBatchSurvivesSameAccountPublicationDuringFirstSend() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let (controller, _, reset) = try await heldBatch(defaults: defaults, delivery: delivery)
      let refreshed = try twoWindowReading(used: 97, reset: reset)
      controller.update(snapshots: refreshed, enabledProviders: [.codex])
      releaseSend(delivery)
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 2)
      XCTAssertTrue(delivery.sentBodies.contains { $0.contains("5-hour window") })
      XCTAssertTrue(delivery.sentBodies.contains { $0.contains("7-day window") })
      controller.update(snapshots: refreshed, enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 2)
    }
  }
  func testCheckingPublicationDefersCommittedRemainingNoticeUntilSuccess() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let (controller, crossed, _) = try await heldBatch(defaults: defaults, delivery: delivery)
      var checking = crossed
      checking[.codex]?.refreshStatus.checking = true
      controller.update(snapshots: checking, enabledProviders: [.codex])
      releaseSend(delivery)
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 1)
      controller.update(snapshots: crossed, enabledProviders: [.codex])
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 2)
    }
  }
  func testAccountSwitchCancelsCommittedOldAccountBatch() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let (controller, _, reset) = try await heldBatch(defaults: defaults, delivery: delivery)
      controller.update(snapshots: try twoWindowReading(used: 0, reset: reset,
        binding: "synthetic-other-account"), enabledProviders: [.codex])
      releaseSend(delivery)
      await controller.waitForIdle()
      XCTAssertTrue(delivery.sent.isEmpty)
      XCTAssertEqual(delivery.attemptedCount, 1)
    }
  }
  func testStaleAndExpiredPublicationsCancelCommittedBatch() async throws {
    for stale in [true, false] {
      try await suite { defaults in
        let delivery = TestNotificationDelivery()
        let (controller, crossed, reset) = try await heldBatch(defaults: defaults, delivery: delivery)
        var invalid = crossed
        if stale {
          invalid[.codex]?.apply(.fail(.unavailable, binding: "synthetic-account"))
        } else {
          invalid = try twoWindowReading(used: 96, reset: reset,
            date: Date().addingTimeInterval(-121))
        }
        controller.update(snapshots: invalid, enabledProviders: [.codex])
        releaseSend(delivery)
        await controller.waitForIdle()
        XCTAssertTrue(delivery.sent.isEmpty)
        XCTAssertEqual(delivery.attemptedCount, 1)
      }
    }
  }
  func testRemovedWindowCannotDeliverItsCommittedNotice() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let (controller, crossed, _) = try await heldBatch(defaults: defaults, delivery: delivery)
      var reduced = crossed
      let reading = try XCTUnwrap(reduced[.codex]?.reading)
      reduced[.codex]?.apply(.success(.init(binding: reading.binding,
        windows: Array(reading.windows.prefix(1)), date: Date())))
      controller.update(snapshots: reduced, enabledProviders: [.codex])
      releaseSend(delivery)
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 1)
      XCTAssertFalse(delivery.sentBodies.contains { $0.contains("7-day window") })
    }
  }
  func testEqualUsageAndResetDoNotConflateIndependentWindowOrigins() async throws {
    for removeFirst in [false, true] {
      try await suite { defaults in
        let delivery = TestNotificationDelivery(); delivery.current = .allowed
        let controller = UsageAlertController(defaults: defaults, delivery: delivery)
        let reset = Date().addingTimeInterval(3600)
        func snapshot(used: Double) throws -> UsageSnapshot {
          var value = UsageSnapshot(provider: .codex)
          value.apply(.success(.init(binding: "synthetic-account", windows: [
            try UsageWindow(id: "codex:primary", bucket: "codex", label: "General",
              durationMinutes: 300, used: used, reset: reset),
            try UsageWindow(id: "model:a", bucket: "codex", label: "Fixture A",
              durationMinutes: 300, used: used, reset: reset, scope: .model("Fixture A")),
            try UsageWindow(id: "model:b", bucket: "codex", label: "Fixture B",
              durationMinutes: 300, used: used, reset: reset, scope: .model("Fixture B")),
          ], date: Date())))
          return value
        }
        controller.update(snapshots: [.codex: try snapshot(used: 0)], enabledProviders: [.codex])
        controller.settings.enabled = true
        await controller.waitForIdle()
        delivery.holdNextSend = true
        let crossed = try snapshot(used: 96)
        controller.update(snapshots: [.codex: crossed], enabledProviders: [.codex])
        for _ in 0..<500 where delivery.sending == nil { await Task.yield() }
        XCTAssertNotNil(delivery.sending)
        let reading = try XCTUnwrap(crossed.reading)
        var reduced = UsageSnapshot(provider: .codex)
        reduced.apply(.success(.init(binding: reading.binding, windows: reading.windows.filter {
          $0.id != (removeFirst ? "codex:primary" : "model:a")
        }, date: Date())))
        controller.update(snapshots: [.codex: reduced], enabledProviders: [.codex])
        releaseSend(delivery)
        await controller.waitForIdle()
        XCTAssertEqual(delivery.sent.count, 2)
        XCTAssertTrue(delivery.sentBodies.contains { $0.contains("Fixture B") })
        if removeFirst {
          XCTAssertTrue(delivery.sentBodies.contains { $0.contains("Fixture A") })
        } else {
          XCTAssertFalse(delivery.sentBodies.contains { $0.contains("Fixture A") })
          XCTAssertTrue(delivery.sentBodies.contains { $0.hasPrefix("5-hour window:") })
        }
      }
    }
  }
  func testCommittedOriginAcceptsValidatedCodexLegacySlotAlias() async throws {
    try await suite { defaults in
      let delivery = TestNotificationDelivery()
      let (controller, crossed, _) = try await heldBatch(defaults: defaults, delivery: delivery)
      let reading = try XCTUnwrap(crossed[.codex]?.reading)
      let windows = try reading.windows.map {
        try UsageWindow(id: $0.id.replacingOccurrences(of: "codex:", with: "codex:legacy:"),
          bucket: $0.bucket, label: $0.label, durationMinutes: $0.durationMinutes,
          used: $0.used, reset: $0.reset, scope: $0.scope)
      }
      var aliased = UsageSnapshot(provider: .codex)
      aliased.apply(.success(.init(binding: reading.binding, windows: windows, date: Date())))
      controller.update(snapshots: [.codex: aliased], enabledProviders: [.codex])
      releaseSend(delivery)
      await controller.waitForIdle()
      XCTAssertEqual(delivery.sent.count, 2)
    }
  }
  func testDisableAndQuitCancelCommittedRemainingNotices() async throws {
    for quitting in [false, true] {
      try await suite { defaults in
        let delivery = TestNotificationDelivery()
        let (controller, crossed, _) = try await heldBatch(defaults: defaults, delivery: delivery)
        if quitting { controller.stop() } else { controller.settings.enabled = false }
        releaseSend(delivery)
        await controller.waitForIdle()
        XCTAssertTrue(delivery.sent.isEmpty)
        XCTAssertEqual(delivery.attemptedCount, 1)
        if !quitting {
          controller.settings.enabled = true
          controller.update(snapshots: crossed, enabledProviders: [.codex])
          await controller.waitForIdle()
          XCTAssertTrue(delivery.sent.isEmpty)
        }
      }
    }
  }
}
