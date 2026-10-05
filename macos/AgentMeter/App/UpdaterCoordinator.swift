import AppKit
import Sparkle

@MainActor final class UpdaterCoordinator: NSObject, SPUUpdaterDelegate, @preconcurrency SPUStandardUserDriverDelegate {
  private let presentation: UpdatePresentation
  private let defaults: UserDefaults
  private var started = false
  private var pendingForegroundProbe = false
  private var probing = false
  private var probeVersion: String?
  private var observations: [NSKeyValueObservation] = []
  private var synchronizing = false
  private var lastForegroundProbe: Date?
  private var preparedItem: SUAppcastItem?
  private var installingItem: SUAppcastItem?
  private var restartPrepared: (() -> Void)?
  private let completion: UpdateCompletionTracker
  private lazy var controller = SPUStandardUpdaterController(
    startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)

  init(presentation: UpdatePresentation, defaults: UserDefaults = .standard) {
    self.presentation = presentation
    self.defaults = defaults
    completion = UpdateCompletionTracker(defaults: defaults,
      currentVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "",
      currentBuild: Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "")
    super.init()
    presentation.checkAction = { [weak self] in self?.checkForUpdates() }
    presentation.automaticChecksChanged = { [weak self] enabled in
      guard let self, !self.synchronizing else { return }
      self.controller.updater.automaticallyChecksForUpdates = enabled
      if !enabled { self.controller.updater.automaticallyDownloadsUpdates = false }
      if !enabled {
        self.pendingForegroundProbe = false
        self.presentation.clearAvailableUpdate()
      }
      self.synchronize()
    }
    presentation.automaticDownloadsChanged = { [weak self] enabled in
      guard let self, !self.synchronizing else { return }
      let updater = self.controller.updater
      if enabled { updater.automaticallyChecksForUpdates = true }
      updater.automaticallyDownloadsUpdates = enabled
      self.synchronize()
      if enabled {
        self.lastForegroundProbe = nil
        self.foregroundOpened()
      }
    }
    presentation.restartAction = { [weak self] in self?.restartPrepared?() }
    presentation.acknowledgeCompletionAction = { [weak self] in
      guard let self else { return }
      self.completion.acknowledgeCompletion()
      self.synchronize()
      self.foregroundOpened()
    }
  }

  func startIfReady(setupComplete: Bool) {
    guard setupComplete, !started else { return }
    let updater = controller.updater
    AutomaticUpdatesMigration.apply(defaults: defaults) {
      updater.automaticallyChecksForUpdates = true
    }
    observations = [
      updater.observe(\.canCheckForUpdates, options: [.new]) { [weak self] _, _ in
        MainActor.assumeIsolated { self?.synchronize() }
      },
      updater.observe(\.sessionInProgress, options: [.new]) { [weak self] _, _ in
        MainActor.assumeIsolated {
          guard let self else { return }
          self.synchronize()
          if !self.controller.updater.sessionInProgress { self.schedulePendingProbe() }
        }
      },
      updater.observe(\.automaticallyChecksForUpdates, options: [.new]) { [weak self] _, _ in
        MainActor.assumeIsolated { self?.synchronize() }
      },
      updater.observe(\.automaticallyDownloadsUpdates, options: [.new]) { [weak self] _, _ in
        MainActor.assumeIsolated { self?.synchronize() }
      },
    ]
    started = true
    controller.startUpdater()
    synchronize()
    if let notice = completion.completion {
      Task { @MainActor [weak self] in self?.presentation.showCompletion(notice) }
    }
  }

  func foregroundOpened() {
    guard started, controller.updater.automaticallyChecksForUpdates else { return }
    guard !probing, preparedItem == nil, completion.completion == nil else { return }
    guard lastForegroundProbe.map({ Date().timeIntervalSince($0) >= 900 }) ?? true else { return }
    pendingForegroundProbe = true
    performPendingProbe()
  }

  func checkForUpdates() {
    if let notice = completion.completion { presentation.showCompletion(notice); return }
    if preparedItem != nil { presentation.showReadyPrompt(); return }
    guard started, controller.updater.canCheckForUpdates else { return }
    pendingForegroundProbe = false
    presentation.nativePresentationBegan()
    controller.updater.checkForUpdates()
    synchronize()
  }

  private func performPendingProbe() {
    let updater = controller.updater
    guard pendingForegroundProbe, started, updater.automaticallyChecksForUpdates,
      completion.completion == nil, !updater.sessionInProgress else { return }
    pendingForegroundProbe = false
    lastForegroundProbe = Date()
    probeVersion = nil
    if updater.automaticallyDownloadsUpdates {
      updater.checkForUpdatesInBackground()
    } else {
      probing = true
      updater.checkForUpdateInformation()
    }
    synchronize()
  }

  private func synchronize() {
    let updater = controller.updater
    synchronizing = true
    defer { synchronizing = false }
    presentation.canCheck = started && completion.completion == nil && (preparedItem != nil || updater.canCheckForUpdates)
    presentation.checking = preparedItem == nil && (probing || (updater.sessionInProgress && !updater.canCheckForUpdates))
    presentation.automaticChecks = updater.automaticallyChecksForUpdates
    presentation.automaticDownloads = updater.automaticallyDownloadsUpdates
  }
  private func schedulePendingProbe() {
    guard pendingForegroundProbe else { return }
    // Sparkle may briefly create another session while scheduling its timer.
    // The session observer retries when that scheduling work also finishes.
    Task { @MainActor [weak self] in self?.performPendingProbe() }
  }

