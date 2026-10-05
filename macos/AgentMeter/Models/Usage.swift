import Foundation

enum ProviderID: String, CaseIterable, Sendable, Codable {
  case codex, claude
  var title: String { self == .codex ? "Codex" : "Claude" }
}
enum Failure: String, Error, Sendable {
  case notInstalled, signedOut, unavailable, malformed, incompatible, timeout, cancelled,
    outputLimit, accountChanged, processExited, unsupportedBilling, unsupportedAllowance, rateLimited
}
enum ProviderState: String, Sendable {
  case loading = "Loading"
  case live = "Live"
  case stale = "Stale"
  case notInstalled = "Not installed"
  case signedOut = "Not signed in"
  case unavailable = "Unavailable"
  case notReported = "Not reported"
  case unsupportedBilling = "Unsupported billing"
  case unsupportedAllowance = "Unsupported allowance"
}
enum UsageScope: Equatable, Sendable {
  case general, model(String), additional(String), unknown
}
enum AllowanceAvailability: Sendable, Equatable {
  case reported, notReported, unsupportedBilling, unsupportedAllowance
}
struct NormalizedUsage: Sendable {
  let windows: [UsageWindow]
  let availability: AllowanceAvailability
  init(windows: [UsageWindow], unsupportedBilling: Bool = false, unsupportedAllowance: Bool = false) {
    self.windows = windows
    if windows.contains(where: { $0.isSupported && $0.used != nil }) { availability = .reported }
    else if windows.contains(where: \.isSupported) { availability = .notReported }
    else if unsupportedBilling { availability = .unsupportedBilling }
    else if unsupportedAllowance || !windows.isEmpty { availability = .unsupportedAllowance }
    else { availability = .notReported }
  }
}
struct UsageWindow: Equatable, Sendable, Identifiable {
  let id: String
  let bucket: String
  let label: String
  let durationMinutes: Int?
  let used: Double?
  let reset: Date?
  let scope: UsageScope
  var remaining: Double? { used.map { 100 - $0 } }
  var isSupported: Bool { scope != .unknown }
  var isUsableGeneral: Bool {
    scope == .general && used != nil && durationMinutes.map { $0 > 0 } == true
  }
  var scopeLabel: String {
    switch scope {
    case .general: "General"
    case .model(let name): "Model: " + name
    case .additional(let name): name == "Additional" ? name : "Additional: " + name
    case .unknown: "Unknown"
    }
  }
  var claudeDisplayLabel: String? {
    guard bucket == "claude" else { return nil }
    switch id {
    case "five_hour": return "5 hours"
    case "seven_day": return "7 days"
    default: return nil
    }
  }
  init(
    id: String, bucket: String, label: String, durationMinutes: Int?, used: Double?, reset: Date?,
    scope: UsageScope? = nil
  ) throws {
    if let used, !used.isFinite || !(0...100).contains(used) { throw Failure.malformed }
    if let durationMinutes, durationMinutes <= 0 { throw Failure.malformed }
    if let reset, !reset.timeIntervalSince1970.isFinite || abs(reset.timeIntervalSince1970) >= 253_402_300_800 {
      throw Failure.malformed
    }
    self.id = id
    self.bucket = bucket
    self.label = label
    self.durationMinutes = durationMinutes
    self.used = used
    self.reset = reset
    self.scope = scope ?? (bucket == "codex" || (bucket == "claude" && ["five_hour", "seven_day"].contains(id))
      ? .general : .unknown)
  }
  func resetText(at now: Date) -> String {
    guard let reset else { return "Reset unknown" }
    let seconds = reset.timeIntervalSince(now)
    guard seconds > 0 else { return "Reported reset time has passed" }
    let minutes = max(1, Int(ceil(seconds / 60)))
    if minutes >= 1440 { return "Resets in \(minutes / 1440)d \((minutes % 1440) / 60)h" }
    if minutes >= 60 { return "Resets in \(minutes / 60)h \(minutes % 60)m" }
    return "Resets in \(minutes)m"
  }
}
struct Reading: Sendable {
  let binding: String
  let windows: [UsageWindow]
  let date: Date
  let availability: AllowanceAvailability
  init(binding: String, windows: [UsageWindow], date: Date, availability: AllowanceAvailability = .reported) {
    self.binding = binding
    self.windows = windows
    self.date = date
    self.availability = availability
  }
}
struct QueryResult: Sendable {
  let reading: Reading?
  let failure: Failure?
  let verifiedBinding: String?
  static func success(_ r: Reading) -> Self {
    .init(reading: r, failure: nil, verifiedBinding: r.binding)
  }
  static func fail(_ f: Failure, binding: String? = nil) -> Self {
    .init(reading: nil, failure: f, verifiedBinding: binding)
  }
}
struct UsageSnapshot: Sendable {
  let provider: ProviderID
  var state: ProviderState = .loading
  var reading: Reading?
  var failure: Failure?
  // Current query evidence only. A failed account check must not inherit a
  // previous account's readiness; allowance retrieval can fail independently.
  var authenticationVerified = false
  var refreshStatus = UsageRefreshStatus()
  var primary: UsageWindow? {
    consumerWindows.filter(\.isUsableGeneral).sorted(by: Self.windowOrder).first
  }
  func claudeWindow(_ id: String) -> UsageWindow? {
    let matches = (reading?.windows ?? []).filter { $0.id == id && $0.bucket == "claude" }
    return matches.count == 1 ? matches[0] : nil
  }
  var detailWindows: [UsageWindow] {
    consumerWindows
  }
  var consumerWindows: [UsageWindow] {
    // Defensively exclude ambiguous duplicate identities supplied by fixtures or future sources.
    let groups = Dictionary(grouping: (reading?.windows ?? []).filter(\.isSupported), by: \.id)
    return groups.values.compactMap { values -> UsageWindow? in
      guard let first = values.first, values.allSatisfy({ $0 == first }) else { return nil }
      return first
    }.sorted(by: Self.windowOrder)
  }
  static func windowOrder(_ a: UsageWindow, _ b: UsageWindow) -> Bool {
    if (a.scope == .general) != (b.scope == .general) { return a.scope == .general }
    func rank(_ w: UsageWindow) -> Int {
      w.durationMinutes == 300 ? 0 : (w.durationMinutes == 10080 ? 1 : 2)
    }
    if rank(a) != rank(b) { return rank(a) < rank(b) }
    if a.durationMinutes != b.durationMinutes { return (a.durationMinutes ?? Int.max) < (b.durationMinutes ?? Int.max) }
    if a.bucket != b.bucket { return a.bucket < b.bucket }
    return a.id < b.id
  }
  var compact: String {
    guard let remaining = primary?.remaining else {
      return "\(provider.title) · \(state == .loading ? "Loading" : "—")\(state == .stale ? " · stale" : "")"
    }
    return "\(provider.title) \(Self.percent(remaining))\(state == .stale ? " · stale" : "")"
  }
  static func percent(_ n: Double) -> String { String(format: "%.0f%%", floor(n)) }
  mutating func apply(_ result: QueryResult) {
    failure = result.failure
    authenticationVerified = result.verifiedBinding.map { !$0.isEmpty } == true
      && ![Failure.notInstalled, .signedOut, .accountChanged, .unsupportedBilling, .cancelled]
        .contains(result.failure ?? .unavailable)
    if let r = result.reading {
      reading = r
      switch r.availability {
      case .reported:
        state = consumerWindows.contains(where: { $0.used != nil }) ? .live
          : (consumerWindows.isEmpty && !r.windows.isEmpty ? .unsupportedAllowance : .notReported)
      case .notReported: state = .notReported
      case .unsupportedBilling: state = .unsupportedBilling
      case .unsupportedAllowance: state = .unsupportedAllowance
      }
      return
    }
    if let old = reading, let binding = result.verifiedBinding, binding == old.binding,
      consumerWindows.contains(where: { $0.used != nil }),
      [Failure.unavailable, .malformed, .timeout, .outputLimit, .processExited, .rateLimited].contains(
        result.failure ?? .unavailable)
    {
      state = .stale
      return
    }
    reading = nil
    switch result.failure {
    case .notInstalled: state = .notInstalled
    case .signedOut: state = .signedOut
    case .unsupportedBilling: state = .unsupportedBilling
    case .unsupportedAllowance: state = .unsupportedAllowance
    default: state = .unavailable
    }
  }
}
struct SurfaceActivity: Equatable, Sendable {
  var cli = false
  var desktop = false
  var active: Bool { cli || desktop }
}
struct ActivitySnapshot: Equatable, Sendable {
  var codex = SurfaceActivity()
  var claude = SurfaceActivity()
  subscript(_ p: ProviderID) -> SurfaceActivity { p == .codex ? codex : claude }
  var providers: [ProviderID] { ProviderID.allCases.filter { self[$0].active } }
  var name: String {
    providers.isEmpty ? "Hidden" : providers.count == 2 ? "Both" : providers[0].title
  }
  static func desktopActive(frontmost: Bool, hidden: Bool, normalWindows: Int) -> Bool {
    frontmost && !hidden && normalWindows > 0
  }
}
