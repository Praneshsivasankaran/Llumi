import CryptoKit
import Foundation

struct UsageAlertThreshold: Codable, Equatable, Sendable, Identifiable {
  var id: UUID
  var percent: Int
  var enabled: Bool

  init(id: UUID = UUID(), percent: Int, enabled: Bool = true) {
    self.id = id
    self.percent = percent
    self.enabled = enabled
  }

  static var defaults: [Self] {
    [75, 90, 95].map {
      Self(id: UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))!, percent: $0)
    }
  }
}

struct UsageAlertProviderSettings: Codable, Equatable, Sendable {
  var enabled = true
  var thresholds = UsageAlertThreshold.defaults
}

struct UsageAlertSettings: Codable, Equatable, Sendable {
  var enabled = false
  var providers: [ProviderID: UsageAlertProviderSettings] = Dictionary(
    uniqueKeysWithValues: ProviderID.allCases.map { ($0, UsageAlertProviderSettings()) })

  func provider(_ provider: ProviderID) -> UsageAlertProviderSettings {
    providers[provider] ?? UsageAlertProviderSettings()
  }

  var isValid: Bool {
    guard providers.count <= ProviderID.allCases.count else { return false }
    return providers.values.allSatisfy { settings in
      settings.thresholds.count <= 8
        && settings.thresholds.allSatisfy { (1...100).contains($0.percent) }
        && Set(settings.thresholds.map(\.id)).count == settings.thresholds.count
        && Set(settings.thresholds.map(\.percent)).count == settings.thresholds.count
    }
  }

  // Invalid settings never silently turn notifications on or repair a threshold
  // into another value. The UI can preserve an unfinished edit separately.
  func sanitized() -> Self { isValid ? self : Self() }
}

enum UsageAlertPersistence {
  static let settingsKey = "llumiUsageAlertSettingsV1"
  static let receiptsKey = "llumiUsageAlertReceiptsV1"
  static let maximumSettingsBytes = 16_384

  static func loadSettings(defaults: UserDefaults = .standard) -> UsageAlertSettings {
    guard let object = defaults.object(forKey: settingsKey) else { return .init() }
    guard let data = object as? Data, data.count <= maximumSettingsBytes,
      let settings = try? JSONDecoder().decode(UsageAlertSettings.self, from: data)
    else { return .init() }
    return settings.sanitized()
  }

  @discardableResult
  static func saveSettings(_ settings: UsageAlertSettings, defaults: UserDefaults = .standard) -> Bool {
    guard let data = try? JSONEncoder().encode(settings.sanitized()),
      data.count <= maximumSettingsBytes else { return false }
    defaults.set(data, forKey: settingsKey)
    return defaults.data(forKey: settingsKey) == data
  }
}

struct UsageAlertNotice: Equatable, Sendable, Identifiable {
  let id: String
  let provider: ProviderID
  let windowLabel: String
  let usedPercent: Double
  let thresholdPercent: Int
  let reset: Date
  // Exact origin for delivery-time continuity checks; never persisted or logged.
  let sourceWindow: UsageWindow
  var title: String { "\(provider.title) usage alert" }
  var body: String {
    "\(windowLabel): \(Int(floor(usedPercent)))% used (your \(thresholdPercent)% alert)."
  }
}

@MainActor final class UsageAlertEvaluator {
  static let maximumRecords = 128
  static let maximumReceiptBytes = 65_536
  static let maximumReadingAge: TimeInterval = 120
  private let defaults: UserDefaults
  private var records: [String: Receipt] = [:]
  private var previouslyEnabled = false
  private var pendingOptIn = false
  private(set) var persistenceAvailable = true
  private(set) var statusMessage: String?

  private struct Receipt: Codable, Equatable {
    let shape: String
    var reset: Double
    // This is a receipt range for configurable integer thresholds, not stored
    // usage samples. Every lower crossed threshold is acknowledged at once.
    var consumedThrough: Int
  }
  private struct Envelope: Codable {
    let version: Int
    let records: [String: Receipt]
  }

