import AppKit
import Sparkle

@MainActor final class UpdaterCoordinator: NSObject, SPUUpdaterDelegate, @preconcurrency SPUStandardUserDriverDelegate {
  private let presentation: UpdatePresentation
  private let defaults: UserDefaults
  private var started = false
  private var startAttempted = false
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
  private lazy var userDriver: LlumiUpdateUserDriver = {
    let driver = LlumiUpdateUserDriver(hostBundle: .main, delegate: self)
    driver.downloadBegan = { [weak self] in self?.presentation.nativePresentationBegan() }
    driver.ready = { [weak self] item, install in self?.prepareUpdate(item, install: install) }
    return driver
  }()
  private lazy var updater = SPUUpdater(hostBundle: .main, applicationBundle: .main,
    userDriver: userDriver, delegate: self)

  init(presentation: UpdatePresentation, defaults: UserDefaults = .standard) {
    self.presentation = presentation
    self.defaults = defaults
    completion = UpdateCompletionTracker(defaults: defaults,
      currentVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "",
      currentBuild: Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "")
    super.init()
    presentation.checkAction = { [weak self] in self?.checkForUpdates() }
    presentation.automaticUpdatesChanged = { [weak self] enabled in
      self?.setAutomaticUpdates(enabled)
    }
    presentation.restartAction = { [weak self] in
      guard let self, let install = self.restartPrepared else { return }
      self.restartPrepared = nil
      self.synchronize()
      install()
    }
    presentation.acknowledgeCompletionAction = { [weak self] in
      guard let self else { return }
      self.completion.acknowledgeCompletion()
      self.synchronize()
      self.foregroundOpened()
    }
  }

  func startIfReady(setupComplete: Bool) {
    guard setupComplete, !startAttempted else { return }
    startAttempted = true
    let updater = self.updater
    AutomaticUpdatesMigration.apply(defaults: defaults) {
      updater.automaticallyChecksForUpdates = true
    }
    // The new combined switch uses the existing download choice, including
    // fresh-install false, rather than turning a checks-only choice into downloads.
    CombinedAutomaticUpdates.adoptExistingPreference(updater)
    observations = [
      updater.observe(\.canCheckForUpdates, options: [.new]) { [weak self] _, _ in
        MainActor.assumeIsolated { self?.synchronize() }
      },
      updater.observe(\.sessionInProgress, options: [.new]) { [weak self] _, _ in
        MainActor.assumeIsolated {
          guard let self else { return }
          self.synchronize()
          if !self.updater.sessionInProgress { self.schedulePendingProbe() }
        }
      },
      updater.observe(\.automaticallyChecksForUpdates, options: [.new]) { [weak self] updater, _ in
        MainActor.assumeIsolated { self?.setAutomaticUpdates(updater.automaticallyChecksForUpdates) }
      },
      updater.observe(\.automaticallyDownloadsUpdates, options: [.new]) { [weak self] updater, _ in
        MainActor.assumeIsolated { self?.setAutomaticUpdates(updater.automaticallyDownloadsUpdates) }
      },
    ]
    do {
      try updater.start()
      started = true
    }
    catch {
      Task { @MainActor in
        let alert = NSAlert()
        alert.messageText = "Unable to check for updates"
        alert.informativeText = "Please try reopening Llumi."
        alert.runModal()
      }
    }
    synchronize()
    // Verified installation evidence remains available even if the update
    // engine cannot start another check in this launch.
    if let notice = completion.completion {
      Task { @MainActor [weak self] in self?.presentation.showCompletion(notice) }
    }
  }

  func foregroundOpened() {
    guard started, updater.automaticallyChecksForUpdates else { return }
    guard !probing, preparedItem == nil, completion.completion == nil else { return }
    guard lastForegroundProbe.map({ Date().timeIntervalSince($0) >= 900 }) ?? true else { return }
    pendingForegroundProbe = true
    performPendingProbe()
  }

