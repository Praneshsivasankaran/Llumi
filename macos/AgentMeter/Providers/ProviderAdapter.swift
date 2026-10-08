import Foundation

protocol UsageSource: Sendable { func query() async -> QueryResult }
struct ProviderAdapter: UsageSource {
  private var claudeEnvironment: [String] {
    discovery.childEnvironment + ["DISABLE_TELEMETRY=1"]
  }
  let provider: ProviderID
  let discovery: ProviderDiscovery
  var discovered: @Sendable (ProviderID, Installation) -> Void = { _, _ in }
  func query() async -> QueryResult {
    let start = ContinuousClock.now
    do {
      let install = try await discovery.find(provider)
      discovered(provider, install)
      let result = provider == .codex ? await codex(install) : await claude(install)
      Diagnostics.shared.record(
        "refresh", provider: provider, code: result.failure?.rawValue ?? "success",
        seconds: Self.seconds(start.duration(to: .now)))
      return result
    } catch {
      let f = Self.failure(error)
      Diagnostics.shared.record("refresh", provider: provider, code: f.rawValue)
      return .fail(f)
    }
  }
  static func seconds(_ d: Duration) -> Double {
    Double(d.components.seconds) + Double(d.components.attoseconds) / 1e18
  }
  static func failure(_ error: Error) -> Failure {
    error is CancellationError ? .cancelled : (error as? Failure ?? .malformed)
  }
  static func rpcFailure(_ error: J) -> Failure {
    // Classify fixed numeric transport statuses only; never inspect or expose provider messages.
    guard error.object != nil, let code = error["code"].number, code.rounded() == code,
      error["message"].string != nil else { return .incompatible }
    return code == 429 || code == -32001 ? .rateLimited : .unavailable
  }
  static func codexClientInfo(info: [String: Any]? = Bundle.main.infoDictionary) -> J {
    // Use the app's marketing version, including future releases. Hostless tests
    // and library contexts have no Llumi bundle metadata; their fallback matches
    // the current local candidate rather than the frozen 1.1.1 launch release.
    let version = [info?["LlumiReleaseVersion"], info?["CFBundleShortVersionString"]]
      .compactMap { $0 as? String }.first { value in
        guard value.utf8.count <= 32 else { return false }
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 3 && parts.allSatisfy { part in
          !part.isEmpty && (part.count == 1 || part.first != "0")
            && part.utf8.allSatisfy { (48...57).contains($0) }
        }
      } ?? "1.1.3"
    return .object(["name": .string("llumi"), "version": .string(version)])
  }
  private func response(_ p: Subprocess) async throws -> J {
    do { return try await p.next() }
    catch Failure.malformed { throw Failure.incompatible }
  }
  private func rpc(_ p: Subprocess, id: Int, method: String, params: J = .object([:])) async throws
    -> J
  {
    try await p.send(
      .object(["id": .number(Double(id)), "method": .string(method), "params": params]))
    while true {
      let r = try await response(p)
      if r["id"].number == Double(id) {
        if r["error"] != .null { throw Self.rpcFailure(r["error"]) }
        guard r.object?.keys.contains("result") == true else { throw Failure.incompatible }
        return r["result"]
      }
    }
  }
  private func codex(_ install: Installation) async -> QueryResult {
    var process: Subprocess?
    do {
      let p = try Subprocess(
        executable: install.executable,
        arguments: ["app-server", "--stdio", "-c", "analytics.enabled=false"],
        environment: discovery.childEnvironment, directory: discovery.directory)
      process = p
      _ = try await rpc(
        p, id: 1, method: "initialize",
        params: .object([
          "clientInfo": Self.codexClientInfo()
        ]))
      try await p.send(.object(["method": .string("initialized")]))
      let before = try Parsers.codexAccount(
        await rpc(p, id: 2, method: "account/read", params: .object(["refreshToken": .bool(false)]))
      )
      var allowance = NormalizedUsage(windows: [])
      var failure: Failure?
      do {
        allowance = try Parsers.codexAllowance(
          await rpc(
            p, id: 3, method: "account/rateLimits/read",
            params: .object(["excludeResetCreditDetails": .bool(true)])))
      } catch { failure = Self.failure(error) }
      let after = try Parsers.codexAccount(
        await rpc(p, id: 4, method: "account/read", params: .object(["refreshToken": .bool(false)]))
      )
      guard before.binding == after.binding else { throw Failure.accountChanged }
      let peak = await p.peakRSS
      let group = await p.peakGroupRSS
      await p.close()
      Diagnostics.shared.record("query-group-memory", provider: .codex, value: Int(group))
      Diagnostics.shared.record("query-memory", provider: .codex, value: Int(peak))
      if let failure { return .fail(failure, binding: after.binding) }
      return .success(.init(binding: after.binding, windows: allowance.windows, date: Date(),
        availability: allowance.availability))
    } catch {
      if let process { await process.close() }
      return .fail(Self.failure(error))
    }
  }
  private func auth(_ install: Installation) async throws -> AuthIdentity {
    var limits = ProcessLimits()
    limits.seconds = 6
    limits.stdout = 131072
    limits.line = 131072
    let (data, status) = try await Subprocess.run(
      executable: install.executable, arguments: ["auth", "status"],
      environment: claudeEnvironment, directory: discovery.directory, limits: limits)
    return try Parsers.claudeAccount(J.parse(data), status: status)
  }
  private func control(_ p: Subprocess, id: String, request: [String: J]) async throws -> J {
    try await p.send(
      .object([
        "type": .string("control_request"), "request_id": .string(id), "request": .object(request),
      ]))
    while true {
      let r = try await response(p)
      if r["type"].string == "control_response" && r["response"]["request_id"].string == id {
        let response = r["response"]
        if response["subtype"].string == "error" { throw Failure.unavailable }
        guard response["subtype"].string == "success", response["response"].object != nil else {
          throw Failure.incompatible
        }
        return response["response"]
      }
    }
  }
  private func claude(_ install: Installation) async -> QueryResult {
    var process: Subprocess?
    do {
      let before = try await auth(install)
      let p = try Subprocess(
        executable: install.executable,
        arguments: [
          "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
          "--no-session-persistence", "--safe-mode", "--setting-sources=", "--strict-mcp-config",
          "--mcp-config", "{\"mcpServers\":{}}",
        ], environment: claudeEnvironment, directory: discovery.directory)
      process = p
      let initialized = try await control(
        p, id: "init", request: ["subtype": .string("initialize"), "hooks": .object([:])])
      try Parsers.verifyClaudeSession(initialized["account"], account: before)
      var allowance = NormalizedUsage(windows: [])
      var failure: Failure?
      do {
        allowance = try Parsers.claudeAllowance(
          await control(
            p, id: "usage",
            request: ["subtype": .string("get_usage"), "skip_behaviors": .bool(true)]),
          plan: before.plan)
      } catch { failure = Self.failure(error) }
      let peak = await p.peakRSS
      let group = await p.peakGroupRSS
      await p.close()
      Diagnostics.shared.record("query-group-memory", provider: .claude, value: Int(group))
      process = nil
      let after = try await auth(install)
      guard before.binding == after.binding else { throw Failure.accountChanged }
      Diagnostics.shared.record("query-memory", provider: .claude, value: Int(peak))
      if let failure { return .fail(failure, binding: after.binding) }
      return .success(.init(binding: after.binding, windows: allowance.windows, date: Date(),
        availability: allowance.availability))
    } catch {
      if let process { await process.close() }
      return .fail(Self.failure(error))
    }
  }
}
