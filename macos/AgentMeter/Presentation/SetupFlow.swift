import Foundation
import CoreFoundation
import Observation

enum SetupCompletion {
  static let key = "setupCompleted"
  static func isComplete(_ defaults: UserDefaults) -> Bool {
    guard let value = defaults.object(forKey: key) as? NSNumber,
      CFGetTypeID(value) == CFBooleanGetTypeID() else { return false }
    return value.boolValue
  }
  static func hasPreferences(_ values: [String: Any]) -> Bool {
    if let value = values["appearance"] as? String, AppAppearance(rawValue: value) != nil { return true }
    return ["notchEnabled", "menuEnabled"].contains { key in
      guard let value = values[key] as? NSNumber else { return false }
      return CFGetTypeID(value) == CFBooleanGetTypeID()
    }
  }
  static func recognizeExisting(defaults: UserDefaults, old: [String: Any]) {
    guard defaults.object(forKey: key) == nil else { return }
    let current = Dictionary(uniqueKeysWithValues: ["appearance", "notchEnabled", "menuEnabled"].compactMap {
      key in defaults.object(forKey: key).map { (key, $0) }
    })
    // Persist false for a fresh install too: changing preferences partway through setup
    // must not make an incomplete first run look like an existing installation later.
    defaults.set(hasPreferences(current) || hasPreferences(old), forKey: key)
  }
}

enum SetupStep: Equatable {
  case welcome, providers, codex, claude, verify, preferences, done
  static let ordered: [SetupStep] = [.welcome, .providers, .codex, .claude, .verify, .preferences, .done]
}

enum SetupStatus: String {
  case checking = "Checking…"
  case notChecked = "Sign-in not checked"
  case notInstalled = "Not installed"
  case signedOut = "Sign in required"
  case ready = "Signed in"
  case unsupportedBilling = "Other billing mode detected"
  case unavailable = "Sign-in not verified"
  init(snapshot: UsageSnapshot) {
    if snapshot.refreshStatus.checking { self = .checking; return }
    if snapshot.authenticationVerified { self = .ready; return }
    switch snapshot.state {
    case .notInstalled: self = .notInstalled
    case .signedOut: self = .signedOut
    case .loading: self = .notChecked
    case .unsupportedBilling: self = .unsupportedBilling
    default: self = .unavailable
    }
  }
}

enum SetupMonitoring {
  static func text(_ snapshot: UsageSnapshot) -> String {
    if snapshot.refreshStatus.checking { return "Checking allowances…" }
    switch snapshot.state {
    case .live: return snapshot.reading == nil ? "Unavailable" : "Available"
    case .notReported: return "Allowances not reported"
    case .unsupportedBilling: return "This billing mode can’t be monitored"
    case .unsupportedAllowance: return "Allowance format not supported"
    case .stale: return "Last known allowances — stale"
    case .notInstalled, .signedOut: return "Not ready"
    case .loading: return "Not checked yet"
    case .unavailable: return "Allowances unavailable"
    }
  }
}

enum SetupRetryPresentation {
  static func canRetry(_ snapshot: UsageSnapshot, at now: Date) -> Bool {
    guard !snapshot.refreshStatus.checking else { return false }
    guard let retry = snapshot.refreshStatus.retryAt, retry > now else { return true }
    return snapshot.refreshStatus.retryReason == .cooldown
  }
  static func message(_ snapshot: UsageSnapshot, at now: Date) -> String? {
    let status = snapshot.refreshStatus
    if status.checking { return "Checking…" }
    guard let retry = status.retryAt, retry > now, retry.timeIntervalSince(now).isFinite
    else { return nil }
    // Wall-clock changes can make a bounded collector deadline appear farther
    // away. Keep the reason visible without overflowing a duration conversion.
    if retry.timeIntervalSince(now) > 900 {
      let date = retry.formatted(date: .abbreviated, time: .shortened)
      switch status.retryReason {
      case .rateLimited: return "Rate limited. Retry after \(date)."
      case .manualCooldown: return "Retry after \(date)."
      case .cooldown: return "Automatic retry after \(date). Retry checks now."
      case nil: return nil
      }
    }
    let seconds = Int(ceil(retry.timeIntervalSince(now)))
    let duration = seconds < 60 ? "\(seconds)s" : "\(Int(ceil(Double(seconds) / 60)))m"
    switch status.retryReason {
    case .rateLimited: return "Rate limited. Retry in \(duration)."
    case .manualCooldown: return "Retry in \(duration)."
    case .cooldown: return "Automatic retry in \(duration). Retry checks now."
    case nil: return nil
    }
  }
}

