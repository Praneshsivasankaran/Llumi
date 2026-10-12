import AppKit
import Observation
import UserNotifications

enum UsageNotificationPermission: String, Sendable {
  case unknown, allowed, denied, unavailable
  var message: String {
    switch self {
    case .unknown: "Permission will be requested when you enable alerts."
    case .allowed: "Notifications are allowed."
    case .denied: "Notifications are disabled in System Settings."
    case .unavailable: "Notification permission could not be checked."
    }
  }
}

@MainActor protocol UsageNotificationDelivery {
  func permission() async -> UsageNotificationPermission
  func requestPermission() async -> UsageNotificationPermission
  func send(id: String, title: String, body: String) async throws
}

@MainActor final class NativeUsageNotificationDelivery: NSObject, UsageNotificationDelivery,
  UNUserNotificationCenterDelegate {
  // Delayed creation keeps hostless tests and disabled alerts out of the native service.
  private var installedDelegate = false
  private var center: UNUserNotificationCenter {
    let center = UNUserNotificationCenter.current()
    if !installedDelegate { center.delegate = self; installedDelegate = true }
    return center
  }
  var openAction: () -> Void = {}
  func permission() async -> UsageNotificationPermission {
    let settings = await center.notificationSettings()
    switch settings.authorizationStatus {
    case .authorized, .provisional, .ephemeral:
      return settings.alertSetting == .disabled ? .denied : .allowed
    case .denied: return .denied
    case .notDetermined: return .unknown
    @unknown default: return .unavailable
    }
  }
  func requestPermission() async -> UsageNotificationPermission {
    do { _ = try await center.requestAuthorization(options: [.alert, .sound]) }
    catch { return .unavailable }
    return await permission()
  }
  func send(id: String, title: String, body: String) async throws {
    let content = UNMutableNotificationContent()
    content.title = title
    content.body = body
    content.sound = .default
    try await center.add(UNNotificationRequest(identifier: id, content: content, trigger: nil))
  }
  nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
    willPresent notification: UNNotification,
    withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
    completionHandler([.banner, .sound, .list])
  }
  nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
    didReceive response: UNNotificationResponse,
    withCompletionHandler completionHandler: @escaping () -> Void) {
    Task { @MainActor [weak self] in self?.openAction() }
    completionHandler()
  }
}

