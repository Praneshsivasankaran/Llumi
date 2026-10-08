import AppKit
import Observation
import ServiceManagement
import CoreFoundation

enum BetaPreferences {
  // Ordered newest first. These domains are read-only migration sources.
  static let oldDomain = "local.agentmeter.mac"
  static let legacyDomains = ["io.github.praneshsivasankaran.agentmeter", oldDomain]
  static let completion = "llumiPreferencesMigrated"
  static func boolean(_ value: Any?) -> Bool? {
    guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
    return number.boolValue
  }
  static func valid(_ value: Any?, key: String) -> Bool {
    if key == "appearance" { return (value as? String).flatMap(AppAppearance.init(rawValue:)) != nil }
    return boolean(value) != nil
  }
  static func migrateIfNeeded() {
    let defaults = UserDefaults.standard
    guard boolean(defaults.object(forKey: completion)) != true else { return }
    migrate(sources: legacyDomains.map { defaults.persistentDomain(forName: $0) ?? [:] }, to: defaults)
  }
  static func migrate(from old: [String: Any], to defaults: UserDefaults) {
    migrate(sources: [old], to: defaults)
  }
  static func migrate(sources: [[String: Any]], to defaults: UserDefaults) {
    guard boolean(defaults.object(forKey: completion)) != true else { return }
    let keys = ["notchEnabled", "menuEnabled", "appearance", SetupCompletion.key]
    for key in keys where !valid(defaults.object(forKey: key), key: key) {
      if let source = sources.first(where: { valid($0[key], key: key) }), let value = source[key] {
        defaults.set(value, forKey: key)
      }
    }
    // Existing valid preferences suppress first-run setup, but explicit false wins.
    if boolean(defaults.object(forKey: SetupCompletion.key)) == nil {
      let existing = sources.contains(where: SetupCompletion.hasPreferences)
        || SetupCompletion.hasPreferences(defaults.dictionaryRepresentation())
      defaults.set(existing, forKey: SetupCompletion.key)
    }
    // SMAppService registration is never inferred from a preference.
    defaults.set(true, forKey: completion)
  }
}

enum AppAppearance: String, CaseIterable, Identifiable {
  case system, light, dark
  var id: String { rawValue }
  var title: String { rawValue.capitalized }
  var native: NSAppearance? {
    switch self {
    case .system: nil
    case .light: NSAppearance(named: .aqua)
    case .dark: NSAppearance(named: .darkAqua)
    }
  }
}
@MainActor @Observable final class Preferences {
  static let lightMigrationKey = "llumi113LightAppearanceMigrated"
  static let enabledProvidersKey = "enabledProviders"
  private let defaults: UserDefaults
  var changed: () -> Void = {}
  var notchEnabled: Bool {
    didSet {
      defaults.set(notchEnabled, forKey: "notchEnabled")
      changed()
    }
  }
  var menuEnabled: Bool {
    didSet {
      defaults.set(menuEnabled, forKey: "menuEnabled")
      changed()
    }
  }
  var appearance: AppAppearance {
    didSet {
      defaults.set(appearance.rawValue, forKey: "appearance")
      changed()
    }
  }
  var enabledProviders: Set<ProviderID> {
    didSet {
      guard enabledProviders != oldValue else { return }
      defaults.set(ProviderID.allCases.filter(enabledProviders.contains).map(\.rawValue),
        forKey: Self.enabledProvidersKey)
      changed()
    }
  }
  func isEnabled(_ provider: ProviderID) -> Bool { enabledProviders.contains(provider) }
  func setEnabled(_ provider: ProviderID, _ enabled: Bool) {
    if enabled { enabledProviders.insert(provider) } else { enabledProviders.remove(provider) }
  }
  init(defaults: UserDefaults = .standard) {
    self.defaults = defaults
    // Recognize the previous installation before adding new preferences to a fresh one.
    SetupCompletion.recognizeExisting(defaults: defaults, old: [:])
    if BetaPreferences.boolean(defaults.object(forKey: Self.lightMigrationKey)) != true {
      defaults.set(AppAppearance.light.rawValue, forKey: "appearance")
      defaults.set(true, forKey: Self.lightMigrationKey)
    }
    notchEnabled = defaults.object(forKey: "notchEnabled") as? Bool ?? true
    menuEnabled = defaults.object(forKey: "menuEnabled") as? Bool ?? true
    appearance = AppAppearance(rawValue: defaults.string(forKey: "appearance") ?? "") ?? .light
    if let values = defaults.array(forKey: Self.enabledProvidersKey) as? [String] {
      enabledProviders = Set(values.compactMap(ProviderID.init(rawValue:)))
    } else {
      enabledProviders = Set(ProviderID.allCases)
    }
  }
}
@MainActor @Observable final class LoginItem {
  private(set) var enabled = false
  private(set) var message: String?
  init() { synchronize() }
  func synchronize() {
    let status = SMAppService.mainApp.status
    enabled = [.enabled, .requiresApproval].contains(status)
    message =
      status == .requiresApproval ? "Approval is needed in System Settings → Login Items." : nil
  }
  func setEnabled(_ value: Bool) {
    var failed = false
    do {
      if value {
        try SMAppService.mainApp.register()
      } else {
        try SMAppService.mainApp.unregister()
      }
    } catch {
      failed = true
      Diagnostics.shared.record(
        "login-item-error", code: (error as NSError).domain, value: (error as NSError).code)
    }
    synchronize()
    if failed {
      message = "macOS couldn’t change the login item. Try again from the installed app."
    }
  }
}
