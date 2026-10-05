import CoreGraphics
import Foundation
import XCTest
import SwiftUI
import AppKit

// Opt-in native render review. Fixtures live in an isolated preference suite and
// never enter UsageStore, a provider query, or the production preference domain.
@MainActor final class AllowanceCaptureTests: XCTestCase {
  func testSuccessfulMissingDataDiagnosticsRemainSuccessfulAndRedacted() {
    for availability in [AllowanceAvailability.notReported, .unsupportedAllowance] {
      var snapshot = UsageSnapshot(provider: .codex)
      snapshot.apply(.success(.init(binding: "private-account-fixture", windows: [], date: Date(),
        availability: availability)))
      let diagnostic = SetupDiagnostic(snapshot)
      XCTAssertEqual(diagnostic.authentication, "verified")
      XCTAssertEqual(diagnostic.result, "success")
      XCTAssertEqual(diagnostic.usage, availability == .notReported ? "not-reported" : "unsupported")
      let report = SetupDiagnostics.report([.codex: snapshot], version: "1.1.3", build: "3")
      XCTAssertFalse(report.contains("private-account-fixture"))
    }
  }
  func testReadOnlyProviderEvidence() async throws {
    let directory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
      .appendingPathComponent("Llumi/AllowanceReview", isDirectory: true)
    guard FileManager.default.fileExists(atPath: directory.appendingPathComponent("live-request").path)
    else { throw XCTSkip("Real-account evidence requires a separate local read-only opt-in.") }
    let discovery = ProviderDiscovery()
    var evidence: [[String: Any]] = []
    for provider in ProviderID.allCases {
      let result = await ProviderAdapter(provider: provider, discovery: discovery).query()
      var snapshot = UsageSnapshot(provider: provider)
      snapshot.apply(result)
      let windows: [[String: Any]] = snapshot.consumerWindows.map { window in
        let scope: String
        switch window.scope {
        case .general: scope = "general"
        case .model: scope = "model"
        case .additional: scope = "additional"
        case .unknown: scope = "unknown"
        }
        // No identities, binding hashes, provider-controlled names or balances.
        return ["scope": scope, "minutes": window.durationMinutes as Any? ?? NSNull(),
          "usedPercent": window.used as Any? ?? NSNull(),
          "remainingPercent": window.remaining as Any? ?? NSNull(),
          "reset": window.reset?.timeIntervalSince1970 as Any? ?? NSNull()]
      }
      evidence.append(["provider": provider.rawValue, "state": snapshot.state.rawValue,
        "failure": result.failure?.rawValue as Any? ?? NSNull(),
        "accountVerified": result.verifiedBinding != nil, "windows": windows,
        "compactMinutes": snapshot.primary?.durationMinutes as Any? ?? NSNull(),
        "unrecognizedWindowCount": (snapshot.reading?.windows.filter { !$0.isSupported }.count ?? 0)])
    }
    let data = try JSONSerialization.data(withJSONObject: ["kind": "real-account",
      "observedAt": Date().timeIntervalSince1970, "providers": evidence], options: [.prettyPrinted, .sortedKeys])
    try data.write(to: directory.appendingPathComponent("live-normalized.json"), options: .atomic)
  }