  init(defaults: UserDefaults = .standard) {
    self.defaults = defaults
    guard let object = defaults.object(forKey: UsageAlertPersistence.receiptsKey) else { return }
    guard let data = object as? Data, data.count <= Self.maximumReceiptBytes,
      let decoded = try? JSONDecoder().decode(Envelope.self, from: data), decoded.version == 1,
      decoded.records.count <= Self.maximumRecords,
      decoded.records.allSatisfy({ Self.isDigest($0.key) && Self.isValid($0.value) })
    else { failPersistence(); return }
    records = decoded.records
  }

  func evaluate(
    snapshots: [ProviderID: UsageSnapshot], settings: UsageAlertSettings,
    enabledProviders: Set<ProviderID>, at now: Date = Date()
  ) -> [UsageAlertNotice] {
    let enabled = settings.isValid && settings.enabled
    if enabled && !previouslyEnabled { pendingOptIn = true }
    if !enabled { pendingOptIn = false }
    let firstOptIn = pendingOptIn
    previouslyEnabled = enabled
    guard enabled, persistenceAvailable, Self.isValidDate(now) else { return [] }
    let oldRecords = records
    // Elapsed windows can never alert again; pruning them cannot recreate a
    // future window and keeps account-switch receipts bounded.
    records = records.filter {
      $0.value.reset + Self.maximumReadingAge + 5 > now.timeIntervalSince1970
    }
    var candidates: [UsageAlertNotice] = []
    var hasEligibleObservation = false
    for provider in ProviderID.allCases where enabledProviders.contains(provider) {
      let control = settings.provider(provider)
      guard control.enabled, let snapshot = snapshots[provider], snapshot.provider == provider,
        snapshot.state == .live, snapshot.authenticationVerified, snapshot.failure == nil,
        !snapshot.refreshStatus.checking,
        let reading = snapshot.reading, reading.availability == .reported,
        !reading.binding.isEmpty, reading.binding.utf8.count <= 4096,
        Self.isValidDate(reading.date),
        (-5...Self.maximumReadingAge).contains(now.timeIntervalSince(reading.date))
      else { continue }
      for window in snapshot.consumerWindows {
        guard let used = window.used, used.isFinite, (0...100).contains(used),
          let reset = window.reset, Self.isValidDate(reset), reset > now,
          reset.timeIntervalSince(now) <= 366 * 24 * 60 * 60,
          !window.id.isEmpty, !window.bucket.isEmpty,
          window.id.utf8.count <= 256, window.bucket.utf8.count <= 256
        else { continue }
        let key = Self.digest(["receipt-v1", provider.rawValue, reading.binding, window.bucket,
          Self.receiptWindowID(window, provider: provider)])
        let shape = Self.digest(["shape-v1", String(window.durationMinutes ?? 0), Self.scopeKey(window.scope)])
        let reportedReset = reset.timeIntervalSince1970
        // Keep the prior record from before pruning: a new future reset may
        // re-arm only after the old boundary and from a post-boundary reading.
        let previous = records[key] ?? oldRecords[key]
        var receipt: Receipt
        if let previous {
          if previous.reset == reportedReset {
            guard previous.shape == shape else { continue }
            receipt = previous
          } else {
            guard previous.reset <= now.timeIntervalSince1970,
              reportedReset > previous.reset, reading.date.timeIntervalSince1970 >= previous.reset
            else { continue }
            receipt = Receipt(shape: shape, reset: reportedReset, consumedThrough: 0)
          }
        } else {
          guard records.count < Self.maximumRecords else {
            statusMessage = "Alert history is full. New windows wait until older windows expire."
            continue
          }
          receipt = Receipt(shape: shape, reset: reportedReset, consumedThrough: 0)
        }
        hasEligibleObservation = true
        let highest = control.thresholds.filter {
          $0.enabled && Double($0.percent) <= used && $0.percent > receipt.consumedThrough
        }.map(\.percent).max()
        // A usage decrease, toggling a threshold or editing it to a lower value
        // cannot undo receipts or generate a late burst from the same reading.
        receipt.consumedThrough = max(receipt.consumedThrough, Int(floor(used)))
        records[key] = receipt
        if let highest {
          candidates.append(UsageAlertNotice(
            id: "llumi-usage-" + Self.digest([key, String(reportedReset), String(highest)]),
            provider: provider, windowLabel: Self.windowLabel(window), usedPercent: used,
            thresholdPercent: highest, reset: reset, sourceWindow: window))
        }
      }
    }
    if hasEligibleObservation { pendingOptIn = false }
    guard records != oldRecords else { return [] }
    guard saveReceipts() else { records = oldRecords; failPersistence(); return [] }
    // First opt-in is one current-status notice across all windows/providers.
    // Other crossed receipts are still committed, avoiding a later burst.
    if firstOptIn {
      return candidates.sorted {
        if $0.thresholdPercent != $1.thresholdPercent { return $0.thresholdPercent > $1.thresholdPercent }
        if $0.usedPercent != $1.usedPercent { return $0.usedPercent > $1.usedPercent }
        if $0.provider != $1.provider { return $0.provider.rawValue < $1.provider.rawValue }
        return $0.id < $1.id
      }.prefix(1).map { $0 }
    }
    return candidates
  }

