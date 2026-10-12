import AppKit
import Darwin

@main struct LlumiMain {
  @MainActor static func main() {
    signal(SIGPIPE, SIG_IGN)
    let work = RuntimePaths.providerWork
    try? FileManager.default.createDirectory(
      at: work, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    // Never retain a Finder/development working directory for incidental framework work.
    _ = FileManager.default.changeCurrentDirectoryPath(work.path)
    // Acquire before AppDelegate/Presentation, diagnostics, or any provider work.
    let lease: [InstanceLease]?
    do { lease = try InstanceLease.acquireProductLeases() } catch { return }
    guard let instance = lease else {
      DistributedNotificationCenter.default().postNotificationName(
        InstanceLease.reopen, object: nil, userInfo: nil, deliverImmediately: true)
      return
    }
    BetaPreferences.migrateIfNeeded()
    let app = NSApplication.shared
    app.setActivationPolicy(
      BetaPreferences.boolean(UserDefaults.standard.object(forKey: Preferences.startInBackgroundKey)) == true
        ? .accessory : .regular)
    let delegate = AppDelegate()
    app.delegate = delegate
    withExtendedLifetime((delegate, instance)) { app.run() }
  }
}