  func testIsolatedAllowanceReviewScreens() async throws {
    let directory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
      .appendingPathComponent("Llumi/AllowanceReview", isDirectory: true)
    guard FileManager.default.fileExists(atPath: directory.appendingPathComponent("capture-request").path)
    else { throw XCTSkip("Native fixture screenshots are opt-in local review artifacts.") }
    // The standalone XCTest runner has no app asset catalog. Resolve the real
    // sibling build's images for this render process without modifying a bundle.
    let products = Bundle(for: Self.self).bundleURL.deletingLastPathComponent()
    let app = try XCTUnwrap(Bundle(url: products.appendingPathComponent("Llumi.app")))
    for name in ["CodexLogo", "ClaudeLogo", "ClaudeCodeMascot"] {
      XCTAssertNotNil(app.image(forResource: name))
    }
    let suite = "Llumi-Allowance-Render-" + UUID().uuidString
    let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    defer { defaults.removePersistentDomain(forName: suite) }
    let model = Presentation(defaults: defaults)
    let now = Date()
    let fiveReset = now.addingTimeInterval(7_200)
    let weeklyReset = now.addingTimeInterval(345_600)
    func window(_ p: ProviderID, _ id: String, _ minutes: Int, _ used: Double,
      _ scope: UsageScope = .general) throws -> UsageWindow {
      try UsageWindow(id: id, bucket: p.rawValue, label: "", durationMinutes: minutes,
        used: used, reset: minutes == 300 ? fiveReset : weeklyReset, scope: scope)
    }
    func snapshot(_ p: ProviderID, _ windows: [UsageWindow], observed: Date? = nil) -> UsageSnapshot {
      var result = UsageSnapshot(provider: p)
      result.apply(.success(.init(binding: "synthetic-review", windows: windows, date: observed ?? now)))
      return result
    }
    let bothCodex = try [window(.codex, "codex:primary", 300, 22),
      window(.codex, "codex:secondary", 10_080, 64),
      window(.codex, "spark:primary", 300, 14, .additional("Spark"))]
    let bothClaude = try [window(.claude, "five_hour", 300, 30),
      window(.claude, "seven_day", 10_080, 52),
      window(.claude, "seven_day_sonnet", 10_080, 18, .model("Sonnet"))]
    var stale = snapshot(.codex, bothCodex, observed: now.addingTimeInterval(-390))
    stale.apply(.fail(.timeout, binding: "synthetic-review"))
    var unavailable = UsageSnapshot(provider: .claude)
    unavailable.apply(.fail(.unavailable))
    var unsupported = UsageSnapshot(provider: .claude)
    unsupported.apply(.success(.init(binding: "synthetic-review", windows: [], date: now,
      availability: .unsupportedAllowance)))
    let cases: [(String, UsageSnapshot, UsageSnapshot)] = [
      ("both-window", snapshot(.codex, bothCodex), snapshot(.claude, bothClaude)),
      ("weekly-only", snapshot(.codex, try [window(.codex, "codex:primary", 10_080, 48)]),
        snapshot(.claude, try [window(.claude, "seven_day", 10_080, 64)])),
      ("five-hour-only", snapshot(.codex, try [window(.codex, "codex:primary", 300, 0)]),
        snapshot(.claude, try [window(.claude, "five_hour", 300, 100)])),
      ("stale-unavailable", stale, unavailable),
      ("missing-unsupported", snapshot(.codex, []), unsupported),
    ]
    for (name, codex, claude) in cases {
      model.usage = [.codex: codex, .claude: claude]
      try await capture(UsageView(model: model, assetBundle: app).background(Color(nsColor: .windowBackgroundColor)),
        name: name, size: CGSize(width: 800, height: 620),
        directory: directory)
      if name == "both-window" {
        try await capture(UsageView(model: model, assetBundle: app)
          .background(Color(nsColor: .windowBackgroundColor)).preferredColorScheme(.dark),
          name: name + "-dark", size: CGSize(width: 800, height: 620),
          directory: directory, appearance: .darkAqua)
      }
      let notch = NotchPresentation()
      notch.state.reconcile(activity: ActivitySnapshot(codex: SurfaceActivity(cli: true),
        claude: SurfaceActivity(cli: true)), enabled: true)
      notch.rows = [ProviderGlance(snapshot: codex), ProviderGlance(snapshot: claude)]
      try await capture(NotchView(model: notch, assetBundle: app).background(Color(nsColor: .windowBackgroundColor)),
        name: name + "-compact", size: CGSize(width: 300, height: 40), directory: directory)
      notch.state.hover(true)
      try await capture(NotchView(model: notch, assetBundle: app).background(Color(nsColor: .windowBackgroundColor)),
        name: name + "-hover", size: CGSize(width: 600, height: 430), directory: directory)
    }
    XCTAssertNotNil(defaults.object(forKey: Preferences.lightMigrationKey))
  }