  private func saveReceipts() -> Bool {
    guard records.count <= Self.maximumRecords,
      let data = try? JSONEncoder().encode(Envelope(version: 1, records: records)),
      data.count <= Self.maximumReceiptBytes else { return false }
    defaults.set(data, forKey: UsageAlertPersistence.receiptsKey)
    return defaults.data(forKey: UsageAlertPersistence.receiptsKey) == data
  }
  private func failPersistence() {
    persistenceAvailable = false
    statusMessage = "Alert history could not be saved or read. Alerts are paused to avoid duplicates."
  }
  private static func isValid(_ receipt: Receipt) -> Bool {
    isDigest(receipt.shape) && (0...100).contains(receipt.consumedThrough)
      && receipt.reset.isFinite && abs(receipt.reset) < 253_402_300_800
  }
  private static func isValidDate(_ date: Date) -> Bool {
    date.timeIntervalSince1970.isFinite && abs(date.timeIntervalSince1970) < 253_402_300_800
  }
  private static func isDigest(_ value: String) -> Bool {
    value.utf8.count == 64 && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
  }
  private static func digest(_ components: [String]) -> String {
    // Length prefixes prevent ambiguous concatenation. Only the digest is
    // persisted; source account bindings/window names never enter preferences.
    var data = Data()
    for component in components {
      let bytes = Data(component.utf8)
      data.append(Data("\(bytes.count):".utf8))
      data.append(bytes)
    }
    return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
  }
  private static func scopeKey(_ scope: UsageScope) -> String {
    switch scope {
    case .general: "general"
    case .model(let name): "model:" + name
    case .additional(let name): "additional:" + name
    case .unknown: "unknown"
    }
  }
  private static func receiptWindowID(_ window: UsageWindow, provider: ProviderID) -> String {
    guard provider == .codex else { return window.id }
    // Codex's legacy envelope adds ":legacy:" when an unrelated map bucket
    // appears. This changes transport IDs without changing the quota window.
    // Recognize only exact parser-owned forms, preserving escaped bucket IDs
    // and separate primary/secondary windows even when durations are equal.
    let bucket = window.bucket.replacingOccurrences(of: "%", with: "%25")
      .replacingOccurrences(of: ":", with: "%3A")
    for slot in ["primary", "secondary"] {
      let canonical = bucket + ":" + slot
      if window.id == canonical || window.id == bucket + ":legacy:" + slot { return canonical }
    }
    return window.id
  }
  private static func windowLabel(_ window: UsageWindow) -> String {
    let duration: String
    if let minutes = window.durationMinutes {
      if minutes % 1440 == 0 { duration = "\(minutes / 1440)-day window" }
      else if minutes % 60 == 0 { duration = "\(minutes / 60)-hour window" }
      else { duration = "\(minutes)-minute window" }
    } else { duration = "Quota window" }
    switch window.scope {
    case .model(let name), .additional(let name):
      let safeName = String(name.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }
        .map(String.init).joined().prefix(40)).trimmingCharacters(in: .whitespacesAndNewlines)
      return safeName.isEmpty ? duration : "\(safeName) · \(duration)"
    default: return duration
    }
  }
}
