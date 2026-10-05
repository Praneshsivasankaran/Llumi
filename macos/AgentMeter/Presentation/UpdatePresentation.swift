import Foundation
import Observation

// UI state and session-only dismissals; Sparkle owns the persisted update setting.
@MainActor @Observable final class UpdatePresentation {
  var canCheck = false
  var checking = false
  var automaticChecks = true {
    didSet {
      if automaticChecks != oldValue { automaticChecksChanged(automaticChecks) }
    }
  }
  private(set) var availableVersion: String?
  var checkAction: () -> Void = {}
  var automaticChecksChanged: (Bool) -> Void = { _ in }
  var dismissAction: () -> Void = {}
  private var dismissedVersions: Set<String> = []
  private var nativeUIVisible = false

  init() { dismissAction = { [weak self] in self?.dismiss() } }
  func showAvailableUpdate(_ version: String) {
    guard automaticChecks, !nativeUIVisible, !dismissedVersions.contains(version) else { return }
    availableVersion = version
  }
  func clearAvailableUpdate() { availableVersion = nil }
  func dismiss() {
    if let availableVersion { dismissedVersions.insert(availableVersion) }
    availableVersion = nil
  }
  func nativePresentationBegan() {
    nativeUIVisible = true
    availableVersion = nil
  }
  func nativePresentationEnded() { nativeUIVisible = false }
}

enum AutomaticUpdatesMigration {
  static let key = "llumi113AutomaticUpdatesMigrated"
  // Called once before Sparkle starts, including for an existing explicit opt-out.
  static func apply(defaults: UserDefaults, enable: () -> Void) {
    guard BetaPreferences.boolean(defaults.object(forKey: key)) != true else { return }
    enable()
    defaults.set(true, forKey: key)
  }
}
