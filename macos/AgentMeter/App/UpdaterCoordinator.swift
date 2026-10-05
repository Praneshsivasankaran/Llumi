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
  private lazy var controller = SPUStandardUpdaterController(
    startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)

  init(presentation: UpdatePresentation, defaults: UserDefaults = .standard) {
    self.presentation = presentation
    self.defaults = defaults
    super.init()
    presentation.checkAction = { [weak self] in self?.checkForUpdates() }
    presentation.automaticChecksChanged = { [weak self] enabled in
      guard let self else { return }
      self.controller.updater.automaticallyChecksForUpdates = enabled
      if !enabled {
        self.pendingForegroundProbe = false
        self.presentation.clearAvailableUpdate()
      }
      self.synchronize()
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
    ]
    started = true
    controller.startUpdater()
    synchronize()
  }

  func foregroundOpened() {
    guard started, controller.updater.automaticallyChecksForUpdates else { return }
    guard !probing else { return }
    pendingForegroundProbe = true
    performPendingProbe()
  }

  func checkForUpdates() {
    guard started, controller.updater.canCheckForUpdates else { return }
    pendingForegroundProbe = false
    presentation.nativePresentationBegan()
    controller.updater.checkForUpdates()
    synchronize()
  }

  private func performPendingProbe() {
    let updater = controller.updater
    guard pendingForegroundProbe, started, updater.automaticallyChecksForUpdates,
      !updater.sessionInProgress else { return }
    pendingForegroundProbe = false
    probing = true
    probeVersion = nil
    updater.checkForUpdateInformation()
    synchronize()
  }

  private func synchronize() {
    let updater = controller.updater
    presentation.canCheck = started && updater.canCheckForUpdates
    presentation.checking = probing || (updater.sessionInProgress && !updater.canCheckForUpdates)
    presentation.automaticChecks = updater.automaticallyChecksForUpdates
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
    guard updateCheck == .updatesInBackground else { return }
    // A standard scheduled alert would retain an open session after our banner's
    // Later button. Use Sparkle's signed probing driver for scheduled notices too.
    pendingForegroundProbe = updater.automaticallyChecksForUpdates
    throw NSError(domain: "Llumi.ScheduledUpdateProbe", code: 1,
      userInfo: [NSLocalizedDescriptionKey: "Scheduled update check redirected to a silent probe."])
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
    presentation.nativePresentationEnded()
    synchronize()
    schedulePendingProbe()
  }

  var supportsGentleScheduledUpdateReminders: Bool { true }
  func standardUserDriverShouldHandleShowingScheduledUpdate(
    _ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool
  ) -> Bool { false }
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