@MainActor @Observable final class SetupFlow {
  private let defaults: UserDefaults
  let preferences: Preferences
  private(set) var step: SetupStep = .welcome
  var selected: Set<ProviderID> {
    get { preferences.enabledProviders }
    set { preferences.enabledProviders = newValue }
  }
  var needsAutomaticSetup: Bool { !SetupCompletion.isComplete(defaults) }
  var steps: [SetupStep] {
    [.welcome, .providers] + (selected.contains(.codex) ? [.codex] : [])
      + (selected.contains(.claude) ? [.claude] : []) + [.verify, .preferences, .done]
  }
  init(defaults: UserDefaults = .standard, preferences: Preferences? = nil) {
    self.defaults = defaults
    self.preferences = preferences ?? Preferences(defaults: defaults)
  }
  func reopen() { step = .welcome }
  func next() {
    guard let index = SetupStep.ordered.firstIndex(of: step),
      let next = SetupStep.ordered.dropFirst(index + 1).first(where: steps.contains) else { return }
    step = next
  }
  func back() {
    guard let index = SetupStep.ordered.firstIndex(of: step),
      let previous = SetupStep.ordered.prefix(index).last(where: steps.contains) else { return }
    step = previous
  }
  func complete() { defaults.set(true, forKey: SetupCompletion.key) }
}

// Copy-only instructions verified against official docs on 2026-09-23.
enum ProviderSetup {
  static func install(_ provider: ProviderID) -> String {
    provider == .codex ? "brew install --cask codex" : "curl -fsSL https://claude.ai/install.sh | bash"
  }
  static func login(_ provider: ProviderID) -> String {
    provider == .codex ? "codex login" : "claude auth login"
  }
  static func documentation(_ provider: ProviderID) -> URL {
    URL(string: provider == .codex ? "https://learn.chatgpt.com/docs/codex/cli" : "https://code.claude.com/docs/en/quickstart")!
  }
}

// Deliberately no Reading, Installation, raw errors, or log text in this export.
struct SetupDiagnostic {
  let detected: String
  let authentication: String
  let usage: String
  let failure: String
  let result: String
  init(_ snapshot: UsageSnapshot) {
    authentication = snapshot.authenticationVerified ? "verified"
      : snapshot.state == .signedOut ? "signed-out"
      : snapshot.state == .unsupportedBilling ? "mode-detected" : "unknown"
    switch snapshot.state {
    case .live where snapshot.reading != nil:
      detected = "yes"; usage = "available"; result = "success"
    case .notReported where snapshot.reading != nil:
      detected = "yes"; usage = "not-reported"; result = "success"
    case .unsupportedAllowance where snapshot.reading != nil:
      detected = "yes"; usage = "unsupported"; result = "success"
    case .unsupportedBilling:
      detected = "yes"; usage = "unsupported"; result = snapshot.reading == nil ? "failure" : "success"
    case .notInstalled:
      detected = "no"; usage = "unavailable"; result = "failure"
    case .signedOut:
      detected = "yes"; usage = "unavailable"; result = "failure"
    case .stale:
      detected = snapshot.authenticationVerified ? "yes" : "unknown"; usage = "stale"; result = "failure"
    case .loading:
      detected = "unknown"; usage = "checking"; result = "pending"
    default:
      detected = snapshot.authenticationVerified ? "yes" : "unknown"; usage = "unavailable"; result = "failure"
    }
    failure = snapshot.failure?.rawValue ?? "none"
  }
  var summary: String { "Detected: \(detected) · Authentication: \(authentication) · Usage: \(usage)" }
}
enum SetupDiagnostics {
  static func numeric(_ value: String?) -> String {
    guard let value, value.count <= 32, !value.isEmpty,
      value.split(separator: ".", omittingEmptySubsequences: false).allSatisfy({
        !$0.isEmpty && $0.utf8.allSatisfy { (48...57).contains($0) }
      }) else { return "unknown" }
    return value
  }
  static func report(_ usage: [ProviderID: UsageSnapshot], version: String?, build: String?) -> String {
    let os = ProcessInfo.processInfo.operatingSystemVersion
    #if arch(arm64)
    let architecture = "arm64"
    #elseif arch(x86_64)
    let architecture = "x86_64"
    #else
    let architecture = "other"
    #endif
    var lines = ["Llumi diagnostics schema: 1", "App version: \(numeric(version))",
      "App build: \(numeric(build))", "OS: macOS \(os.majorVersion).\(os.minorVersion).\(os.patchVersion)",
      "Architecture: \(architecture)"]
    for provider in ProviderID.allCases {
      let value = SetupDiagnostic(usage[provider] ?? UsageSnapshot(provider: provider))
      lines += ["[\(provider.rawValue)]", "Detected: \(value.detected)",
        "Authentication: \(value.authentication)", "Usage: \(value.usage)",
        "Failure: \(value.failure)", "Last result: \(value.result)"]
    }
    return lines.joined(separator: "\n")
  }
}