  func checkForUpdates() {
    if let notice = completion.completion { presentation.showCompletion(notice); return }
    if preparedItem != nil { presentation.showReadyPrompt(); return }
    guard started, updater.canCheckForUpdates else { return }
    pendingForegroundProbe = false
    presentation.nativePresentationBegan()
    updater.checkForUpdates()
    synchronize()
  }

  private func performPendingProbe() {
    let updater = self.updater
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

  private func setAutomaticUpdates(_ enabled: Bool) {
    guard !synchronizing else { return }
    synchronizing = true
    CombinedAutomaticUpdates.setEnabled(enabled, settings: updater)
    synchronizing = false
    if !updater.automaticallyChecksForUpdates {
      pendingForegroundProbe = false
      presentation.clearAvailableUpdate()
    }
    synchronize()
    if enabled {
      lastForegroundProbe = nil
      foregroundOpened()
    }
  }

  private func synchronize() {
    guard !synchronizing else { return }
    let updater = self.updater
    synchronizing = true
    defer { synchronizing = false }
    presentation.canCheck = started && !presentation.restartRequested && completion.completion == nil && (preparedItem != nil || updater.canCheckForUpdates)
    presentation.checking = presentation.restartRequested || (preparedItem == nil && (probing || (updater.sessionInProgress && !updater.canCheckForUpdates)))
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
    prepareUpdate(item, install: install)
    // Later keeps this process running. Sparkle retains the verified installer
    // and installs on natural quit, as documented by its public delegate API.
    return true
  }
  private func prepareUpdate(_ item: SUAppcastItem, install: @escaping () -> Void) {
    preparedItem = item
    restartPrepared = install
    _ = completion.markInstallationIntent(targetVersion: item.displayVersionString,
      targetBuild: item.versionString)
    presentation.preparedUpdate(version: item.displayVersionString,
      notesURL: UpdateCompletionNotice.releaseNotesURL(version: item.displayVersionString))
    synchronize()
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
  ) -> Bool { updater.automaticallyDownloadsUpdates }
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

extension SPUUpdater: AutomaticUpdateSettings {}

// Preserve Sparkle's checking/progress/cancellation/error/installation UI. The
// only automatic choice here starts an ordinary manual download; restarting
// always goes through Llumi's explicit ready prompt.
@MainActor final class LlumiUpdateUserDriver: SPUStandardUserDriver {
  var downloadBegan: () -> Void = {}
  var ready: (SUAppcastItem, @escaping () -> Void) -> Void = { _, _ in }
  private var manualItem: SUAppcastItem?


  override func showUpdateFound(with appcastItem: SUAppcastItem, state: SPUUserUpdateState,
    reply: @escaping (SPUUserUpdateChoice) -> Void) {
    let action = ManualUpdateDownloadPolicy.action(userInitiated: state.userInitiated, stage: state.stage,
      informationOnly: appcastItem.isInformationOnlyUpdate, majorUpgrade: appcastItem.isMajorUpgrade,
      installationType: appcastItem.installationType, signing: appcastItem.signingValidationStatus)
    switch action {
    case .download:
      super.dismissUpdateInstallation()
      manualItem = appcastItem
      downloadBegan()
      reply(.install)
    case .confirmRestart:
      super.dismissUpdateInstallation()
      manualItem = appcastItem
      ready(appcastItem) { reply(.install) }
    case .standardReview:
      manualItem = nil
      super.showUpdateFound(with: appcastItem, state: state, reply: reply)
    }
  }
  override func showReady(toInstallAndRelaunch reply: @escaping (SPUUserUpdateChoice) -> Void) {
    guard let manualItem else { super.showReady(toInstallAndRelaunch: reply); return }
    super.dismissUpdateInstallation()
    ready(manualItem) { reply(.install) }
  }
  override func dismissUpdateInstallation() {
    manualItem = nil
    super.dismissUpdateInstallation()
  }
}
