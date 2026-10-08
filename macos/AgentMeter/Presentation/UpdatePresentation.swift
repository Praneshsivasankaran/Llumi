import Foundation
import Observation
import Sparkle

// UI state and session-only dismissals; Sparkle owns the persisted update setting.
@MainActor @Observable final class UpdatePresentation {
  var canCheck = false
  var checking = false
  var automaticDownloads = false
  var automaticChecks = true
  var automaticUpdates: Bool {
    get { automaticChecks && automaticDownloads }
    set {
      if newValue != automaticUpdates { automaticUpdatesChanged(newValue) }
    }
  }
  private(set) var availableVersion: String?
  var checkAction: () -> Void = {}
  var automaticUpdatesChanged: (Bool) -> Void = { _ in }
  var dismissAction: () -> Void = {}
  private(set) var readyVersion: String?
  private(set) var readyNotesURL: URL?
  private(set) var completedUpdate: UpdateCompletionNotice?
  private(set) var promptVisible = false
  private(set) var restartRequested = false
  var showPromptAction: () -> Void = {}
  var hidePromptAction: () -> Void = {}
  var restartAction: () -> Void = {}
  var acknowledgeCompletionAction: () -> Void = {}
  private var dismissedVersions: Set<String> = []
  private var nativeUIVisible = false

  init() { dismissAction = { [weak self] in self?.dismiss() } }
  func showAvailableUpdate(_ version: String) {
    guard UpdateCompletionNotice.releaseNotesURL(version: version) != nil,
      automaticChecks, !nativeUIVisible, !dismissedVersions.contains(version) else { return }
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
  func preparedUpdate(version: String, notesURL: URL?) {
    // Sparkle may resume an old download without its new-item validation hook.
    // Invalid metadata never becomes a displayed version or an external URL.
    let validated = UpdateCompletionNotice.releaseNotesURL(version: version)
    restartRequested = false
    readyVersion = validated == nil ? "" : version
    readyNotesURL = validated == notesURL ? validated : nil
    clearAvailableUpdate()
    showReadyPrompt()
  }
  func showReadyPrompt() {
    guard readyVersion != nil, completedUpdate == nil, !restartRequested else { return }
    promptVisible = true
    showPromptAction()
  }
  func showCompletion(_ notice: UpdateCompletionNotice) {
    completedUpdate = notice
    promptVisible = true
    showPromptAction()
  }
  func dismissPrompt() {
    if completedUpdate != nil {
      acknowledgeCompletionAction()
      completedUpdate = nil
    }
    promptVisible = false
    hidePromptAction()
  }
  func clearPreparedUpdate() {
    restartRequested = false
    readyVersion = nil
    readyNotesURL = nil
    if completedUpdate == nil { dismissPrompt() }
  }
  func restartPreparedUpdate() {
    guard readyVersion != nil, !restartRequested else { return }
    restartRequested = true
    promptVisible = false
    hidePromptAction()
    restartAction()
  }
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

// Downloading is the legacy authority when adopting the combined control.
// Checking-only users are never silently enrolled in automatic downloads.
@MainActor protocol AutomaticUpdateSettings: AnyObject {
  var automaticallyChecksForUpdates: Bool { get set }
  var automaticallyDownloadsUpdates: Bool { get set }
}
@MainActor enum CombinedAutomaticUpdates {
  static func adoptExistingPreference(_ settings: any AutomaticUpdateSettings) {
    setEnabled(settings.automaticallyDownloadsUpdates, settings: settings)
  }
  static func setEnabled(_ enabled: Bool, settings: any AutomaticUpdateSettings) {
    settings.automaticallyDownloadsUpdates = enabled
    // If Sparkle's host policy disallows automatic downloading, stay manual.
    settings.automaticallyChecksForUpdates = settings.automaticallyDownloadsUpdates
  }
}

enum ManualUpdateAction: Equatable { case download, confirmRestart, standardReview }
@MainActor enum ManualUpdateDownloadPolicy {
  static func action(userInitiated: Bool, stage: SPUUserUpdateStage,
    informationOnly: Bool, majorUpgrade: Bool, installationType: String,
    signing: SPUAppcastSigningValidationStatus) -> ManualUpdateAction {
    guard userInitiated, !informationOnly, !majorUpgrade,
      installationType == "application", signing == .succeeded else { return .standardReview }
    switch stage {
    case .notDownloaded, .downloaded: return .download
    case .installing: return .confirmRestart
    @unknown default: return .standardReview
    }
  }

}