  private func capture<V: View>(_ view: V, name: String, size: CGSize, directory: URL,
    appearance: NSAppearance.Name = .aqua) async throws {
    _ = NSApplication.shared
    let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.borderless],
      backing: .buffered, defer: false)
    window.isReleasedWhenClosed = false
    window.appearance = NSAppearance(named: appearance)
    let host = NSHostingView(rootView: view)
    window.contentView = host
    host.frame = CGRect(origin: .zero, size: size)
    defer { window.close() }
    host.layoutSubtreeIfNeeded()
    try await Task.sleep(for: .milliseconds(250))
    host.layoutSubtreeIfNeeded()
    let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
    host.cacheDisplay(in: host.bounds, to: bitmap)
    let data = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    XCTAssertGreaterThan(data.count, 1_000)
    try data.write(to: directory.appendingPathComponent(name + ".png"), options: .atomic)
  }
}

final class NotchGeometryTests: XCTestCase {
  private let left = MonitorDisplay(
    id: 1, frame: CGRect(x: -1600, y: 0, width: 1600, height: 1000),
    visible: CGRect(x: -1600, y: 30, width: 1600, height: 940))
  private let right = MonitorDisplay(
    id: 2, frame: CGRect(x: 100, y: -200, width: 1200, height: 800),
    visible: CGRect(x: 100, y: -170, width: 1200, height: 740))

