import Foundation
import Observation

// UI state and session-only dismissals; Sparkle owns the persisted update setting.
@MainActor @Observable final class UpdatePresentation {
  var canCheck = false
  var checking = false
  var automaticDownloads = false {
    didSet {
      if automaticDownloads != oldValue { automaticDownloadsChanged(automaticDownloads) }
    }
  }
  var automaticChecks = true {
    didSet {
      if automaticChecks != oldValue { automaticChecksChanged(automaticChecks) }
    }
  }
  private(set) var availableVersion: String?
  var checkAction: () -> Void = {}
  var automaticChecksChanged: (Bool) -> Void = { _ in }
  var automaticDownloadsChanged: (Bool) -> Void = { _ in }
  var dismissAction: () -> Void = {}
  private(set) var readyVersion: String?
  private(set) var readyNotesURL: URL?
  private(set) var completedUpdate: UpdateCompletionNotice?
  private(set) var promptVisible = false
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
    readyVersion = validated == nil ? "" : version
    readyNotesURL = validated == notesURL ? validated : nil
    clearAvailableUpdate()
    showReadyPrompt()
  }
  func showReadyPrompt() {
    guard readyVersion != nil, completedUpdate == nil else { return }
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
    readyVersion = nil
    readyNotesURL = nil
    if completedUpdate == nil { dismissPrompt() }
  }
  func restartPreparedUpdate() {
    guard readyVersion != nil else { return }
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
