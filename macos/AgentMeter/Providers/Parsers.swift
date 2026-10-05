import CryptoKit
import Foundation

struct AuthIdentity: Sendable, Equatable {
  let email: String
  let organization: String
  let organizationName: String
  let plan: String
  let kind: String
  var binding: String {
    SHA256.hash(data: Data([kind, email, organization, plan].joined(separator: "\n").utf8)).map {
      String(format: "%02x", $0)
    }.joined()
  }
}
enum Parsers {
  static func text(_ j: J, max: Int = 320) throws -> String {
    guard let s = j.string, !s.isEmpty, s.count <= max,
      s == s.trimmingCharacters(in: .whitespacesAndNewlines),
      !s.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
    else { throw Failure.malformed }
    return s
  }
  static func email(_ j: J) throws -> String {
    let s = try text(j)
    guard s.contains("@"), !s.contains(where: \.isWhitespace) else { throw Failure.incompatible }
    return s.lowercased()
  }
  static func codexAccount(_ j: J) throws -> AuthIdentity {
    guard let object = j.object, object.keys.contains("account") else { throw Failure.malformed }
    let a = j["account"]
    if a == .null { throw Failure.signedOut }
    let type = try text(a["type"])
    if type == "apiKey" { throw Failure.unsupportedBilling }
    guard ["chatgpt", "chatgptAuthTokens"].contains(type) else { throw Failure.incompatible }
    return try .init(
      email: email(a["email"]), organization: "", organizationName: "",
      plan: text(a["planType"], max: 80), kind: type)
  }
  static func claudeAccount(_ j: J, status: Int32) throws -> AuthIdentity {
    guard let logged = j["loggedIn"].bool else { throw Failure.malformed }
    if !logged {
      guard [0, 1].contains(status) else { throw Failure.processExited }
      throw Failure.signedOut
    }
    guard status == 0 else { throw Failure.malformed }
    if j["authMethod"].string == "api_key" || ["bedrock", "vertex", "foundry"].contains(j["apiProvider"].string ?? "") {
      throw Failure.unsupportedBilling
    }
    guard j["authMethod"].string == "claude.ai", j["apiProvider"].string == "firstParty",
      let plan = j["subscriptionType"].string, ["pro", "max", "team", "enterprise"].contains(plan)
    else { throw Failure.incompatible }
    // Helpers explicitly opt out. Missing/changed confirmation fails closed;
    // telemetry policy is separate from the no-inference checks in claude().
    guard j["analyticsDisabled"].bool == true else { throw Failure.incompatible }
    let org = try text(j["orgId"], max: 160)
    guard UUID(uuidString: org) != nil else { throw Failure.incompatible }
    return try .init(
      email: email(j["email"]), organization: org, organizationName: text(j["orgName"]), plan: plan,
      kind: "claude.ai")
  }
  static func verifyClaudeSession(_ j: J, account: AuthIdentity) throws {
    guard try email(j["email"]) == account.email, j["apiProvider"].string == "firstParty",
      [account.organization, account.organizationName].contains(j["organization"].string ?? ""),
      j["apiKeySource"] == .null || j["apiKeySource"].string == "none"
    else { throw Failure.accountChanged }
    if let source = j["tokenSource"].string,
      !["claude.ai", "oauth", "claudeAiOauth", "CLAUDE_CODE_OAUTH_TOKEN"].contains(source)
    {
      throw Failure.incompatible
    }
  }
  static func percent(_ j: J) throws -> Double? {
    if j == .null { return nil }
    guard let n = j.number, n.isFinite, (0...100).contains(n) else { throw Failure.malformed }
    return n
  }
  static func unix(_ j: J) throws -> Date? {
    if j == .null { return nil }
    guard let n = j.number, n.rounded() == n, n > 0, n < 253_402_300_800 else {
      throw Failure.malformed
    }
    return Date(timeIntervalSince1970: n)
  }
  static func iso(_ j: J) throws -> Date? {
    if j == .null { return nil }
    let s = try text(j, max: 64)
    guard
      s.range(
        of: #"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$"#,
        options: .regularExpression) != nil
    else { throw Failure.malformed }
    let formatter = ISO8601DateFormatter()
    formatter.formatOptions =
      s.contains(".") ? [.withInternetDateTime, .withFractionalSeconds] : [.withInternetDateTime]
    guard let date = formatter.date(from: s) else { throw Failure.malformed }
    // Validate calendar components explicitly; parsers must not normalize February 30.
    let parts = Array(s.prefix(19).utf8)
    func n(_ a: Int, _ b: Int) -> Int { Int(String(decoding: parts[a..<b], as: UTF8.self)) ?? -1 }
    let y = n(0, 4)
    let m = n(5, 7)
    let d = n(8, 10)
    let h = n(11, 13)
    let mi = n(14, 16)
    let sec = n(17, 19)
    let leap = y % 4 == 0 && (y % 100 != 0 || y % 400 == 0)
    let days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
    guard y > 0, (1...12).contains(m), (1...days[m - 1]).contains(d), (0...23).contains(h),
      (0...59).contains(mi), (0...59).contains(sec)
    else { throw Failure.malformed }
    if !s.hasSuffix("Z") {
      let zone = String(s.suffix(6))
      let hh = Int(zone.dropFirst().prefix(2)) ?? 99
      let mm = Int(zone.suffix(2)) ?? 99
      guard hh <= 14, mm <= 59, hh < 14 || mm == 0 else { throw Failure.malformed }
    }
    return date
  }
  static func codex(_ j: J) throws -> [UsageWindow] {
    try codexAllowance(j).windows
  }
  private static func duration(_ j: J) throws -> Int? {
    if j == .null { return nil }
    guard let n = j.number, n.rounded() == n, n > 0, n < 5_256_000 else { throw Failure.malformed }
    return Int(n)
  }
  private static func optionalField<T>(_ parse: () throws -> T?, malformed: inout Bool) -> T? {
    do { return try parse() } catch { malformed = true; return nil }
  }
  private static func hasMetadata(_ value: J) -> Bool {
    switch value {
    case .null: false
    case .object(let object): !object.isEmpty
    case .array(let array): !array.isEmpty
    default: true
    }
  }
  // Escape transport separators injectively while retaining familiar IDs for ordinary bucket names.
  private static func identityBucket(_ bucket: String) -> String {
    bucket.replacingOccurrences(of: "%", with: "%25").replacingOccurrences(of: ":", with: "%3A")
  }
  // Slot names are transport identifiers. Known duration and scope identify equivalent windows.
  private static func merge(_ window: UsageWindow, into result: inout [UsageWindow], codexMirror: Bool = false) throws {
    func sameTransportSlot(_ old: UsageWindow) -> Bool {
      old.id.hasPrefix(identityBucket(old.bucket) + ":") && window.id.hasPrefix(identityBucket(window.bucket) + ":")
        && old.id.split(separator: ":").last == window.id.split(separator: ":").last
    }
    let candidates = result.indices.filter { i in
      let old = result[i]
      return old.bucket == window.bucket && old.scope == window.scope
        && (!codexMirror || !old.id.hasPrefix(identityBucket(old.bucket) + ":legacy:"))
        && ((old.durationMinutes != nil && old.durationMinutes == window.durationMinutes)
          || ((old.id == window.id || sameTransportSlot(old)) && (old.durationMinutes == nil || window.durationMinutes == nil)))
    }
    let match = candidates.count > 1 && codexMirror
      ? candidates.first(where: { sameTransportSlot(result[$0]) }) : candidates.first
    if codexMirror && candidates.count > 1 && match == nil { throw Failure.incompatible }
    if let i = match {
      let old = result[i]
      guard old.used == nil || window.used == nil || old.used == window.used,
        old.reset == nil || window.reset == nil || old.reset == window.reset
      else { throw Failure.incompatible }
      result[i] = try .init(id: old.id, bucket: old.bucket, label: old.label,
        durationMinutes: old.durationMinutes ?? window.durationMinutes, used: old.used ?? window.used,
        reset: old.reset ?? window.reset, scope: old.scope)
    } else { result.append(window) }
  }
  static func codexAllowance(_ j: J) throws -> NormalizedUsage {
    guard j.object != nil else { throw Failure.incompatible }
    var buckets: [String: J] = [:]
    if j["rateLimitsByLimitId"] != .null {
      guard let map = j["rateLimitsByLimitId"].object else { throw Failure.incompatible }
      buckets = map
    }
    guard buckets.count <= 32 else { throw Failure.outputLimit }
    var legacy: (String, J)?
    if j["rateLimits"] != .null {
      guard j["rateLimits"].object != nil else { throw Failure.incompatible }
      let id = j["rateLimits"]["limitId"] == .null ? "codex" : try text(j["rateLimits"]["limitId"], max: 120)
      legacy = (id, j["rateLimits"])
    }
    var result: [UsageWindow] = []
    var malformed = false
    var unsupported = hasMetadata(j["credits"]) || hasMetadata(j["spend"])
    func parseBucket(_ key: String, _ bucket: J, legacyIDs: Bool) throws {
      _ = try text(.string(key), max: 120)
      guard bucket.object != nil,
        bucket["limitId"] == .null || bucket["limitId"].string == key
      else { throw Failure.incompatible }
      let mapName = try? text(buckets[key]?["limitName"] ?? .null, max: 100)
      let legacyName = legacy?.0 == key ? try? text(legacy!.1["limitName"], max: 100) : nil
      if let mapName, let legacyName, mapName != legacyName { throw Failure.incompatible }
      let label = key == "codex" ? "General" : mapName ?? legacyName ?? "Additional"
      let scope: UsageScope = key == "codex" ? .general : .additional(label)
      unsupported = unsupported || hasMetadata(bucket["credits"]) || hasMetadata(bucket["spend"])
      for slot in ["primary", "secondary"] {
        let w = bucket[slot]
        if w == .null { continue }
        guard let object = w.object else { malformed = true; continue }
        if !object.isEmpty && !["windowDurationMins", "usedPercent", "resetsAt"].contains(where: { object.keys.contains($0) }) {
          unsupported = true; continue
        }
        let minutes = optionalField({ try duration(w["windowDurationMins"]) }, malformed: &malformed)
        let used = optionalField({ try percent(w["usedPercent"]) }, malformed: &malformed)
        let reset = optionalField({ try unix(w["resetsAt"]) }, malformed: &malformed)
        let window = try UsageWindow(id: identityBucket(key) + (legacyIDs ? ":legacy:" : ":") + slot, bucket: key, label: label,
          durationMinutes: minutes, used: used, reset: reset, scope: scope)
        if legacyIDs { try merge(window, into: &result, codexMirror: true) }
        else { result.append(window) }
      }
    }
    for key in buckets.keys.sorted() { try parseBucket(key, buckets[key]!, legacyIDs: false) }
    if let legacy { try parseBucket(legacy.0, legacy.1, legacyIDs: !buckets.isEmpty) }
    if malformed && !result.contains(where: { $0.used != nil }) { throw Failure.malformed }
    return .init(windows: result, unsupportedAllowance: unsupported)
  }
  static func claude(_ j: J, plan: String) throws -> [UsageWindow] {
    try claudeAllowance(j, plan: plan).windows
  }
  static func claudeAllowance(_ j: J, plan: String) throws -> NormalizedUsage {
    guard j.object != nil, let available = j["rate_limits_available"].bool,
      j.object?.keys.contains("behaviors") == true, j["behaviors"] == .null else { throw Failure.incompatible }
    guard j["subscription_type"].string == plan else { throw Failure.accountChanged }
    guard j["session"]["total_cost_usd"].number == 0,
      j["session"]["total_api_duration_ms"].number == 0,
      j["session"]["model_usage"].object?.isEmpty == true
    else { throw Failure.incompatible }
    guard available, j["rate_limits"] != .null else { return .init(windows: []) }
    guard let limits = j["rate_limits"].object else { throw Failure.incompatible }
    guard limits.count <= 64 else { throw Failure.outputLimit }
    var result: [UsageWindow] = []
    var malformed = false
    var unsupported = hasMetadata(limits["extra_usage"] ?? .null)
    for key in limits.keys.sorted() where key != "model_scoped" && key != "extra_usage" {
      let w = limits[key]!
      if w == .null { continue }
      let known = ["five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus"].contains(key)
      guard let object = w.object else {
        if known { malformed = true } else { unsupported = true }
        continue
      }
      if !known && !object.keys.contains("utilization") && !object.keys.contains("resets_at") {
        unsupported = true; continue
      }
      let scope: UsageScope
      let minutes: Int?
      switch key {
      case "five_hour": scope = .general; minutes = 300
      case "seven_day": scope = .general; minutes = 10080
      case "seven_day_sonnet": scope = .model("Sonnet"); minutes = 10080
      case "seven_day_opus": scope = .model("Opus"); minutes = 10080
      default: scope = .unknown; minutes = nil
      }
      var fieldMalformed = false
      let used = optionalField({ try percent(w["utilization"]) }, malformed: &fieldMalformed)
      let reset = optionalField({ try iso(w["resets_at"]) }, malformed: &fieldMalformed)
      malformed = malformed || (scope != .unknown && fieldMalformed)
      guard (try? text(.string(key), max: 100)) != nil else { unsupported = true; continue }
      let label = scope == .unknown ? "" : key.replacingOccurrences(of: "_", with: " ")
      try merge(.init(id: key, bucket: "claude", label: label,
        durationMinutes: minutes, used: used, reset: reset, scope: scope), into: &result)
    }
    if let scoped = limits["model_scoped"], scoped != .null {
      guard let a = scoped.array, a.count <= 32 else { throw Failure.incompatible }
      for w in a {
        guard let name = try? text(w["display_name"], max: 100), w.object != nil else { malformed = true; continue }
        let used = optionalField({ try percent(w["utilization"]) }, malformed: &malformed)
        let reset = optionalField({ try iso(w["resets_at"]) }, malformed: &malformed)
        try merge(.init(id: "model:" + name, bucket: "claude", label: name, durationMinutes: 10080,
          used: used, reset: reset, scope: .model(name)), into: &result)
      }
    }
    if malformed && !result.contains(where: { $0.isSupported && $0.used != nil }) { throw Failure.malformed }
    return .init(windows: result, unsupportedAllowance: unsupported)
  }
}
