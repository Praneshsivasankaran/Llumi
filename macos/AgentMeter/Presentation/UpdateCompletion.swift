import CryptoKit
import Foundation

// Bundle identity, not an appcast URL or a setup preference, establishes which
// version is actually running. Current releases use a numeric marketing version
// and a monotonically increasing integer build.
struct UpdateReleaseIdentity: Equatable, Sendable {
  let version: String
  let build: String
  private let components: [Int]
  private let buildNumber: Int

  init?(version: String?, build: String?) {
    guard let version, let build, let parts = Self.versionComponents(version), build.count <= 18,
      build.range(of: #"^[1-9][0-9]*$"#, options: .regularExpression) != nil,
      let buildNumber = Int(build)
    else { return nil }
    self.version = version
    self.build = build
    components = parts
    self.buildNumber = buildNumber
  }

  fileprivate static func versionComponents(_ version: String) -> [Int]? {
    guard version.count <= 32,
      version.range(of: #"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"#,
        options: .regularExpression) != nil
    else { return nil }
    let parts = version.split(separator: ".").compactMap { Int($0) }
    return parts.count == 3 ? parts : nil
  }

  func isNewer(than previous: Self) -> Bool {
    guard buildNumber > previous.buildNumber else { return false }
    for (current, old) in zip(components, previous.components) {
      if current != old { return current > old }
    }
    return true  // A newer build of the same marketing version is an update.
  }

  fileprivate var propertyList: [String: String] { ["version": version, "build": build] }
  fileprivate init?(propertyList: Any?) {
    guard let values = propertyList as? [String: String],
      Set(values.keys) == Set(["version", "build"])
    else { return nil }
    self.init(version: values["version"], build: values["build"])
  }
}

struct UpdateCompletionNotice: Equatable, Sendable {
  let release: UpdateReleaseIdentity
  var version: String { release.version }
  var build: String { release.build }
  // The version above accepts only canonical ASCII digits and dots. The host,
  // scheme and path prefix are constants; provider-supplied URLs never enter it.
  var releaseNotesURL: URL {
    Self.releaseNotesURL(version: release.version)!
  }
  static func releaseNotesURL(version: String) -> URL? {
    guard UpdateReleaseIdentity.versionComponents(version) != nil else { return nil }
    return URL(string: "https://tryllumi.com/releases/macos/\(version)/")
  }
}

@MainActor final class UpdateCompletionTracker {
  static let pendingKey = "llumiUpdateCompletionPendingV1"
  static let acknowledgedKey = "llumiUpdateCompletionAcknowledgedV1"
  private let defaults: UserDefaults
  private let current: UpdateReleaseIdentity?
  private let pendingStorageKey: String?
  private let acknowledgedStorageKey: String?
  private(set) var completion: UpdateCompletionNotice?

  // A local preview and /Applications may share the bundle identifier and
  // preference domain. Their installation records must remain independent.
  static func persistenceKeys(forHostPath path: String) -> (pending: String, acknowledged: String)? {
    guard path.hasPrefix("/"), path.count <= 4096,
      !path.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
    else { return nil }
    let canonical = URL(fileURLWithPath: path).standardizedFileURL.resolvingSymlinksInPath().path
    let binding = SHA256.hash(data: Data(canonical.utf8)).map { String(format: "%02x", $0) }.joined()
    return (pendingKey + "." + binding, acknowledgedKey + "." + binding)
  }

  private struct InstallationIntent {
    let origin: UpdateReleaseIdentity
    let target: UpdateReleaseIdentity
    var propertyList: [String: Any] {
      ["schema": "1", "origin": origin.propertyList, "target": target.propertyList]
    }
    init(origin: UpdateReleaseIdentity, target: UpdateReleaseIdentity) {
      self.origin = origin
      self.target = target
    }
    init?(propertyList: Any?) {
      guard let values = propertyList as? [String: Any], values["schema"] as? String == "1",
        Set(values.keys) == Set(["schema", "origin", "target"]),
        let origin = UpdateReleaseIdentity(propertyList: values["origin"]),
        let target = UpdateReleaseIdentity(propertyList: values["target"]),
        target.isNewer(than: origin)
      else { return nil }
      self.init(origin: origin, target: target)
    }
  }