  // Do not send inherited system-profile preferences to the update server.
  func allowedSystemProfileKeys(for updater: SPUUpdater) -> [String]? { [] }
  func updater(_ updater: SPUUpdater, mayPerform updateCheck: SPUUpdateCheck) throws {
    guard completion.completion == nil else {
      throw NSError(domain: "Llumi.UpdateAcknowledgmentPending", code: 1,
        userInfo: [NSLocalizedDescriptionKey: "Acknowledge the installed update before starting another update."])
    }
    guard updateCheck == .updatesInBackground, !updater.automaticallyDownloadsUpdates else { return }
    // A standard scheduled alert would retain an open session after our banner's
    // Later button. Use Sparkle's signed probing driver for scheduled notices too.
    pendingForegroundProbe = updater.automaticallyChecksForUpdates
    throw NSError(domain: "Llumi.ScheduledUpdateProbe", code: 1,
      userInfo: [NSLocalizedDescriptionKey: "Scheduled update check redirected to a silent probe."])
  }
  func updater(_ updater: SPUUpdater, shouldProceedWithUpdate item: SUAppcastItem,
    updateCheck: SPUUpdateCheck) throws {
    guard let target = UpdateReleaseIdentity(version: item.displayVersionString, build: item.versionString),
      let current = UpdateReleaseIdentity(
        version: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
        build: Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String),
      target.isNewer(than: current)
    else {
      throw NSError(domain: "Llumi.InvalidUpdateMetadata", code: 1,
        userInfo: [NSLocalizedDescriptionKey: "The update version could not be verified. Check again later."])
    }
  }
  @objc(updater:userDidMakeChoice:forUpdate:state:)
  func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice,
    forUpdate item: SUAppcastItem, state: SPUUserUpdateState) {
    guard choice == .skip else { return }
    completion.failedInstallation(targetVersion: item.displayVersionString, targetBuild: item.versionString)
    if preparedItem?.versionString == item.versionString || installingItem?.versionString == item.versionString {
      cancelPreparedReceipt()
      synchronize()
    }
  }
  func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
    if probing { probeVersion = item.displayVersionString }
  }
  func updaterDidNotFindUpdate(_ updater: SPUUpdater, error: Error) {
    if probing {
      probeVersion = nil
      presentation.clearAvailableUpdate()
    }
  }
  func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?) {
    if updateCheck == .updateInformation {
      probing = false
      if error == nil, let probeVersion { presentation.showAvailableUpdate(probeVersion) }
      probeVersion = nil
    }
    if (preparedItem != nil || installingItem != nil), error != nil {
      cancelPreparedReceipt()
    }
    presentation.nativePresentationEnded()
    synchronize()
    schedulePendingProbe()
  }

  // No remote HTML is rendered inside Llumi. Native prompts link to the exact
  // reviewed HTTPS patch-note route derived from the validated release version.
  func updater(_ updater: SPUUpdater, shouldDownloadReleaseNotesForUpdate item: SUAppcastItem) -> Bool { false }
  func updater(_ updater: SPUUpdater, willInstallUpdate item: SUAppcastItem) {
    installingItem = item
    _ = completion.markInstallationIntent(targetVersion: item.displayVersionString,
      targetBuild: item.versionString)
  }
  func updater(_ updater: SPUUpdater, willInstallUpdateOnQuit item: SUAppcastItem,
    immediateInstallationBlock install: @escaping () -> Void) -> Bool {
    preparedItem = item
    restartPrepared = install
    _ = completion.markInstallationIntent(targetVersion: item.displayVersionString,
      targetBuild: item.versionString)
    presentation.preparedUpdate(version: item.displayVersionString,
      notesURL: UpdateCompletionNotice.releaseNotesURL(version: item.displayVersionString))
    synchronize()
    // Later keeps this process running. Sparkle retains the verified installer
    // and installs on natural quit, as documented by its public delegate API.
    return true
  }
  private func cancelPreparedReceipt() {
    if let item = preparedItem ?? installingItem {
      completion.failedInstallation(targetVersion: item.displayVersionString, targetBuild: item.versionString)
    }
    preparedItem = nil
    installingItem = nil
    restartPrepared = nil
    presentation.clearPreparedUpdate()
  }

  var supportsGentleScheduledUpdateReminders: Bool { true }
  func standardUserDriverShouldHandleShowingScheduledUpdate(
    _ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool
  ) -> Bool { controller.updater.automaticallyDownloadsUpdates }
  func standardUserDriverWillHandleShowingUpdate(
    _ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState
  ) {
    if handleShowingUpdate {
      presentation.nativePresentationBegan()
    } else {
      presentation.showAvailableUpdate(update.displayVersionString)
    }
    synchronize()
  }
  func standardUserDriverDidReceiveUserAttention(forUpdate update: SUAppcastItem) {
    presentation.nativePresentationBegan()
  }
  func standardUserDriverWillFinishUpdateSession() {
    presentation.clearAvailableUpdate()
    presentation.nativePresentationEnded()
  }
}
