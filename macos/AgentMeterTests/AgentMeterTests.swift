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
final class ProviderClientMetadataTests: XCTestCase {
  func testCodexClientUsesRuntimeMarketingVersion() {
    let info = ProviderAdapter.codexClientInfo(info: [
      "LlumiReleaseVersion": "1.1.3", "CFBundleShortVersionString": "1.1.2",
      "CFBundleVersion": "3",
    ])
    XCTAssertEqual(info["name"].string, "llumi")
    XCTAssertEqual(info["version"].string, "1.1.3")
    XCTAssertEqual(ProviderAdapter.codexClientInfo(info: [
      "CFBundleShortVersionString": "2.0.1", "CFBundleVersion": "4",
    ])["version"].string, "2.0.1")
  }
  func testCodexClientRejectsUnusableMetadataAndUsesCandidateFallback() {
    let values: [Any] = ["", "1.1", "1.1.3\n", "01.1.3", "１.１.３", "1.1.3-private",
      String(repeating: "1", count: 33) + ".1.3", 113, true]
    for value in values {
      XCTAssertEqual(ProviderAdapter.codexClientInfo(info: [
        "LlumiReleaseVersion": value, "CFBundleShortVersionString": "2.0.1",
      ])["version"].string, "2.0.1")
      XCTAssertEqual(ProviderAdapter.codexClientInfo(info: [
        "LlumiReleaseVersion": value,
      ])["version"].string, "1.1.3")
    }
    XCTAssertEqual(ProviderAdapter.codexClientInfo(info: nil)["version"].string, "1.1.3")
  }
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
    XCTAssertEqual(w.resetText(at: Date(timeIntervalSince1970: 2_100_000_000)), "Reported reset time has passed")
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
  func syntheticCodex(switchAccount: Bool, malformed: Bool, response: String? = nil,
    rateLimitError: Bool = false, failRevalidation: Bool = false) async throws -> QueryResult {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let path = directory.appendingPathComponent("codex")
    let responseLiteral = String(data: try J.string(response ?? "").encoded(), encoding: .utf8)!
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
              assert q['params']=={'refreshToken':False}
              if i==4 and \(failRevalidation ? "True":"False"):
                  print(json.dumps({'id':i,'error':{'code':-32603,'message':'synthetic private detail'}}),flush=True);continue
              email='b@example.invalid' if i==4 and \(switchAccount ? "True":"False") else 'a@example.invalid'
              r={'account':{'type':'chatgpt','email':email,'planType':'pro'}}
          if m=='account/rateLimits/read':
              assert q['params']=={'excludeResetCreditDetails':True}
              if \(rateLimitError ? "True":"False"):
                  print(json.dumps({'id':i,'error':{'code':429,'message':'synthetic private detail'}}),flush=True);continue
              r={'rateLimitsByLimitId':{'codex':{'primary':{'usedPercent':\(malformed ? "101":"20"),'windowDurationMins':10080,'resetsAt':2000000000}}}}
              if \(response != nil ? "True":"False"):r=json.loads(\(responseLiteral))
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
  func testActualAdapterRevalidatesAccountAfterMalformedPercentageWithoutAnotherValidPercentage() async throws {
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
  func testActualAdapterEmptySuccessInvalidatesPreviousAllowance() async throws {
    let result = try await syntheticCodex(switchAccount: false, malformed: false,
      response: #"{"rateLimitsByLimitId":{},"rateLimits":null}"#)
    XCTAssertEqual(result.reading?.availability, .notReported)
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.apply(.success(try .init(binding: result.verifiedBinding!, windows: [window("codex:primary", bucket: "codex")], date: Date())))
    snapshot.apply(result)
    XCTAssertEqual(snapshot.state, .notReported)
    XCTAssertTrue(snapshot.reading?.windows.isEmpty == true)
  }
  func testActualAdapterRateLimitIsFixedCategoryAndNeedsAccountReverification() async throws {
    let limited = try await syntheticCodex(switchAccount: false, malformed: false, rateLimitError: true)
    XCTAssertEqual(limited.failure, .rateLimited)
    XCTAssertNotNil(limited.verifiedBinding)
    let unverified = try await syntheticCodex(switchAccount: false, malformed: false,
      rateLimitError: true, failRevalidation: true)
    XCTAssertNil(unverified.verifiedBinding)
    XCTAssertNil(unverified.reading)
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.apply(.success(try .init(binding: "fixture", windows: [window("codex:primary", bucket: "codex")], date: Date())))
    snapshot.apply(unverified)
    XCTAssertNil(snapshot.reading)
    XCTAssertEqual(ProviderAdapter.rpcFailure(try json(#"{"code":-32001,"message":"private"}"#)), .rateLimited)
    XCTAssertEqual(ProviderAdapter.rpcFailure(try json(#"{"code":-32603,"message":"429 rate limit private"}"#)), .unavailable)
    for malformed in [#"{"code":429}"#, #"{"code":429,"message":true}"#, #"{"code":"429","message":"private"}"#] {
      XCTAssertEqual(ProviderAdapter.rpcFailure(try json(malformed)), .incompatible)
    }
  }
  func testActualAdapterMalformedEnvelopeClearsEvenReverifiedOldAccount() async throws {
    let result = try await syntheticCodex(switchAccount: false, malformed: false,
      response: #"{"rateLimitsByLimitId":[],"rateLimits":{"primary":{"usedPercent":20}}}"#)
    XCTAssertEqual(result.failure, .incompatible)
    var snapshot = UsageSnapshot(provider: .codex)
    snapshot.apply(.success(try .init(binding: result.verifiedBinding!, windows: [window("codex:primary", bucket: "codex")], date: Date())))
    snapshot.apply(result)
    XCTAssertNil(snapshot.reading)
  }
  func syntheticClaude(accountMismatch: Bool = false, nonzeroSession: Bool = false) async throws -> QueryResult {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let path = directory.appendingPathComponent("claude")
    let authLiteral = String(data: try J.string(ParserTests.auth).encoded(), encoding: .utf8)!
    let usage = nonzeroSession ? ParserTests.usage.replacingOccurrences(of: "\"total_cost_usd\":0", with: "\"total_cost_usd\":1") : ParserTests.usage
    let usageLiteral = String(data: try J.string(usage).encoded(), encoding: .utf8)!
    let script = """
      #!/usr/bin/python3
      import sys,json,os
      if '--version' in sys.argv:print('9.9.9 (Claude Code)');sys.exit(0)
      assert os.environ.get('DISABLE_TELEMETRY')=='1'
      if sys.argv[1:]==['auth','status']:print(\(authLiteral));sys.exit(0)
      assert '--no-session-persistence' in sys.argv and '--safe-mode' in sys.argv
      assert '--setting-sources=' in sys.argv and '--strict-mcp-config' in sys.argv
      assert sys.argv[sys.argv.index('--mcp-config')+1]=='{"mcpServers":{}}'
      for line in sys.stdin:
          q=json.loads(line);assert q['type']=='control_request'
          request=q['request'];subtype=request['subtype']
          if subtype=='initialize':
              assert request['hooks']=={}
              email='different@example.invalid' if \(accountMismatch ? "True":"False") else 'synthetic@example.invalid'
              r={'account':{'email':email,'apiProvider':'firstParty','organization':'Synthetic','apiKeySource':'none','tokenSource':'oauth'}}
          elif subtype=='get_usage':
              assert request['skip_behaviors']==True;r=json.loads(\(usageLiteral))
          else:raise Exception('Unexpected control request')
          print(json.dumps({'type':'control_response','response':{'request_id':q['request_id'],'subtype':'success','response':r}}),flush=True)
      """
    try Data(script.utf8).write(to: path)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
    let discovery = ProviderDiscovery(directory: directory, searchDirectories: [directory.path])
    return await ProviderAdapter(provider: .claude, discovery: discovery).query()
  }
  func testClaudeActualControlSessionUsesNoInferenceContractAndVerifiedAllowance() async throws {
    let result = try await syntheticClaude()
    XCTAssertNil(result.failure)
    XCTAssertNotNil(result.verifiedBinding)
    XCTAssertEqual(result.reading?.availability, .reported)
    XCTAssertEqual(result.reading?.windows.map(\.used), [20, 30])
  }
  func testClaudeActualControlSessionRejectsAccountMismatchAndInference() async throws {
    let mismatch = try await syntheticClaude(accountMismatch: true)
    XCTAssertEqual(mismatch.failure, .accountChanged)
    XCTAssertNil(mismatch.reading)
    XCTAssertNil(mismatch.verifiedBinding)
    let inference = try await syntheticClaude(nonzeroSession: true)
    XCTAssertEqual(inference.failure, .incompatible)
    XCTAssertNil(inference.reading)
    XCTAssertNotNil(inference.verifiedBinding)
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
    XCTAssertEqual(parsed.label, "")
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

  func testWeeklyWithoutFiveHourIsGeneralCompactFallback() throws {
    let s = try snapshot(week + "," + internalBucket)
    XCTAssertEqual(s.primary?.id, "seven_day")
    XCTAssertEqual(s.consumerWindows.map(\.id), ["seven_day"])
    XCTAssertEqual(s.detailWindows.map(\.id), ["seven_day"])
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "70%")
    XCTAssertTrue(s.compact.contains("70%"))
  }

  func testUnknownsStayInternalAndVerifiedWeeklyModelWindowsAppearInDetails() throws {
    let extra = #""future_internal":{"utilization":2},"seven_day_sonnet":{"utilization":3},"model_scoped":[{"display_name":"synthetic_model","utilization":4},{"display_name":"five_hour","utilization":5}]"#
    let s = try snapshot(five + "," + week + "," + internalBucket + "," + extra)
    XCTAssertEqual(s.reading?.windows.count, 7)
    XCTAssertTrue(s.reading?.windows.contains { $0.id == "model:synthetic_model" } == true)
    XCTAssertTrue(s.reading?.windows.contains { $0.id == "model:five_hour" } == true)
    XCTAssertEqual(s.consumerWindows.map(\.id), ["five_hour", "seven_day", "model:five_hour", "model:synthetic_model", "seven_day_sonnet"])
    XCTAssertEqual(s.detailWindows, s.consumerWindows)
    XCTAssertEqual(s.reading?.windows.first { $0.id == "model:five_hour" }?.scope, .model("five_hour"))
    XCTAssertEqual(s.reading?.windows.first { $0.id == "model:five_hour" }?.durationMinutes, 10080)
    let consumerText = s.consumerWindows.map(\.scopeLabel).joined()
      + s.compact + ProviderGlance(snapshot: s).accessibility
    let diagnostics = SetupDiagnostics.report([.claude: s], version: "1.1.2", build: "2")
    for name in ["iguana_necktie", "future_internal"] {
      XCTAssertFalse(consumerText.contains(name))
    }
    for name in ["iguana_necktie", "future_internal", "seven_day_sonnet", "synthetic_model", "model:"] {
      XCTAssertFalse(diagnostics.contains(name))
    }
  }

  func testOnlyModelWindowsAreReportedWithoutGeneralCompactSubstitution() throws {
    let s = try snapshot(internalBucket + #", "seven_day_sonnet":{"utilization":3},"model_scoped":[{"display_name":"five_hour","utilization":4}]"#)
    XCTAssertNil(s.primary)
    XCTAssertEqual(s.consumerWindows.map(\.id), ["model:five_hour", "seven_day_sonnet"])
    XCTAssertEqual(s.detailWindows, s.consumerWindows)
    XCTAssertEqual(s.state, .live)
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "--")
  }

  func testCodexAllWindowsAppearAndFiveHourGeneralIsPrimary() throws {
    let windows = try Parsers.codex(json(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300},"secondary":{"usedPercent":30,"windowDurationMins":10080}},"codex_bengalfox":{"limitName":"Spark","primary":{"usedPercent":1,"windowDurationMins":300}}}}"#))
    var s = UsageSnapshot(provider: .codex)
    s.apply(.success(.init(binding: "fixture", windows: windows, date: Date())))
    XCTAssertEqual(s.consumerWindows, windows)
    XCTAssertEqual(s.primary?.id, "codex:primary")
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "80%")
    XCTAssertEqual(s.detailWindows.map(\.id), ["codex:primary", "codex:secondary", "codex_bengalfox:primary"])
    XCTAssertTrue(s.consumerWindows.contains { $0.label == "Spark" })
  }
}

@MainActor final class AllowanceNormalizationTests: XCTestCase {
  private func claude(_ limits: String, available: Bool = true) throws -> NormalizedUsage {
    var root = try XCTUnwrap(json(ParserTests.usage).object)
    root["rate_limits"] = try json(limits)
    root["rate_limits_available"] = .bool(available)
    return try Parsers.claudeAllowance(.object(root), plan: "max")
  }
  private func snapshot(_ provider: ProviderID, _ data: NormalizedUsage) -> UsageSnapshot {
    var value = UsageSnapshot(provider: provider)
    value.apply(.success(.init(binding: "fixture", windows: data.windows, date: Date(), availability: data.availability)))
    return value
  }
  func testCodexEmptyMapFallsBackAndMirroredSwappedSlotsDeduplicateByDuration() throws {
    let fallback = try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300}}}"#))
    XCTAssertEqual(fallback.availability, .reported)
    XCTAssertEqual(fallback.windows.first?.remaining, 100)
    let mirrored = try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":10080},"secondary":{"usedPercent":10,"windowDurationMins":300}}},"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":10080}}}"#))
    XCTAssertEqual(mirrored.windows.count, 2)
    let value = snapshot(.codex, mirrored)
    XCTAssertEqual(value.primary?.durationMinutes, 300)
    XCTAssertEqual(value.primary?.remaining, 90)
    XCTAssertEqual(value.detailWindows.count, 2)
  }
  func testCodexContradictoryRepresentationsAndWrongBucketIdentityFailClosed() throws {
    for input in [
      #"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":10,"windowDurationMins":300}}},"rateLimits":{"primary":{"usedPercent":11,"windowDurationMins":300}}}"#,
      #"{"rateLimitsByLimitId":{"codex":{"limitId":"other","primary":{"usedPercent":10}}}}"#,
    ] {
      XCTAssertThrowsError(try Parsers.codexAllowance(json(input))) { XCTAssertEqual($0 as? Failure, .incompatible) }
    }
  }
  func testDistinctSameDurationTransportWindowsArePreservedAndMirrorsDeduplicate() throws {
    let raw = #"{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":11,"windowDurationMins":300}}"#
    let data = try Parsers.codexAllowance(json("{\"rateLimitsByLimitId\":{\"codex\":" + raw + "},\"rateLimits\":" + raw + "}"))
    XCTAssertEqual(data.windows.count, 2)
    XCTAssertEqual(data.windows.map(\.used), [10, 11])
    XCTAssertEqual(snapshot(.codex, data).primary?.id, "codex:primary")
  }
  func testMirroredPartialSlotsDeduplicateWithoutInventingDuration() throws {
    let data = try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"resetsAt":2000000000}}},"rateLimits":{"primary":{"usedPercent":20,"resetsAt":2000000000}}}"#))
    XCTAssertEqual(data.windows.count, 1)
    XCTAssertNil(data.windows.first?.durationMinutes)
    XCTAssertEqual(data.windows.first?.remaining, 80)
    XCTAssertNil(snapshot(.codex, data).primary)
    XCTAssertThrowsError(try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20}}},"rateLimits":{"primary":{"usedPercent":21}}}"#))) {
      XCTAssertEqual($0 as? Failure, .incompatible)
    }
  }
  func testCodexBucketSeparatorsAndEscapeSequencesCannotCollideWithLegacyIdentity() throws {
    let data = try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{"foo:legacy":{"primary":{"usedPercent":10,"windowDurationMins":300}},"foo%3Alegacy":{"primary":{"usedPercent":20,"windowDurationMins":300}}},"rateLimits":{"limitId":"foo","primary":{"usedPercent":30,"windowDurationMins":300}}}"#))
    XCTAssertEqual(Set(data.windows.map(\.id)).count, 3)
    XCTAssertEqual(Set(data.windows.map(\.bucket)), Set(["foo", "foo:legacy", "foo%3Alegacy"]))
    XCTAssertEqual(data.windows.first { $0.bucket == "foo:legacy" }?.id, "foo%3Alegacy:primary")
    XCTAssertEqual(data.windows.first { $0.bucket == "foo%3Alegacy" }?.id, "foo%253Alegacy:primary")
    XCTAssertEqual(snapshot(.codex, data).consumerWindows.count, 3)
    XCTAssertTrue(snapshot(.codex, data).consumerWindows.allSatisfy { $0.scope == .additional("Additional") })
    let mirrored = try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{"foo:legacy":{"primary":{"usedPercent":10}}},"rateLimits":{"limitId":"foo:legacy","primary":{"usedPercent":10}}}"#))
    XCTAssertEqual(mirrored.windows.count, 1)
    XCTAssertEqual(mirrored.windows.first?.bucket, "foo:legacy")
    XCTAssertEqual(mirrored.windows.first?.id, "foo%3Alegacy:primary")
  }
  func testCodexValidPercentageSurvivesBadResetDurationAndNeighbor() throws {
    let data = try Parsers.codexAllowance(json(#"{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":false,"resetsAt":"invalid"},"secondary":[]}}"#))
    XCTAssertEqual(data.availability, .reported)
    let value = snapshot(.codex, data)
    XCTAssertEqual(value.consumerWindows.count, 1)
    XCTAssertEqual(value.consumerWindows.first?.remaining, 100)
    XCTAssertNil(value.consumerWindows.first?.durationMinutes)
    XCTAssertNil(value.consumerWindows.first?.reset)
    XCTAssertNil(value.primary)
    XCTAssertEqual(value.state, .live)
  }
  func testMalformedPercentageIsFailureAndMissingPercentageIsNotReported() throws {
    XCTAssertThrowsError(try Parsers.codexAllowance(json(#"{"rateLimits":{"primary":{"usedPercent":true,"windowDurationMins":300,"resetsAt":2000000000}}}"#))) {
      XCTAssertEqual($0 as? Failure, .malformed)
    }
    let value = snapshot(.codex, try Parsers.codexAllowance(json(#"{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":300,"resetsAt":2000000000}}}"#)))
    XCTAssertEqual(value.state, .notReported)
    XCTAssertNil(value.consumerWindows.first?.remaining)
    XCTAssertNotNil(value.consumerWindows.first?.reset)
  }
  func testMalformedWindowCannotEraseValidNeighbor() throws {
    let data = try Parsers.codexAllowance(json(#"{"rateLimits":{"primary":{"usedPercent":101,"windowDurationMins":300},"secondary":{"usedPercent":25,"windowDurationMins":10080}}}"#))
    let value = snapshot(.codex, data)
    XCTAssertEqual(value.primary?.durationMinutes, 10080)
    XCTAssertEqual(value.primary?.remaining, 75)
    XCTAssertNil(value.consumerWindows.first?.used)
    XCTAssertThrowsError(try Parsers.codexAllowance(json(#"{"rateLimits":{"primary":{"usedPercent":101,"windowDurationMins":300}}}"#)))
  }
  func testMissingAndUnsupportedSuccessfulResponsesNeverStayLiveOrUseOldCache() throws {
    let cases: [(NormalizedUsage, ProviderState)] = [
      (try Parsers.codexAllowance(json(#"{"rateLimitsByLimitId":{},"rateLimits":null}"#)), .notReported),
      (try Parsers.codexAllowance(json(#"{"future_format":{}}"#)), .notReported),
      (try Parsers.codexAllowance(json(#"{"rateLimits":{"primary":{"future_quota":20}}}"#)), .unsupportedAllowance),
      (try Parsers.codexAllowance(json(#"{"rateLimits":{"credits":{"balance":"opaque"},"primary":null}}"#)), .unsupportedAllowance),
      (try claude(#"{"future_internal":{"utilization":0}}"#), .unsupportedAllowance),
      (try claude(#"{}"#), .notReported),
      (try claude(#"{"extra_usage":{"used_credits":999,"monthly_limit":1000}}"#), .unsupportedAllowance),
    ]
    for (data, expected) in cases {
      var value = UsageSnapshot(provider: .codex)
      value.apply(.success(try .init(binding: "fixture", windows: [window("codex:primary", bucket: "codex")], date: Date())))
      value.apply(.success(.init(binding: "fixture", windows: data.windows, date: Date(), availability: data.availability)))
      XCTAssertEqual(value.state, expected)
      XCTAssertNil(value.primary)
      XCTAssertFalse(value.compact.contains("%"))
    }
  }
  func testBillingMetadataDoesNotChangeValidTimeAllowanceOrEmptyDataIntoUnsupportedBilling() throws {
    let codex = try Parsers.codexAllowance(json(#"{"rateLimits":{"credits":{"balance":"opaque"},"primary":{"usedPercent":20,"windowDurationMins":300}}}"#))
    XCTAssertEqual(snapshot(.codex, codex).state, .live)
    XCTAssertEqual(snapshot(.codex, codex).primary?.remaining, 80)
    XCTAssertEqual(try Parsers.codexAllowance(json(#"{"rateLimits":{"credits":{},"primary":null}}"#)).availability, .notReported)
    XCTAssertEqual(try claude(#"{"extra_usage":{}}"#).availability, .notReported)
    let models = try claude(#"{"extra_usage":{"used_credits":999},"seven_day_opus":{"utilization":30}}"#)
    XCTAssertEqual(snapshot(.claude, models).state, .live)
    XCTAssertEqual(snapshot(.claude, models).detailWindows.first?.remaining, 70)
    XCTAssertNil(snapshot(.claude, models).primary)
    XCTAssertThrowsError(try Parsers.codexAccount(json(#"{"account":{"type":"apiKey"}}"#))) {
      XCTAssertEqual($0 as? Failure, .unsupportedBilling)
    }
  }
  func testSelectionUsesOnlyUsableGeneralDurationAndDeterministicShortestFallback() throws {
    let windows = [
      try window("short", bucket: "codex", minutes: 15, used: 1),
      try window("weekly", bucket: "codex", used: 30),
      try UsageWindow(id: "model", bucket: "codex_other", label: "Model", durationMinutes: 300, used: 90, reset: nil, scope: .model("Model")),
    ]
    var value = UsageSnapshot(provider: .codex)
    value.apply(.success(.init(binding: "fixture", windows: windows, date: Date())))
    XCTAssertEqual(value.primary?.id, "weekly")
    value.apply(.success(.init(binding: "fixture", windows: [windows[2], windows[0]], date: Date())))
    XCTAssertEqual(value.primary?.id, "short")
    let five = try window("five", bucket: "codex", minutes: 300, used: 20)
    value.apply(.success(.init(binding: "fixture", windows: windows + [five], date: Date())))
    XCTAssertEqual(value.primary?.id, "five")
    value.apply(.success(.init(binding: "fixture", windows: [windows[2]], date: Date())))
    XCTAssertNil(value.primary)
    XCTAssertEqual(value.detailWindows.count, 1)
  }
  func testVerifiedClaudeModelWindowsRetainWeeklyScopeAndOpaqueFieldsDoNot() throws {
    let data = try claude(#"{"five_hour":{"utilization":0},"seven_day":{"utilization":20},"seven_day_sonnet":{"utilization":30},"seven_day_opus":{"utilization":40},"seven_day_future":{"utilization":50},"model_scoped":[{"display_name":"five_hour","utilization":60}]}"#)
    let value = snapshot(.claude, data)
    XCTAssertEqual(value.primary?.remaining, 100)
    XCTAssertEqual(value.consumerWindows.count, 5)
    XCTAssertFalse(value.consumerWindows.contains { $0.id == "seven_day_future" })
    XCTAssertEqual(data.windows.first { $0.id == "seven_day_future" }?.scope, .unknown)
    XCTAssertNil(data.windows.first { $0.id == "seven_day_future" }?.durationMinutes)
    XCTAssertEqual(data.windows.first { $0.id == "seven_day_opus" }?.scope, .model("Opus"))
    XCTAssertEqual(data.windows.first { $0.id == "model:five_hour" }?.durationMinutes, 10080)
    XCTAssertEqual(data.windows.first { $0.id == "model:five_hour" }?.scope, .model("five_hour"))
    let longUnknown = String(repeating: "x", count: 101)
    let partial = try claude("{\"five_hour\":{\"utilization\":0},\"" + longUnknown + "\":{\"utilization\":10}}")
    XCTAssertEqual(partial.windows.map(\.id), ["five_hour"])
    XCTAssertEqual(snapshot(.claude, partial).primary?.remaining, 100)
    let collided = try claude(#"{"model:Sonnet":{"utilization":10},"model_scoped":[{"display_name":"Sonnet","utilization":20}]}"#)
    let visible = snapshot(.claude, collided)
    XCTAssertEqual(collided.windows.count, 2)
    XCTAssertEqual(visible.consumerWindows.count, 1)
    XCTAssertEqual(visible.consumerWindows.first?.scope, .model("Sonnet"))
    XCTAssertEqual(visible.consumerWindows.first?.used, 20)
    XCTAssertNil(visible.primary)
  }
  func testClaudeModelMirrorsDeduplicateAndContradictoryNamesFailClosed() throws {
    let data = try claude(#"{"seven_day_opus":{"utilization":20},"model_scoped":[{"display_name":"Opus","utilization":20}]}"#)
    XCTAssertEqual(data.windows.count, 1)
    XCTAssertEqual(data.windows.first?.scope, .model("Opus"))
    XCTAssertThrowsError(try claude(#"{"seven_day_opus":{"utilization":20},"model_scoped":[{"display_name":"Opus","utilization":21}]}"#)) {
      XCTAssertEqual($0 as? Failure, .incompatible)
    }
    let modelOnly = snapshot(.claude, data)
    XCTAssertEqual(modelOnly.state, .live)
    XCTAssertNil(modelOnly.primary)
  }
  func testClaudePartialFieldsAndNeighborsKeepValidPercentagesWithoutInventingValues() throws {
    let data = try claude(#"{"five_hour":{"utilization":101,"resets_at":"2030-01-01T00:00:00Z"},"seven_day":{"utilization":0,"resets_at":"bad"},"seven_day_opus":[],"future_internal":{"utilization":true}}"#)
    let value = snapshot(.claude, data)
    XCTAssertEqual(value.primary?.id, "seven_day")
    XCTAssertEqual(value.primary?.remaining, 100)
    XCTAssertNil(value.primary?.reset)
    XCTAssertNil(data.windows.first { $0.id == "five_hour" }?.used)
    XCTAssertNotNil(data.windows.first { $0.id == "five_hour" }?.reset)
    XCTAssertThrowsError(try claude(#"{"five_hour":{"utilization":true}}"#))
  }
  func testClaudeUnavailableAndNullAllowancesDoNotInferFromPlan() throws {
    XCTAssertEqual(try claude(#"{}"#, available: false).availability, .notReported)
    XCTAssertEqual(try claude(#"{"five_hour":null,"seven_day":null}"#).availability, .notReported)
    var envelope = try XCTUnwrap(json(ParserTests.usage).object)
    envelope["rate_limits"] = .array([])
    XCTAssertThrowsError(try Parsers.claudeAllowance(.object(envelope), plan: "max")) { XCTAssertEqual($0 as? Failure, .incompatible) }
    envelope["rate_limits"] = .object([:])
    XCTAssertEqual(try Parsers.claudeAllowance(.object(envelope), plan: "max").availability, .notReported)
    envelope["rate_limits"] = .null
    XCTAssertEqual(try Parsers.claudeAllowance(.object(envelope), plan: "max").availability, .notReported)
  }
  func testStaleRequiresReverifiedBindingAndClearFailuresNeverRetainOldData() throws {
    for failure in [Failure.accountChanged, .signedOut, .notInstalled, .incompatible, .unsupportedBilling, .unsupportedAllowance] {
      var value = snapshot(.claude, try claude(#"{"five_hour":{"utilization":20}}"#))
      value.apply(.fail(failure, binding: "fixture"))
      XCTAssertNil(value.reading)
    }
    var value = snapshot(.claude, try claude(#"{"five_hour":{"utilization":20}}"#))
    let observed = value.reading?.date
    value.apply(.fail(.rateLimited, binding: "fixture"))
    XCTAssertEqual(value.state, .stale)
    XCTAssertEqual(value.reading?.date, observed)
    value.apply(.fail(.unavailable, binding: "different"))
    XCTAssertNil(value.reading)
  }
  func testResetValidationCannotTrapOrRefill() throws {
    for seconds in [Double.nan, .infinity, -.infinity, Double.greatestFiniteMagnitude] {
      XCTAssertThrowsError(try UsageWindow(id: "w", bucket: "codex", label: "General", durationMinutes: 300,
        used: 0, reset: Date(timeIntervalSince1970: seconds)))
    }
    let expired = try UsageWindow(id: "w", bucket: "codex", label: "General", durationMinutes: 300, used: 100, reset: Date(timeIntervalSince1970: 1))
    XCTAssertEqual(expired.remaining, 0)
    XCTAssertEqual(expired.resetText(at: Date()), "Reported reset time has passed")
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
  func testClaudeMissingPercentageFallsBackToReportedWeeklyAndInvalidValuesReject() throws {
    var s = UsageSnapshot(provider: .claude)
    let week = try window()
    s.apply(.success(.init(binding: "fixture", windows: [week], date: Date())))
    XCTAssertEqual(s.primary?.id, "seven_day")
    XCTAssertEqual(ProviderGlance(snapshot: s).percentage, "75%")
    XCTAssertEqual(s.detailWindows.map(\.id), ["seven_day"])
    let five = try UsageWindow(id: "five_hour", bucket: "claude", label: "", durationMinutes: 300, used: nil, reset: nil)
    s.apply(.success(.init(binding: "fixture", windows: [five, week], date: Date())))
    XCTAssertEqual(s.primary?.id, "seven_day")
    s.apply(.success(.init(binding: "fixture", windows: [five, five, week], date: Date())))
    XCTAssertEqual(s.primary?.id, "seven_day")
    XCTAssertThrowsError(try window("five_hour", minutes: 300, used: .nan))
    XCTAssertThrowsError(try window("five_hour", minutes: 300, used: 101))
  }
  func testExpiredFiveHourDoesNotRefill() throws {
    let five = try UsageWindow(id: "five_hour", bucket: "claude", label: "", durationMinutes: 300,
      used: 80, reset: Date(timeIntervalSince1970: 1))
    XCTAssertEqual(five.remaining, 20)
    XCTAssertEqual(five.resetText(at: Date()), "Reported reset time has passed")
  }
  func testDiagnosticsNeverExportPayloadIdentityOrArbitraryMetadata() throws {
    var s = UsageSnapshot(provider: .claude)
    let secret = "private@example.test /Users/example/project secret-token"
    s.apply(.success(.init(binding: secret, windows: [try UsageWindow(id: secret, bucket: secret,
      label: secret, durationMinutes: 300, used: 20, reset: nil, scope: .general)], date: Date())))
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