  init(
    defaults: UserDefaults = .standard,
    currentVersion: String? = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
    currentBuild: String? = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String,
    hostPath: String = Bundle.main.bundleURL.resolvingSymlinksInPath().path
  ) {
    self.defaults = defaults
    let keys = Self.persistenceKeys(forHostPath: hostPath)
    pendingStorageKey = keys?.pending
    acknowledgedStorageKey = keys?.acknowledged
    current = keys == nil ? nil : UpdateReleaseIdentity(version: currentVersion, build: currentBuild)
    detectInstalledUpdate()
  }

  private func acknowledgedRelease() -> UpdateReleaseIdentity? {
    guard let acknowledgedStorageKey,
      let values = defaults.object(forKey: acknowledgedStorageKey) as? [String: Any],
      values["schema"] as? String == "1", Set(values.keys) == Set(["schema", "release"])
    else { return nil }
    return UpdateReleaseIdentity(propertyList: values["release"])
  }

  private func detectInstalledUpdate() {
    guard let current, let pendingStorageKey, let acknowledgedStorageKey else { return }
    guard let raw = defaults.object(forKey: pendingStorageKey) else { return }
    guard let intent = InstallationIntent(propertyList: raw) else {
      defaults.removeObject(forKey: pendingStorageKey)
      return
    }
    // Malformed acknowledgment metadata cannot manufacture a first completion.
    let acknowledgment = acknowledgedRelease()
    if defaults.object(forKey: acknowledgedStorageKey) != nil && acknowledgment == nil {
      defaults.removeObject(forKey: pendingStorageKey)
      return
    }
    if let acknowledgment, !intent.target.isNewer(than: acknowledgment) {
      defaults.removeObject(forKey: pendingStorageKey)
      return
    }
    if current == intent.origin { return }  // Prepared update / Later: still the old app.
    guard current == intent.target, current.isNewer(than: intent.origin) else {
      defaults.removeObject(forKey: pendingStorageKey)
      return
    }
    completion = UpdateCompletionNotice(release: current)
    // Leave the intent in place until explicit acknowledgment. Closing the app
    // before dismissing the notice must not lose it on the next launch.
  }

  // Call only after a verified update is prepared for installation, or from
  // Sparkle's actual installation callback. Checks/download starts do not call it.
  @discardableResult func markInstallationIntent(targetVersion: String?, targetBuild: String?) -> Bool {
    guard completion == nil, let current, let pendingStorageKey, let acknowledgedStorageKey,
      let target = UpdateReleaseIdentity(version: targetVersion, build: targetBuild),
      target.isNewer(than: current)
    else { return false }
    if let acknowledgment = acknowledgedRelease(), !target.isNewer(than: acknowledgment) { return false }
    // A fresh trusted install intent is the only recovery path for corrupted
    // prior metadata. It does not infer that an installation already succeeded.
    if defaults.object(forKey: acknowledgedStorageKey) != nil && acknowledgedRelease() == nil {
      defaults.removeObject(forKey: acknowledgedStorageKey)
    }
    defaults.set(InstallationIntent(origin: current, target: target).propertyList, forKey: pendingStorageKey)
    return true
  }

  // Call for a canceled/failed installation, not a failed feed probe.
  // A late failure from another target cannot erase a newer prepared update or
  // an already installed version's unacknowledged completion notice.
  func failedInstallation(targetVersion: String? = nil, targetBuild: String? = nil) {
    guard completion == nil, let current, let pendingStorageKey,
      let intent = InstallationIntent(propertyList: defaults.object(forKey: pendingStorageKey)),
      intent.origin == current
    else { return }
    if targetVersion != nil || targetBuild != nil {
      guard let target = UpdateReleaseIdentity(version: targetVersion, build: targetBuild),
        target == intent.target
      else { return }
    }
    defaults.removeObject(forKey: pendingStorageKey)
  }

  func acknowledgeCompletion() {
    guard let completion, let pendingStorageKey, let acknowledgedStorageKey else { return }
    if acknowledgedRelease().map({ completion.release.isNewer(than: $0) }) ?? true {
      defaults.set(["schema": "1", "release": completion.release.propertyList], forKey: acknowledgedStorageKey)
    }
    if let intent = InstallationIntent(propertyList: defaults.object(forKey: pendingStorageKey)),
      intent.target == completion.release
    {
      defaults.removeObject(forKey: pendingStorageKey)
    }
    self.completion = nil
  }
}