  func testClickThresholdAndDragReturningToItsOrigin() {
    var gesture = MonitorDragGesture(origin: CGPoint(x: 200, y: 200))
    gesture.move(to: CGPoint(x: 202, y: 202))
    XCTAssertFalse(gesture.isDragging)
    gesture.move(to: CGPoint(x: 203, y: 204))
    XCTAssertTrue(gesture.isDragging)
    gesture.move(to: gesture.origin)
    XCTAssertTrue(gesture.isDragging, "A completed drag must never become an accidental click")
  }
  func testSavedCenterAndTopSurviveExpansionWithoutDrift() throws {
    let compact = CGRect(x: -1056, y: 596, width: 112, height: 34)
    let anchor = try XCTUnwrap(MonitorGeometry.anchor(frame: compact, on: left))
    let expanded = MonitorGeometry.frame(
      size: CGSize(width: 390, height: 174), anchor: anchor, visible: left.visible)
    XCTAssertEqual(expanded.midX, compact.midX, accuracy: 0.001)
    XCTAssertEqual(expanded.maxY, compact.maxY, accuracy: 0.001)
    let restored = MonitorGeometry.frame(size: compact.size, anchor: anchor, visible: left.visible)
    XCTAssertEqual(restored.minX, compact.minX, accuracy: 0.001)
    XCTAssertEqual(restored.minY, compact.minY, accuracy: 0.001)
  }
  func testExpansionAndOversizedFramesStayWithinUsableBounds() throws {
    let compact = CGRect(x: -1600, y: 30, width: 112, height: 34)
    let anchor = try XCTUnwrap(MonitorGeometry.anchor(frame: compact, on: left))
    XCTAssertTrue(left.visible.contains(MonitorGeometry.frame(
      size: CGSize(width: 390, height: 174), anchor: anchor, visible: left.visible)))
    XCTAssertEqual(MonitorGeometry.clamped(
      CGRect(x: -4000, y: -4000, width: 5000, height: 5000), to: left.visible), left.visible)
  }
  func testDraggingAcrossDisplaysRetainsGrabOffsetAndClampsEdges() throws {
    let start = CGRect(x: -1300, y: 600, width: 200, height: 34)
    let pressed = CGPoint(x: -1250, y: 610)
    let current = CGPoint(x: 600, y: 300)
    let destination = try XCTUnwrap(MonitorGeometry.display(at: current, among: [left, right]))
    XCTAssertEqual(destination.id, right.id)
    let moved = MonitorGeometry.draggedFrame(
      start: start, pressed: pressed, current: current, visible: destination.visible)
    XCTAssertEqual(moved.minX, 550)
    XCTAssertEqual(moved.minY, 290)
    let edge = MonitorGeometry.draggedFrame(
      start: start, pressed: pressed, current: CGPoint(x: 1290, y: 590), visible: right.visible)
    XCTAssertTrue(right.visible.contains(edge))
    XCTAssertEqual(edge.maxX, right.visible.maxX)
    XCTAssertEqual(edge.maxY, right.visible.maxY)
  }
  func testDisplayGapsChooseNearestScreenAndRemovalUsesMain() throws {
    XCTAssertEqual(MonitorGeometry.display(at: CGPoint(x: 80, y: 400), among: [left, right])?.id, 2)
    let anchor = try XCTUnwrap(MonitorAnchor(displayID: 2, centerX: 0.6, topY: 0.8))
    XCTAssertEqual(MonitorGeometry.display(for: anchor, among: [left], fallbackID: 1), left)
    let restored = MonitorGeometry.frame(
      size: CGSize(width: 390, height: 174), anchor: anchor, visible: left.visible)
    XCTAssertTrue(left.visible.contains(restored))
    XCTAssertNil(MonitorGeometry.display(for: anchor, among: [], fallbackID: 1))
  }
  func testNormalizedAnchorAdaptsToResolutionAndVisibleFrameChanges() throws {
    let anchor = try XCTUnwrap(MonitorAnchor(displayID: 2, centerX: 0.5, topY: 0.75))
    let first = MonitorGeometry.frame(
      size: CGSize(width: 200, height: 34), anchor: anchor, visible: right.visible)
    let resized = CGRect(x: 100, y: -170, width: 1600, height: 1000)
    let second = MonitorGeometry.frame(
      size: first.size, anchor: anchor, visible: resized)
    XCTAssertEqual(second.midX, resized.midX)
    XCTAssertEqual(second.maxY, resized.minY + resized.height * 0.75)
    XCTAssertNotEqual(second.origin, first.origin)
  }
  func testPersistenceValidatesSchemaCoordinatesAndDisplayID() throws {
    let name = "Llumi-notch-geometry-" + UUID().uuidString
    let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
    defer { defaults.removePersistentDomain(forName: name) }
    let anchor = try XCTUnwrap(MonitorAnchor(displayID: 2, centerX: 0.4, topY: 0.7))
    anchor.save(to: defaults)
    XCTAssertEqual(MonitorAnchor.load(from: defaults), anchor)
    for invalid in [
      #"{"schema":2,"displayID":2,"centerX":0.5,"topY":0.5}"#,
      #"{"schema":1,"displayID":0,"centerX":0.5,"topY":0.5}"#,
      #"{"schema":1,"displayID":2,"centerX":1.1,"topY":0.5}"#,
      #"{"schema":1,"displayID":2,"centerX":0.5,"topY":-0.1}"#,
      #"{"schema":1,"displayID":true,"centerX":0.5,"topY":0.5}"#,
      #"{"schema":1,"displayID":2,"centerX":"NaN","topY":0.5}"#,
    ] {
      defaults.set(Data(invalid.utf8), forKey: MonitorAnchor.defaultsKey)
      XCTAssertNil(MonitorAnchor.load(from: defaults))
    }
    for value in [Double.nan, .infinity, -.infinity] {
      XCTAssertNil(MonitorAnchor(displayID: 2, centerX: value, topY: 0.5))
      XCTAssertNil(MonitorAnchor(displayID: 2, centerX: 0.5, topY: value))
    }
    defaults.set("malformed", forKey: MonitorAnchor.defaultsKey)
    XCTAssertNil(MonitorAnchor.load(from: defaults))
    defaults.removeObject(forKey: MonitorAnchor.defaultsKey)
    XCTAssertNil(MonitorAnchor.load(from: defaults))
  }
}
