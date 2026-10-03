import Foundation

enum ProviderID: String, CaseIterable, Sendable, Codable {
  case codex, claude
  var title: String { self == .codex ? "Codex" : "Claude" }
}
enum Failure: String, Error, Sendable {
  case notInstalled, signedOut, unavailable, malformed, incompatible, timeout, cancelled,
    outputLimit, accountChanged, processExited
}
enum ProviderState: String, Sendable {
  case loading = "Loading"
  case live = "Live"
  case stale = "Stale"
  case notInstalled = "Not installed"
  case signedOut = "Not signed in"
  case unavailable = "Unavailable"
}
struct UsageWindow: Equatable, Sendable, Identifiable {
  let id: String
  let bucket: String
  let label: String
  let durationMinutes: Int?
  let used: Double?
  let reset: Date?
  var remaining: Double? { used.map { 100 - $0 } }
  var claudeDisplayLabel: String? {
    guard bucket == "claude" else { return nil }
    switch id {
    case "five_hour": return "5 hours"
    case "seven_day": return "7 days"
    default: return nil
    }
  }
  init(
    id: String, bucket: String, label: String, durationMinutes: Int?, used: Double?, reset: Date?
  ) throws {
    if let used, !used.isFinite || !(0...100).contains(used) { throw Failure.malformed }
    if let durationMinutes, durationMinutes <= 0 { throw Failure.malformed }
    self.id = id
    self.bucket = bucket
    self.label = label
    self.durationMinutes = durationMinutes
    self.used = used
    self.reset = reset
  }
  func resetText(at now: Date) -> String {
    guard let reset else { return "Reset unknown" }
    let seconds = reset.timeIntervalSince(now)
    guard seconds > 0 else { return "Resetting…" }
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
  var primary: UsageWindow? {
    let windows = reading?.windows ?? []
    if provider == .codex {
      return windows.filter { $0.bucket == "codex" }.sorted {
        ($0.durationMinutes ?? 0) > ($1.durationMinutes ?? 0)
      }.first
    }
    return claudeWindow("five_hour")
  }
  func claudeWindow(_ id: String) -> UsageWindow? {
    let matches = (reading?.windows ?? []).filter { $0.id == id && $0.bucket == "claude" }
    return matches.count == 1 ? matches[0] : nil
  }
  var detailWindows: [UsageWindow] {
    provider == .claude ? [claudeWindow("five_hour"), claudeWindow("seven_day")].compactMap { $0 }
      : primary.map { [$0] } ?? []
  }
  // Parsing preserves provider windows; consumer Claude surfaces use only the
  // recognized five-hour/weekly semantics already used by the expanded monitor.
  var consumerWindows: [UsageWindow] {
    provider == .claude ? detailWindows : reading?.windows ?? []
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
    if let r = result.reading {
      reading = r
      state = .live
      return
    }
    if let old = reading, let binding = result.verifiedBinding, binding == old.binding,
      ![Failure.accountChanged, .signedOut, .notInstalled, .incompatible].contains(
        result.failure ?? .unavailable)
    {
      state = .stale
      return
    }
    reading = nil
    switch result.failure {
    case .notInstalled: state = .notInstalled
    case .signedOut: state = .signedOut
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
