import AppKit
import Foundation
import SwiftUI
import Sparkle
import XCTest

@MainActor final class NativeUpdatePresentationTests: XCTestCase {
  func testLaterDefersWithoutRestartAndCheckingReopensReadyPrompt() {
    let updates = UpdatePresentation()
    var restarts = 0
    var shown = 0
    updates.restartAction = { restarts += 1 }
    updates.showPromptAction = { shown += 1 }
    updates.preparedUpdate(version: "1.1.4", notesURL: URL(string: "https://tryllumi.com/releases/macos/1.1.4/"))
    XCTAssertTrue(updates.promptVisible)
    updates.dismissPrompt()
    XCTAssertEqual(restarts, 0)
    XCTAssertFalse(updates.promptVisible)
    XCTAssertEqual(updates.readyVersion, "1.1.4")
    updates.showReadyPrompt()
    XCTAssertEqual(shown, 2)
    updates.restartPreparedUpdate()
    XCTAssertEqual(restarts, 1)
    XCTAssertFalse(updates.promptVisible)
    XCTAssertTrue(updates.restartRequested)
    updates.showReadyPrompt()
    updates.restartPreparedUpdate()
    XCTAssertFalse(updates.promptVisible)
    XCTAssertEqual(restarts, 1)
    updates.clearPreparedUpdate()
    XCTAssertFalse(updates.restartRequested)
  }
  func testOnlyCompletionDismissalAcknowledgesInstalledVersion() throws {
    let updates = UpdatePresentation()
    var acknowledgments = 0
    updates.acknowledgeCompletionAction = { acknowledgments += 1 }
    updates.preparedUpdate(version: "1.1.4", notesURL: nil)
    updates.dismissPrompt()
    XCTAssertEqual(acknowledgments, 0)
    let release = try XCTUnwrap(UpdateReleaseIdentity(version: "1.1.4", build: "4"))
    updates.showCompletion(UpdateCompletionNotice(release: release))
    XCTAssertEqual(updates.completedUpdate?.version, "1.1.4")
    updates.dismissPrompt()
    updates.dismissPrompt()
    XCTAssertEqual(acknowledgments, 1)
    XCTAssertNil(updates.completedUpdate)
  }
  func testFailedPreparedUpdateClearsPromptAndCannotRestart() {
    let updates = UpdatePresentation()
    var restarts = 0
    updates.restartAction = { restarts += 1 }
    updates.preparedUpdate(version: "1.1.4", notesURL: nil)
    updates.clearPreparedUpdate()
    updates.restartPreparedUpdate()
    XCTAssertNil(updates.readyVersion)
    XCTAssertFalse(updates.promptVisible)
    XCTAssertEqual(restarts, 0)
  }
  func testCombinedSwitchChangesBothPreferencesOnceAndDoesNotRestart() {
    let updates = UpdatePresentation()
    let settings = FakeAutomaticUpdateSettings(checks: true, downloads: false)
    CombinedAutomaticUpdates.adoptExistingPreference(settings)
    updates.automaticChecks = settings.automaticallyChecksForUpdates
    updates.automaticDownloads = settings.automaticallyDownloadsUpdates
    XCTAssertFalse(updates.automaticUpdates)
    var changes: [Bool] = []
    var restarts = 0
    updates.restartAction = { restarts += 1 }
    updates.automaticUpdatesChanged = { enabled in
      changes.append(enabled)
      CombinedAutomaticUpdates.setEnabled(enabled, settings: settings)
      updates.automaticChecks = settings.automaticallyChecksForUpdates
      updates.automaticDownloads = settings.automaticallyDownloadsUpdates
    }
    updates.automaticUpdates = true
    XCTAssertTrue(settings.automaticallyChecksForUpdates)
    XCTAssertTrue(settings.automaticallyDownloadsUpdates)
    updates.automaticUpdates = true
    updates.automaticUpdates = false
    XCTAssertFalse(settings.automaticallyChecksForUpdates)
    XCTAssertFalse(settings.automaticallyDownloadsUpdates)
    XCTAssertEqual(changes, [true, false])
    XCTAssertEqual(restarts, 0)
  }
  func testCombinedPreferencePreservesDownloadChoiceAcrossAllLegacyStates() {
    for checks in [false, true] {
      for downloads in [false, true] {
        let settings = FakeAutomaticUpdateSettings(checks: checks, downloads: downloads)
        CombinedAutomaticUpdates.adoptExistingPreference(settings)
        XCTAssertEqual(settings.automaticallyChecksForUpdates, downloads)
        XCTAssertEqual(settings.automaticallyDownloadsUpdates, downloads)
        CombinedAutomaticUpdates.adoptExistingPreference(settings)
        XCTAssertEqual(settings.automaticallyChecksForUpdates, downloads)
      }
    }
  }
  func testDisallowedAutomaticDownloadsKeepsManualMode() {
    let settings = FakeAutomaticUpdateSettings(checks: false, downloads: false)
    settings.downloadsAllowed = false
    CombinedAutomaticUpdates.setEnabled(true, settings: settings)
    XCTAssertFalse(settings.automaticallyChecksForUpdates)
    XCTAssertFalse(settings.automaticallyDownloadsUpdates)
  }
  func testManualDownloadNeverBypassesRestartOrUnverifiedAndSpecialUpdates() {
    func action(_ stage: SPUUserUpdateStage = .notDownloaded,
      initiated: Bool = true, information: Bool = false, major: Bool = false,
      type: String = "application", signing: SPUAppcastSigningValidationStatus = .succeeded) -> ManualUpdateAction {
      ManualUpdateDownloadPolicy.action(userInitiated: initiated, stage: stage,
        informationOnly: information, majorUpgrade: major, installationType: type, signing: signing)
    }
    XCTAssertEqual(action(), .download)
    XCTAssertEqual(action(.downloaded), .download)
    XCTAssertEqual(action(.installing), .confirmRestart)
    XCTAssertEqual(action(SPUUserUpdateStage(rawValue: 999)!), .standardReview)
    XCTAssertEqual(action(initiated: false), .standardReview)
    XCTAssertEqual(action(information: true), .standardReview)
    XCTAssertEqual(action(major: true), .standardReview)
    XCTAssertEqual(action(type: "package"), .standardReview)
    XCTAssertEqual(action(type: "future-format"), .standardReview)
    XCTAssertEqual(action(signing: .failed), .standardReview)
    XCTAssertEqual(action(signing: .skipped), .standardReview)
  }
  func testMalformedResumedMetadataCannotEnterNativeLabelsOrLinks() {
    let updates = UpdatePresentation()
    updates.showAvailableUpdate("1.1.3\nprivate-field")
    XCTAssertNil(updates.availableVersion)
    updates.preparedUpdate(version: "1.1.3/private-field", notesURL: URL(string: "https://example.invalid/"))
    XCTAssertEqual(updates.readyVersion, "")
    XCTAssertNil(updates.readyNotesURL)
    XCTAssertTrue(updates.promptVisible)
    updates.preparedUpdate(version: "1.1.4", notesURL: URL(string: "https://example.invalid/"))
    XCTAssertEqual(updates.readyVersion, "1.1.4")
    XCTAssertNil(updates.readyNotesURL)
  }

