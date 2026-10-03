import Darwin
import Foundation
import XCTest

private func json(_ string: String) throws -> J { try J.parse(Data(string.utf8)) }
private func window(
  _ id: String = "seven_day", bucket: String = "claude", minutes: Int = 10080, used: Double = 25
) throws -> UsageWindow {
  try .init(
    id: id, bucket: bucket, label: id, durationMinutes: minutes, used: used,
    reset: Date(timeIntervalSince1970: 2_000_000_000))
}
private func reading(_ binding: String = "A") throws -> Reading {
  try .init(binding: binding, windows: [window()], date: Date())
}
@MainActor final class ParserTests: XCTestCase {
  func testClaudeAnalyticsDisabledTrueAccepted() throws {
    XCTAssertNoThrow(try Parsers.claudeAccount(json(Self.auth), status: 0))
  }
  func testClaudeAnalyticsEnabledRejected() throws {
    XCTAssertThrowsError(try Parsers.claudeAccount(
      json(Self.auth.replacingOccurrences(of: "\"analyticsDisabled\":true", with: "\"analyticsDisabled\":false")), status: 0))
  }
  func testClaudeAnalyticsAbsentOrRenamedRejected() throws {
    for value in [Self.auth.replacingOccurrences(of: "\"analyticsDisabled\":true,", with: ""),
      Self.auth.replacingOccurrences(of: "analyticsDisabled", with: "renamedField")] {
      XCTAssertThrowsError(try Parsers.claudeAccount(json(value), status: 0))
    }
  }
  func testClaudeAnalyticsWrongTypeRejected() throws {
    for value in ["null", "1", "\"true\"", "{}", "[]"] {
      XCTAssertThrowsError(try Parsers.claudeAccount(json(Self.auth.replacingOccurrences(
        of: "\"analyticsDisabled\":true", with: "\"analyticsDisabled\":" + value)), status: 0))
    }
  }
  func testClaudeAnalyticsDuplicateRejected() {
    XCTAssertThrowsError(try json(Self.auth.replacingOccurrences(
      of: "\"analyticsDisabled\":true", with: "\"analyticsDisabled\":true,\"analyticsDisabled\":false")))
  }
  func testCodexPreservesWeeklyMainAndSeparateSpark() throws {
    let r = try Parsers.codex(
      json(
        #"{"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":12,"windowDurationMins":10080,"resetsAt":2000000000}},"codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"Spark","primary":{"usedPercent":2,"windowDurationMins":300,"resetsAt":2000000000}}}}"#
      ))
    XCTAssertEqual(r.count, 2)
    XCTAssertEqual(r.first?.remaining, 88)
    XCTAssertEqual(r.first?.durationMinutes, 10080)
    XCTAssertEqual(r.first?.reset, Date(timeIntervalSince1970: 2_000_000_000))
  }
  func testSignedOutAndAPIKeyCodex() throws {
    XCTAssertThrowsError(try Parsers.codexAccount(json(#"{"account":null}"#))) {
      XCTAssertEqual($0 as? Failure, .signedOut)
    }
    XCTAssertThrowsError(try Parsers.codexAccount(json(#"{"account":{"type":"apiKey"}}"#)))
  }
  func testAccountBindingsChangeWithoutExposingIdentity() throws {
    let a = try Parsers.codexAccount(
      json(#"{"account":{"type":"chatgpt","email":"a@example.invalid","planType":"pro"}}"#))
    let b = try Parsers.codexAccount(
      json(#"{"account":{"type":"chatgpt","email":"b@example.invalid","planType":"pro"}}"#))
    XCTAssertNotEqual(a.binding, b.binding)
    XCTAssertFalse(a.binding.contains("example"))
  }
  func testInvalidPercentagesAndBooleanRejected() throws {
    for j in [J.number(-1), .number(101), .bool(true), .string("80")] {
      XCTAssertThrowsError(try Parsers.percent(j))
    }
    XCTAssertNil(try Parsers.percent(.null))
    XCTAssertEqual(try Parsers.percent(.number(0)), 0)
  }
  func testMalformedCodexFailsInsteadOfSparkSubstitution() throws {
    XCTAssertThrowsError(
      try Parsers.codex(
        json(#"{"rateLimitsByLimitId":[],"rateLimits":{"primary":{"usedPercent":1}}}"#)))
    var s = UsageSnapshot(provider: .codex)
    s.apply(
      .success(
        .init(
          binding: "A", windows: [try window("spark", bucket: "codex_bengalfox", minutes: 300)],
          date: Date())))
    XCTAssertNil(s.primary)
    XCTAssertTrue(s.compact.contains("—"))
  }
  func testClaudeSubscriptionValidation() throws {
    let a = try Parsers.claudeAccount(json(Self.auth), status: 0)
    XCTAssertEqual(a.plan, "max")
    XCTAssertThrowsError(
      try Parsers.claudeAccount(
        json(Self.auth.replacingOccurrences(of: "claude.ai", with: "api_key")), status: 0))
    XCTAssertThrowsError(try Parsers.claudeAccount(json(#"{"loggedIn":false}"#), status: 1)) {
      XCTAssertEqual($0 as? Failure, .signedOut)
    }
    XCTAssertThrowsError(try Parsers.claudeAccount(json(Self.auth), status: 1))
  }
  func testClaudeUsageAndSessionNoInference() throws {
    let windows = try Parsers.claude(json(Self.usage), plan: "max")
    XCTAssertEqual(windows.count, 2)
    XCTAssertEqual(windows.first?.remaining, 80)
    XCTAssertThrowsError(
      try Parsers.claude(
        json(
          Self.usage.replacingOccurrences(of: "\"total_cost_usd\":0", with: "\"total_cost_usd\":1")),
        plan: "max"))
  }
  func testClaudeCompatibilityAndIdentityMismatch() throws {
    XCTAssertThrowsError(try Parsers.claude(.object([:]), plan: "max"))
    XCTAssertThrowsError(try Parsers.claude(json(Self.usage), plan: "pro")) {
      XCTAssertEqual($0 as? Failure, .accountChanged)
    }
    let a = try Parsers.claudeAccount(json(Self.auth), status: 0)
    XCTAssertThrowsError(
      try Parsers.verifyClaudeSession(
        json(
          #"{"email":"different@example.invalid","apiProvider":"firstParty","organization":"Synthetic"}"#
        ), account: a))
  }
  func testISOTimezonesAndImpossibleDates() throws {
    XCTAssertEqual(
      try Parsers.iso(.string("2030-01-01T05:30:00+05:30")),
      try Parsers.iso(.string("2030-01-01T00:00:00Z")))
    for s in ["2030-02-30T00:00:00Z", "2030-01-01T00:00:00", "2030-01-01T00:00:00+15:00"] {
      XCTAssertThrowsError(try Parsers.iso(.string(s)))
    }
  }
  func testStrictJSONDuplicateDepthAndTruncation() throws {
    for s in [
      #"{"a":1,"a":2}"#, #"{"a":{"x":1,"x":2}}"#, "{", "[1,]",
      String(repeating: "[", count: 34) + "0" + String(repeating: "]", count: 34),
    ] { XCTAssertThrowsError(try json(s)) }
    XCTAssertEqual(try json(#"{"escaped":"a\"b"}"#)["escaped"].string, "a\"b")
  }
  func testResetExpiryDoesNotRefill() throws {
    let w = try window()
    XCTAssertEqual(w.resetText(at: Date(timeIntervalSince1970: 2_100_000_000)), "Resetting…")
    XCTAssertEqual(w.remaining, 75)
  }
  static let auth =
    #"{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"max","analyticsDisabled":true,"email":"synthetic@example.invalid","orgId":"00000000-0000-0000-0000-000000000001","orgName":"Synthetic"}"#
  static let usage =
    #"{"rate_limits_available":true,"subscription_type":"max","behaviors":null,"session":{"total_cost_usd":0,"total_api_duration_ms":0,"model_usage":{}},"rate_limits":{"five_hour":{"utilization":20,"resets_at":"2030-01-01T00:00:00Z"},"seven_day":{"utilization":30,"resets_at":"2030-01-07T00:00:00Z"}}}"#
}
@MainActor final class StateTests: XCTestCase {
  func testStaleOnlyForReverifiedSameAccount() throws {
    var s = UsageSnapshot(provider: .claude)
    s.apply(.success(try reading()))
    s.apply(.fail(.unavailable, binding: "A"))
    XCTAssertEqual(s.state, .stale)
    XCTAssertNotNil(s.reading)
    XCTAssertTrue(s.compact.contains("stale"))
    s.apply(.fail(.timeout))
    XCTAssertNil(s.reading)
    XCTAssertEqual(s.state, .unavailable)
  }
  func testAccountSwitchSignedOutAndMissingClearData() throws {
    for r in [
      QueryResult.fail(.accountChanged, binding: "A"), .fail(.unavailable, binding: "B"),
      .fail(.signedOut), .fail(.notInstalled),
    ] {
      var s = UsageSnapshot(provider: .codex)
      s.apply(.success(try reading()))
      s.apply(r)
      XCTAssertNil(s.reading)
    }
  }
  func testPrimarySelectionAndUnknowns() throws {
    var s = UsageSnapshot(provider: .claude)
    XCTAssertFalse(s.compact.contains("0%"))
    s.apply(
      .success(
        .init(
          binding: "A",
          windows: [try window("five_hour", minutes: 300, used: 1), try window(used: 40)],
          date: Date())))
    XCTAssertEqual(s.primary?.remaining, 99)
  }
  func testActivitySequencesBothOrdersAndDeduplication() {
    for first in [ProviderID.codex, .claude] {
      var a = ActivitySnapshot()
      XCTAssertEqual(a.name, "Hidden")
      if first == .codex { a.codex.cli = true } else { a.claude.cli = true }
      XCTAssertEqual(a.name, first.title)
      a.codex.cli = true
      a.claude.cli = true
      XCTAssertEqual(a.name, "Both")
      a.codex.desktop = true
      XCTAssertEqual(a.providers.count, 2)
      a.codex.desktop = false
      if first == .codex { a.codex.cli = false } else { a.claude.cli = false }
      XCTAssertEqual(a.name, first == .codex ? "Claude" : "Codex")
      a.codex.cli = false
      a.claude.cli = false
      XCTAssertEqual(a.name, "Hidden")
    }
  }
  func testDesktopFrontmostMinimizeRestoreAndCLIIndependence() {
    XCTAssertTrue(ActivitySnapshot.desktopActive(frontmost: true, hidden: false, normalWindows: 1))
    XCTAssertFalse(
      ActivitySnapshot.desktopActive(frontmost: false, hidden: false, normalWindows: 1))
    XCTAssertFalse(ActivitySnapshot.desktopActive(frontmost: true, hidden: false, normalWindows: 0))
    XCTAssertFalse(ActivitySnapshot.desktopActive(frontmost: true, hidden: true, normalWindows: 1))
    XCTAssertTrue(SurfaceActivity(cli: true, desktop: false).active)
  }
  func testActivityWithoutUsageDoesNotInventPercentage() {
    let s = UsageSnapshot(provider: .claude)
    let a = ActivitySnapshot(codex: .init(), claude: .init(cli: true))
    XCTAssertEqual(a.name, "Claude")
    XCTAssertFalse(s.compact.contains("%"))
  }
  func testPIDIdentityCannotConfuseReuse() {
    let a = ProcessIdentity(pid: 123, seconds: 1, microseconds: 1)
    let b = ProcessIdentity(pid: 123, seconds: 2, microseconds: 1)
    XCTAssertNotEqual(a, b)
    let registry = OwnedProcesses()
    registry.add(a)
    XCTAssertTrue(registry.contains(a))
    XCTAssertFalse(registry.contains(b))
  }
  func testCLIGrammarPositiveAndNegativeModes() {
    func classify(_ provider: Int32, _ options: [String]) -> Bool {
      let strings = (["provider"] + options).map { strdup($0) }
      defer { strings.forEach { free($0) } }
      let ptrs = strings.map { UnsafePointer($0) }
      return ptrs.withUnsafeBufferPointer {
        am_classify_options(provider, Int32(ptrs.count), $0.baseAddress) == 1
      }
    }
    for p: Int32 in [1, 2] {
      XCTAssertTrue(classify(p, []))
      for args in [
        ["--version"], ["--help"], ["help"], ["auth", "status"], ["--unknown"], ["ordinary prompt"],
      ] { XCTAssertFalse(classify(p, args)) }
    }
    XCTAssertTrue(classify(1, ["--no-alt-screen", "--sandbox", "read-only"]))
    XCTAssertTrue(classify(2, ["--safe-mode", "--effort", "low"]))
    for args in [
      ["app-server"], ["exec"], ["--sandbox"], ["--sandbox", "unknown"],
      ["--no-alt-screen", "--version"],
    ] { XCTAssertFalse(classify(1, args)) }
    for args in [["--print"], ["-p"], ["--safe-mode", "--print"], ["--no-session-persistence"]] {
      XCTAssertFalse(classify(2, args))
    }
  }
}
private actor FakeSource: UsageSource {
  var calls = 0
  var active = 0
  var maximum = 0
  let result: QueryResult
  let delay: Double
  init(_ result: QueryResult, delay: Double = 0.1) {
    self.result = result
    self.delay = delay
  }
  func query() async -> QueryResult {
    calls += 1
    active += 1
    maximum = max(maximum, active)
    defer { active -= 1 }
    do {
      try await Task.sleep(for: .seconds(delay))
      return result
    } catch { return .fail(.cancelled) }
  }
}
@MainActor final class RefreshTests: XCTestCase {
  func testCoalescingAndNoOverlap() async throws {
    let source = FakeSource(.success(try reading()))
    let store = UsageStore(sources: [.codex: source]) { _ in }
    for _ in 0..<20 { await store.refresh() }
    await store.waitForIdle()
    let calls = await source.calls
    let maximum = await source.maximum
    XCTAssertEqual(calls, 1)
    XCTAssertEqual(maximum, 1)
    await store.refresh(.codex, onlyIfOlderThan: 15)
    await store.waitForIdle()
    let unchanged = await source.calls
    XCTAssertEqual(unchanged, 1)
  }
  func testProviderFailuresIndependentBothDirectionsAndBothFail() async throws {
    for failing in [[ProviderID.codex], [.claude], [.codex, .claude]] {
      let sources = Dictionary(
        uniqueKeysWithValues: try ProviderID.allCases.map {
          (
            $0,
            FakeSource(failing.contains($0) ? .fail(.unavailable) : .success(try reading()))
              as any UsageSource
          )
        })
      let store = UsageStore(sources: sources) { _ in }
      await store.refresh()
      await store.waitForIdle()
      let states = await store.snapshot()
      for p in ProviderID.allCases {
        XCTAssertEqual(states[p]?.state, failing.contains(p) ? .unavailable : .live)
      }
    }
  }
  func testTimeoutCancellationAndStopDoNotPublishLateData() async throws {
    let source = FakeSource(.success(try reading()), delay: 10)
    let store = UsageStore(sources: [.codex: source], timeout: 0.1) { _ in }
    await store.refresh()
    await store.waitForIdle()
    let states = await store.snapshot()
    XCTAssertEqual(states[.codex]?.failure, .timeout)
    await store.refresh()
    await store.stop()
    let count = await source.calls
    await store.refresh()
    let after = await source.calls
    XCTAssertEqual(count, after)
    let active = await source.active
    XCTAssertEqual(active, 0)
  }
  func testMissingProviderWithIsolatedDiscovery() async {
    let discovery = ProviderDiscovery(searchDirectories: [])
    for p in ProviderID.allCases {
      do {
        _ = try await discovery.find(p)
        XCTFail("Should be missing")
      } catch { XCTAssertEqual(error as? Failure, .notInstalled) }
    }
  }
}
@MainActor final class ProcessTests: XCTestCase {
  func fixture(_ text: String) throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    let path = directory.appendingPathComponent("fixture.sh")
    try Data(("#!/bin/sh\n" + text).utf8).write(to: path)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
    return path
  }
  func testFiniteExitAndOwnedIdentity() async throws {
    // Keep the fixture alive until ownership is checked; instant exit races the assertion.
    let path = try fixture("read -r release\nprintf '{\"ok\":true}\\n'\n")
    defer { try? FileManager.default.removeItem(at: path.deletingLastPathComponent()) }
    let p = try Subprocess(
      executable: path, arguments: [], directory: path.deletingLastPathComponent())
    do {
      let id = try XCTUnwrap(ProcessIdentity.read(p.pid))
      XCTAssertTrue(OwnedProcesses.shared.contains(id))
      try await p.send(.null)
      let data = try await p.finite()
      let code = await p.close()
      XCTAssertEqual(code, 0)
      XCTAssertEqual(try J.parse(data)["ok"].bool, true)
      XCTAssertNil(ProcessIdentity.read(p.pid))
    } catch {
      await p.close()
      throw error
    }
  }
  func testTimeoutEvenWhenStdoutClosesAndCleanup() async throws {
    let path = try fixture("exec 1>&-\nexec /bin/sleep 60\n")
    defer { try? FileManager.default.removeItem(at: path.deletingLastPathComponent()) }
    let p = try Subprocess(
      executable: path, arguments: [], directory: path.deletingLastPathComponent(),
      limits: .init(seconds: 0.15))
    do {
      _ = try await p.finite()
      XCTFail("EOF must not mean process exit")
    } catch { XCTAssertEqual(error as? Failure, .timeout) }
    await p.close()
    XCTAssertNil(ProcessIdentity.read(p.pid))
  }
  func testOutputAndJSONLineBounds() async throws {
    for script in ["printf '%01000d' 0", "printf '%01000d' 0 >&2"] {
      let path = try fixture(script)
      defer { try? FileManager.default.removeItem(at: path.deletingLastPathComponent()) }
      var limits = ProcessLimits()
      limits.stdout = 50
      limits.stderr = 50
      do {
        _ = try await Subprocess.run(
          executable: path, arguments: [], environment: [],
          directory: path.deletingLastPathComponent(), limits: limits)
        XCTFail("Expected output bound")
      } catch { XCTAssertEqual(error as? Failure, .outputLimit) }
    }
  }
  func testCancellationAndProcessGroupCleanup() async throws {
    let path = try fixture(
      "trap '' TERM\n/bin/sleep 60 &\nprintf '{\"child\":%s}\\n' \"$!\"\nwait\n")
    defer { try? FileManager.default.removeItem(at: path.deletingLastPathComponent()) }
    let p = try Subprocess(
      executable: path, arguments: [], directory: path.deletingLastPathComponent())
    let response = try await p.next()
    let child = Int32(response["child"].number!)
    let task = Task { try await p.finite() }
    try await Task.sleep(for: .milliseconds(30))
    task.cancel()
    do {
      _ = try await task.value
      XCTFail("Expected cancellation")
    } catch { XCTAssertTrue(error is CancellationError) }
    await p.close()
    try await Task.sleep(for: .milliseconds(300))
    XCTAssertNil(ProcessIdentity.read(p.pid))
    XCTAssertNil(ProcessIdentity.read(child))
  }
}

@MainActor final class AdapterTests: XCTestCase {
  func syntheticCodex(switchAccount: Bool, malformed: Bool) async throws -> QueryResult {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let path = directory.appendingPathComponent("codex")
    let script = """
      #!/usr/bin/python3
      import sys,json
      if '--version' in sys.argv:
          print('codex-cli 9.9.9');sys.exit(0)
      for line in sys.stdin:
          q=json.loads(line);i=q.get('id');m=q.get('method')
          if i is None:continue
          r={}
          if m=='account/read':
              email='b@example.invalid' if i==4 and \(switchAccount ? "True":"False") else 'a@example.invalid'
              r={'account':{'type':'chatgpt','email':email,'planType':'pro'}}
          if m=='account/rateLimits/read':
              r={'rateLimitsByLimitId':{'codex':{'primary':{'usedPercent':\(malformed ? "101":"20"),'windowDurationMins':10080,'resetsAt':2000000000}}}}
          print(json.dumps({'id':i,'result':r}),flush=True)
      """
    try Data(script.utf8).write(to: path)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
    let discovery = ProviderDiscovery(directory: directory, searchDirectories: [directory.path])
    return await ProviderAdapter(provider: .codex, discovery: discovery).query()
  }
  func testActualAdapterDiscardsAccountSwitchDuringQuery() async throws {
    let result = try await syntheticCodex(switchAccount: true, malformed: false)
    XCTAssertEqual(result.failure, .accountChanged)
    XCTAssertNil(result.reading)
    XCTAssertNil(result.verifiedBinding)
  }
  func testActualAdapterRevalidatesAccountAfterMalformedUsage() async throws {
    let result = try await syntheticCodex(switchAccount: false, malformed: true)
    XCTAssertEqual(result.failure, .malformed)
    XCTAssertNotNil(result.verifiedBinding)
    XCTAssertNil(result.reading)
  }
  func testActualAdapterPublishesOnlyVerifiedReading() async throws {
    let result = try await syntheticCodex(switchAccount: false, malformed: false)
    XCTAssertNil(result.failure)
    XCTAssertEqual(result.reading?.windows.first?.remaining, 80)
  }
}

@MainActor final class GeometryTests: XCTestCase {
  func testNotchAndNoNotchFramesStayInsideVisibleDisplay() {
    for inset: CGFloat in [0, 32] {
      let screen = CGRect(x: 2000, y: 0, width: 1600, height: 1000)
      let visible = CGRect(x: 2040, y: 30, width: 1560, height: 930)
      let frame = MonitorGeometry.frame(
        screen: screen, visible: visible, safeTop: inset, contentWidth: 250)
      XCTAssertTrue(visible.contains(frame))
      XCTAssertLessThanOrEqual(frame.maxY, screen.maxY - inset)
    }
  }
  func testScalingRecomputesWithoutMachineDimensions() {
    let a = MonitorGeometry.frame(
      screen: CGRect(x: 0, y: 0, width: 1600, height: 1000),
      visible: CGRect(x: 0, y: 0, width: 1600, height: 970), safeTop: 30, contentWidth: 200)
    let b = MonitorGeometry.frame(
      screen: CGRect(x: 0, y: 0, width: 1200, height: 800),
      visible: CGRect(x: 0, y: 0, width: 1200, height: 770), safeTop: 30, contentWidth: 200)
    XCTAssertNotEqual(a.origin, b.origin)
    XCTAssertEqual(a.width, b.width)
  }
}

@MainActor final class DescriptorTests: XCTestCase {
  func testChildCannotInheritUnrelatedApplicationDescriptors() async throws {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let descriptor = Darwin.open(url.path, O_CREAT | O_RDWR, 0o600)
    XCTAssertGreaterThanOrEqual(descriptor, 0)
    let high = fcntl(descriptor, F_DUPFD, 200)
    defer {
      Darwin.close(high)
      Darwin.close(descriptor)
      try? FileManager.default.removeItem(at: url)
    }
    XCTAssertGreaterThanOrEqual(high, 200)
    let result = try await Subprocess.run(
      executable: URL(fileURLWithPath: "/bin/sh"),
      arguments: ["-c", "if [ -e /dev/fd/\(high) ]; then printf leaked; else printf closed; fi"],
      environment: [], directory: url.deletingLastPathComponent())
    XCTAssertEqual(String(decoding: result.0, as: UTF8.self), "closed")
  }
}

@MainActor final class WakeTests: XCTestCase {
  func testSuspendBlocksRefreshAndResumeCoalesces() async throws {
    let source = FakeSource(.success(try reading()), delay: 0.15)
    let store = UsageStore(sources: [.claude: source]) { _ in }
    await store.suspend()
    await store.refresh()
    let asleep = await source.calls
    XCTAssertEqual(asleep, 0)
    await store.resume()
    await store.refresh()
    await store.waitForIdle()
    let awake = await source.calls
    XCTAssertEqual(awake, 1)
    await store.stop()
    await store.resume()
    let stopped = await source.calls
    XCTAssertEqual(stopped, 1)
  }
}

@MainActor final class PrivacyTests: XCTestCase {
  func testProtectedDiscoveryPathsRejected() {
    for folder in ["Documents", "Desktop", "Downloads", "Music", "Pictures", "Movies"] {
      XCTAssertFalse(
        RuntimePaths.permitsDiscovery("/Users/test/" + folder + "/bin", home: "/Users/test"))
      XCTAssertFalse(
        RuntimePaths.permitsDiscovery("/Users/test/.local/../" + folder, home: "/Users/test"))
    }
    XCTAssertTrue(RuntimePaths.permitsDiscovery("/opt/homebrew/bin", home: "/Users/test"))
    XCTAssertFalse(
      RuntimePaths.providerWork.path.hasPrefix(
        FileManager.default.homeDirectoryForCurrentUser.path + "/"))
  }
  func testProtectedSymlinkTargetRejectedBeforeAccess() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let alias = root.appendingPathComponent("claude")
    try FileManager.default.createSymbolicLink(
      atPath: alias.path, withDestinationPath: "/Users/test/Documents/provider")
    XCTAssertNil(RuntimePaths.discoveryExecutable(alias, home: "/Users/test"))
    XCTAssertNotNil(RuntimePaths.discoveryExecutable(URL(fileURLWithPath: "/bin/sh")))
  }
  func testChildWorkingDirectoryAndGitCeiling() async throws {
    let parent = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    let work = parent.appendingPathComponent("cache/ProviderWork")
    try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: parent) }
    let limits = ProcessLimits(seconds: 5)
    _ = try await Subprocess.run(
      executable: URL(fileURLWithPath: "/usr/bin/git"), arguments: ["init", "--quiet"],
      environment: ["GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null"], directory: parent,
      limits: limits)
    let discovery = ProviderDiscovery(directory: work)
    let env = discovery.childEnvironment
    let cwd = try await Subprocess.run(
      executable: URL(fileURLWithPath: "/bin/pwd"), arguments: ["-P"], environment: env,
      directory: discovery.directory, limits: limits)
    XCTAssertEqual(
      String(decoding: cwd.0, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines),
      discovery.directory.path)
    let result = try await Subprocess.run(
      executable: URL(fileURLWithPath: "/usr/bin/git"),
      arguments: ["rev-parse", "--show-toplevel"], environment: env, directory: discovery.directory,
      limits: limits)
    XCTAssertEqual(result.1, 128)
    XCTAssertTrue(result.0.isEmpty)

  }
  func testBuiltAppHasNoPrivacyUsageDescriptions() throws {
    let app = Bundle(for: PrivacyTests.self).bundleURL.deletingLastPathComponent()
      .appendingPathComponent("Llumi.app/Contents/Info.plist")
    let data = try Data(contentsOf: app)
    let plist = try XCTUnwrap(
      try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
    XCTAssertFalse(plist.keys.contains { $0.hasSuffix("UsageDescription") })
  }
}

@MainActor final class ClaudeConsumerWindowTests: XCTestCase {
  private let five = #""five_hour":{"utilization":20,"resets_at":null}"#
  private let week = #""seven_day":{"utilization":30,"resets_at":null}"#
  private let internalBucket = #""iguana_necktie":{"utilization":1,"resets_at":null}"#

  private func snapshot(_ limits: String) throws -> UsageSnapshot {
    var root = try XCTUnwrap(json(ParserTests.usage).object)
    root["rate_limits"] = try json("{" + limits + "}")
    let windows = try Parsers.claude(.object(root), plan: "max")
    var result = UsageSnapshot(provider: .claude)
    result.apply(.success(.init(binding: "fixture", windows: windows, date: Date())))
    return result
  }

  func testStandardWindowsDisplayFiveHourThenWeekly() throws {
    let s = try snapshot(week + "," + five)
    XCTAssertEqual(s.consumerWindows.map(\.id), ["five_hour", "seven_day"])
    XCTAssertEqual(s.consumerWindows.compactMap(\.claudeDisplayLabel), ["5 hours", "7 days"])
    XCTAssertEqual(s.primary?.id, "five_hour")
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "80%")
    XCTAssertEqual(s.detailWindows, s.consumerWindows)
  }

  func testObservedIdentifierIsAnUnknownTopLevelBucketRetainedOnlyInternally() throws {
    let s = try snapshot(five + "," + internalBucket + "," + week)
    let parsed = try XCTUnwrap(s.reading?.windows.first { $0.id == "iguana_necktie" })
    XCTAssertEqual(parsed.bucket, "claude")
    XCTAssertNil(parsed.durationMinutes)
    XCTAssertEqual(parsed.label, "iguana necktie")
    XCTAssertEqual(s.reading?.windows.count, 3)
    XCTAssertEqual(s.consumerWindows.map(\.id), ["five_hour", "seven_day"])
    XCTAssertNil(parsed.claudeDisplayLabel)
    XCTAssertEqual(s.primary?.remaining, 80)
  }

  func testUnknownBucketOnlyNeverFabricatesAnAllowance() throws {
    let s = try snapshot(internalBucket)
    XCTAssertEqual(s.reading?.windows.count, 1)
    XCTAssertNil(s.primary)
    XCTAssertTrue(s.consumerWindows.isEmpty)
    XCTAssertTrue(s.detailWindows.isEmpty)
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "--")
    XCTAssertFalse(s.compact.contains("%"))
  }

  func testWeeklyWithoutFiveHourRemainsDetailAndNotCompactPrimary() throws {
    let s = try snapshot(week + "," + internalBucket)
    XCTAssertNil(s.primary)
    XCTAssertEqual(s.consumerWindows.map(\.id), ["seven_day"])
    XCTAssertEqual(s.detailWindows.map(\.id), ["seven_day"])
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "--")
    XCTAssertFalse(s.compact.contains("%"))
  }

  func testMultipleUnknownAndModelScopedWindowsNeverLeakIntoConsumerSurfaces() throws {
    let extra = #""future_internal":{"utilization":2},"seven_day_sonnet":{"utilization":3},"model_scoped":[{"display_name":"synthetic_model","utilization":4},{"display_name":"five_hour","utilization":5}]"#
    let s = try snapshot(five + "," + week + "," + internalBucket + "," + extra)
    XCTAssertEqual(s.reading?.windows.count, 7)
    XCTAssertTrue(s.reading?.windows.contains { $0.id == "model:synthetic_model" } == true)
    XCTAssertTrue(s.reading?.windows.contains { $0.id == "model:five_hour" } == true)
    XCTAssertEqual(s.consumerWindows.map(\.id), ["five_hour", "seven_day"])
    XCTAssertEqual(s.detailWindows.map(\.id), ["five_hour", "seven_day"])
    let consumerText = s.consumerWindows.compactMap(\.claudeDisplayLabel).joined()
      + s.compact + ProviderGlance(snapshot: s).accessibility
    let diagnostics = SetupDiagnostics.report([.claude: s], version: "1.1.2", build: "2")
    for name in ["iguana_necktie", "future_internal", "seven_day_sonnet", "synthetic_model", "model:"] {
      XCTAssertFalse(consumerText.contains(name))
      XCTAssertFalse(diagnostics.contains(name))
    }
  }

  func testOnlyUnknownAndModelScopedWindowsStayUnknown() throws {
    let s = try snapshot(internalBucket + #", "seven_day_sonnet":{"utilization":3},"model_scoped":[{"display_name":"five_hour","utilization":4}]"#)
    XCTAssertNil(s.primary)
    XCTAssertTrue(s.consumerWindows.isEmpty)
    XCTAssertTrue(s.detailWindows.isEmpty)
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "--")
  }

  func testCodexMainAndAdditionalWindowsRemainUnchanged() throws {
    let windows = try Parsers.codex(json(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":30,"windowDurationMins":10080}},"codex_bengalfox":{"limitName":"Spark","primary":{"usedPercent":1,"windowDurationMins":300}}}}"#))
    var s = UsageSnapshot(provider: .codex)
    s.apply(.success(.init(binding: "fixture", windows: windows, date: Date())))
    XCTAssertEqual(s.consumerWindows, windows)
    XCTAssertEqual(s.primary?.id, "codex:secondary")
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "70%")
    XCTAssertEqual(s.detailWindows.map(\.id), ["codex:secondary"])
    XCTAssertTrue(s.consumerWindows.contains { $0.label == "Spark" })
  }
}

final class ProductPassTests: XCTestCase {
  func testClaudeCompactAlwaysFiveHourAndDetailsOrdered() throws {
    for (short, long) in [(10.0, 80.0), (80.0, 10.0), (37.0, 19.0)] {
      var s = UsageSnapshot(provider: .claude)
      let five = try window("five_hour", minutes: 300, used: short)
      let week = try window("seven_day", used: long)
      s.apply(.success(.init(binding: "fixture", windows: [week, five], date: Date())))
      XCTAssertEqual(s.primary?.remaining, 100 - short)
      XCTAssertEqual(s.detailWindows.map(\.id), ["five_hour", "seven_day"])
      XCTAssertEqual(ProviderGlance(snapshot: s).percentage, UsageSnapshot.percent(100 - short))
    }
  }
  func testClaudeMissingUnknownDuplicateAndInvalidFiveHourNeverUseWeekly() throws {
    var s = UsageSnapshot(provider: .claude)
    let week = try window()
    s.apply(.success(.init(binding: "fixture", windows: [week], date: Date())))
    XCTAssertNil(s.primary)
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "--")
    XCTAssertEqual(s.detailWindows.map(\.id), ["seven_day"])
    let five = try UsageWindow(id: "five_hour", bucket: "claude", label: "", durationMinutes: 300, used: nil, reset: nil)
    s.apply(.success(.init(binding: "fixture", windows: [five, week], date: Date())))
    XCTAssertNil(s.primary?.remaining)
    s.apply(.success(.init(binding: "fixture", windows: [five, five, week], date: Date())))
    XCTAssertNil(s.primary)
    XCTAssertThrowsError(try window("five_hour", minutes: 300, used: .nan))
    XCTAssertThrowsError(try window("five_hour", minutes: 300, used: 101))
  }
  func testExpiredFiveHourDoesNotRefill() throws {
    let five = try UsageWindow(id: "five_hour", bucket: "claude", label: "", durationMinutes: 300,
      used: 80, reset: Date(timeIntervalSince1970: 1))
    XCTAssertEqual(five.remaining, 20)
    XCTAssertEqual(five.resetText(at: Date()), "Resetting…")
  }
  func testDiagnosticsNeverExportPayloadIdentityOrArbitraryMetadata() throws {
    var s = UsageSnapshot(provider: .claude)
    let secret = "private@example.test /Users/example/project secret-token"
    s.apply(.success(.init(binding: secret, windows: [try UsageWindow(id: secret, bucket: secret,
      label: secret, durationMinutes: 300, used: 20, reset: nil)], date: Date())))
    let text = SetupDiagnostics.report([.claude: s], version: secret, build: "1.2\nsecret-token")
    for forbidden in ["private", "secret-token", "@", "/Users", "20%"] { XCTAssertFalse(text.contains(forbidden)) }
    XCTAssertTrue(text.contains("App version: unknown"))
    XCTAssertTrue(text.contains("Authentication: verified"))
    XCTAssertTrue(text.contains("[codex]\nDetected: unknown"))
  }
  func testReadinessDoesNotInferAuthenticationFromFailureOrStale() throws {
    for state in [ProviderState.loading, .unavailable, .stale] {
      let diagnostic = SetupDiagnostic(UsageSnapshot(provider: .codex, state: state))
      XCTAssertEqual(diagnostic.authentication, "unknown")
    }
    XCTAssertEqual(SetupDiagnostic(UsageSnapshot(provider: .codex, state: .signedOut)).authentication, "signed-out")
    XCTAssertEqual(SetupDiagnostic(UsageSnapshot(provider: .codex, state: .notInstalled)).detected, "no")
    XCTAssertEqual(SetupDiagnostic(UsageSnapshot(provider: .codex, state: .live)).usage, "unavailable")
  }
}