@MainActor @Observable final class UsageAlertController {
  private let defaults: UserDefaults
  private let evaluator: UsageAlertEvaluator
  private let delivery: any UsageNotificationDelivery
  private var snapshots: [ProviderID: UsageSnapshot] = [:]
  private var enabledProviders: Set<ProviderID> = []
  private var revision = 0
  private var work: Task<Void, Never>?
  private var deliveryWork: Task<Void, Never>?
  private var deliveryRevision = 0
  private var pendingNotices: [PendingNotice] = []
  private var sending: PendingNotice?
  private var testing: Task<Void, Never>?
  private var permissionRefresh: Task<Void, Never>?
  private var permissionRequestPending = false
  private var stopped = false
  private struct PendingNotice {
    let notice: UsageAlertNotice
    let binding: String
  }
  private enum DeliveryReadiness: Equatable { case ready, checking, invalid }
  private(set) var permission: UsageNotificationPermission = .unknown
  private(set) var message: String?
  var openAction: () -> Void = {} {
    didSet { (delivery as? NativeUsageNotificationDelivery)?.openAction = openAction }
  }
  private var storedSettings: UsageAlertSettings
  var settings: UsageAlertSettings {
    get { storedSettings }
    set {
      let valid = newValue.sanitized()
      let optedIn = valid.enabled && !storedSettings.enabled
      storedSettings = valid
      UsageAlertPersistence.saveSettings(valid, defaults: defaults)
      if optedIn { permissionRequestPending = true }
      if !settings.enabled { permissionRequestPending = false }
      schedule()
    }
  }
  init(defaults: UserDefaults = .standard, delivery: (any UsageNotificationDelivery)? = nil) {
    self.defaults = defaults
    evaluator = UsageAlertEvaluator(defaults: defaults)
    self.delivery = delivery ?? NativeUsageNotificationDelivery()
    storedSettings = UsageAlertPersistence.loadSettings(defaults: defaults)
  }
  func update(snapshots: [ProviderID: UsageSnapshot], enabledProviders: Set<ProviderID>) {
    self.snapshots = snapshots
    self.enabledProviders = enabledProviders
    schedule()
  }
  func refreshPermission() {
    guard !stopped else { return }
    permissionRefresh?.cancel()
    permissionRefresh = Task { [weak self] in
      guard let self else { return }
      let permission = await self.delivery.permission()
      guard !Task.isCancelled, !self.stopped else { return }
      self.permission = permission
    }
  }
  private func schedule() {
    guard !stopped else { return }
    revision += 1
    let currentRevision = revision
    work?.cancel()
    // Let the evaluator observe disabled state; disabling never erases receipts.
    guard settings.enabled else {
      cancelDelivery()
      _ = evaluator.evaluate(snapshots: snapshots, settings: settings,
        enabledProviders: enabledProviders)
      return
    }
    pendingNotices.removeAll { readiness($0) == .invalid }
    if let sending, readiness(sending) == .invalid { cancelDelivery(clearPending: false) }
    work = Task { [weak self] in
      guard let self else { return }
      var permission = await self.delivery.permission()
      guard !Task.isCancelled, currentRevision == self.revision, self.settings.enabled else { return }
      if self.permissionRequestPending, permission == .unknown {
        self.permissionRequestPending = false
        permission = await self.delivery.requestPermission()
      }
      guard !Task.isCancelled, currentRevision == self.revision else { return }
      self.permission = permission
      guard permission == .allowed else { self.cancelDelivery(); return }
      let notices = self.evaluator.evaluate(snapshots: self.snapshots, settings: self.settings,
        enabledProviders: self.enabledProviders)
      self.message = self.evaluator.statusMessage
      self.enqueue(notices)
      self.deliverPendingNotices()
    }
  }
  private func enqueue(_ notices: [UsageAlertNotice]) {
    for notice in notices {
      guard let snapshot = snapshots[notice.provider], let reading = snapshot.reading else { continue }
      guard snapshot.consumerWindows.contains(notice.sourceWindow) else { continue }
      guard pendingNotices.count < UsageAlertEvaluator.maximumRecords else {
        message = "Some alerts could not be queued. Try a test notification."
        continue
      }
      pendingNotices.append(PendingNotice(notice: notice, binding: reading.binding))
    }
  }
  private func readiness(_ pending: PendingNotice, at now: Date = Date()) -> DeliveryReadiness {
    let provider = pending.notice.provider
    let control = settings.provider(provider)
    let origin = pending.notice.sourceWindow
    guard !stopped, settings.enabled, enabledProviders.contains(provider), control.enabled,
      control.thresholds.contains(where: { $0.enabled && $0.percent == pending.notice.thresholdPercent }),
      let snapshot = snapshots[provider], snapshot.provider == provider,
      snapshot.state == .live, snapshot.authenticationVerified, snapshot.failure == nil,
      let reading = snapshot.reading, reading.availability == .reported,
      reading.binding == pending.binding,
      (-5...UsageAlertEvaluator.maximumReadingAge).contains(now.timeIntervalSince(reading.date)),
      pending.notice.reset > now,
      snapshot.consumerWindows.contains(where: { current in
        current.bucket == origin.bucket && sameWindowID(current, origin, provider: provider)
          && current.scope == origin.scope && current.durationMinutes == origin.durationMinutes
          && current.reset == origin.reset
          && current.used.map { $0.isFinite && (Double(pending.notice.thresholdPercent)...100).contains($0) } == true
      })
    else { return .invalid }
    return snapshot.refreshStatus.checking ? .checking : .ready
  }
  private func sameWindowID(_ lhs: UsageWindow, _ rhs: UsageWindow, provider: ProviderID) -> Bool {
    if lhs.id == rhs.id { return true }
    guard provider == .codex, lhs.bucket == rhs.bucket else { return false }
    let bucket = lhs.bucket.replacingOccurrences(of: "%", with: "%25")
      .replacingOccurrences(of: ":", with: "%3A")
    return ["primary", "secondary"].contains { slot in
      let aliases = [bucket + ":" + slot, bucket + ":legacy:" + slot]
      return aliases.contains(lhs.id) && aliases.contains(rhs.id)
    }
  }
  private func deliverPendingNotices() {
    guard !stopped, settings.enabled, permission == .allowed, deliveryWork == nil,
      !pendingNotices.isEmpty else { return }
    let currentRevision = deliveryRevision
    deliveryWork = Task { [weak self] in
      guard let self else { return }
      defer {
        if currentRevision == self.deliveryRevision { self.deliveryWork = nil; self.sending = nil }
      }
      while !Task.isCancelled, currentRevision == self.deliveryRevision, !self.stopped {
        self.pendingNotices.removeAll { self.readiness($0) == .invalid }
        guard self.permission == .allowed,
          let index = self.pendingNotices.firstIndex(where: { self.readiness($0) == .ready })
        else { return }
        let pending = self.pendingNotices.remove(at: index)
        self.sending = pending
        do {
          try await self.delivery.send(id: pending.notice.id,
            title: pending.notice.title, body: pending.notice.body)
        } catch {
          guard !Task.isCancelled, currentRevision == self.deliveryRevision else { return }
          self.message = "A notification could not be delivered. Try a test notification."
        }
        guard !Task.isCancelled, currentRevision == self.deliveryRevision else { return }
        self.sending = nil
      }
    }
  }
  private func cancelDelivery(clearPending: Bool = true) {
    deliveryRevision += 1
    deliveryWork?.cancel()
    deliveryWork = nil
    sending = nil
    if clearPending { pendingNotices.removeAll() }
  }
  func sendTestNotification() {
    guard !stopped else { return }
    testing?.cancel()
    testing = Task { [weak self] in
      guard let self else { return }
      var permission = await self.delivery.permission()
      guard !Task.isCancelled, !self.stopped else { return }
      if permission == .unknown { permission = await self.delivery.requestPermission() }
      guard !Task.isCancelled, !self.stopped else { return }
      self.permission = permission
      guard permission == .allowed else { return }
      do {
        try await self.delivery.send(id: "llumi-test-" + UUID().uuidString,
          title: "Llumi test notification", body: "Usage alerts are ready. Alerts warn; they do not pause your tools.")
        self.message = "Test notification sent."
      } catch { self.message = "The test notification could not be delivered." }
    }
  }
  func openNotificationSettings() {
    if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") {
      NSWorkspace.shared.open(url)
    }
  }
  func stop() {
    stopped = true; work?.cancel(); testing?.cancel(); permissionRefresh?.cancel(); revision += 1
    cancelDelivery()
  }
  func waitForIdle() async { await work?.value; await deliveryWork?.value; await testing?.value }
}