  // Fixture rendering never starts Sparkle, providers or the application, and
  // never writes the production preference domain or an installation receipt.
  func testIsolatedNativeUpdateScreens() async throws {
    let directory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
      .appendingPathComponent("Llumi113Review/NativeUpdateScreens", isDirectory: true)
    guard FileManager.default.fileExists(atPath: directory.appendingPathComponent("capture-request").path)
    else { throw XCTSkip("Native update fixture renders require local review opt-in.") }
    let updates = UpdatePresentation()
    updates.preparedUpdate(version: "1.1.3", notesURL: UpdateCompletionNotice.releaseNotesURL(version: "1.1.3"))
    for appearance in [NSAppearance.Name.aqua, .darkAqua] {
      try await capture(UpdatePromptView(updates: updates), appearance: appearance,
        name: "restart-ready-" + (appearance == .aqua ? "light" : "dark"), directory: directory)
    }
    let release = try XCTUnwrap(UpdateReleaseIdentity(version: "1.1.3", build: "3"))
    updates.showCompletion(UpdateCompletionNotice(release: release))
    for appearance in [NSAppearance.Name.aqua, .darkAqua] {
      try await capture(UpdatePromptView(updates: updates), appearance: appearance,
        name: "update-complete-" + (appearance == .aqua ? "light" : "dark"), directory: directory)
    }
  }
  private func capture<V: View>(_ view: V, appearance: NSAppearance.Name,
    name: String, directory: URL) async throws {
    _ = NSApplication.shared
    let host = NSHostingView(rootView: view.preferredColorScheme(appearance == .aqua ? .light : .dark))
    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 536, height: 240),
      styleMask: [.titled], backing: .buffered, defer: false)
    window.appearance = NSAppearance(named: appearance)
    window.isReleasedWhenClosed = false
    window.contentView = host
    host.frame = NSRect(x: 0, y: 0, width: 536, height: 240)
    defer { window.close() }
    window.layoutIfNeeded()
    host.layoutSubtreeIfNeeded()
    try await Task.sleep(for: .milliseconds(250))
    host.layoutSubtreeIfNeeded()
    let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
    host.cacheDisplay(in: host.bounds, to: bitmap)
    let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    try png.write(to: directory.appendingPathComponent(name + ".png"), options: .atomic)
    window.orderOut(nil)
  }
}

@MainActor private final class FakeAutomaticUpdateSettings: AutomaticUpdateSettings {
  var automaticallyChecksForUpdates: Bool
  private var downloads: Bool
  var downloadsAllowed = true
  var automaticallyDownloadsUpdates: Bool {
    get { downloadsAllowed && downloads }
    set { downloads = newValue }
  }
  init(checks: Bool, downloads: Bool) {
    automaticallyChecksForUpdates = checks
    self.downloads = downloads
  }
}
