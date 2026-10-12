import Foundation
import XCTest

final class BackgroundPolicyTests: XCTestCase {
  func testBackgroundLaunchSuppressesEveryAutomaticFullSurface() {
    for login in [false, true] {
      for setup in [false, true] {
        XCTAssertEqual(BackgroundPolicy.initialSurface(startInBackground: true,
          loginLaunch: login, needsSetup: setup), .none)
      }
    }
  }
  func testForegroundAndLoginLaunchPreserveExistingSetupSemantics() {
    XCTAssertEqual(BackgroundPolicy.initialSurface(startInBackground: false,
      loginLaunch: false, needsSetup: false), .usage)
    XCTAssertEqual(BackgroundPolicy.initialSurface(startInBackground: false,
      loginLaunch: true, needsSetup: false), .none)
    for login in [false, true] {
      XCTAssertEqual(BackgroundPolicy.initialSurface(startInBackground: false,
        loginLaunch: login, needsSetup: true), .setup)
    }
  }
  func testBackgroundRemainsDiscoverableWithoutChangingMenuPreference() {
    XCTAssertTrue(BackgroundPolicy.showsMenu(startInBackground: true, menuEnabled: false))
    XCTAssertTrue(BackgroundPolicy.showsMenu(startInBackground: true, menuEnabled: true))
    XCTAssertFalse(BackgroundPolicy.showsMenu(startInBackground: false, menuEnabled: false))
    XCTAssertTrue(BackgroundPolicy.showsMenu(startInBackground: false, menuEnabled: true))
  }
  func testExplicitWindowRestoresDockAndClosingReturnsToBackground() {
    XCTAssertFalse(BackgroundPolicy.showsDock(startInBackground: true, fullInterfaceVisible: false))
    XCTAssertTrue(BackgroundPolicy.showsDock(startInBackground: true, fullInterfaceVisible: true))
    XCTAssertTrue(BackgroundPolicy.showsDock(startInBackground: false, fullInterfaceVisible: false))
  }
}

@MainActor final class BackgroundPreferenceTests: XCTestCase {
  private func suite(_ body: (UserDefaults) -> Void) {
    let name = "Llumi-background-preferences-" + UUID().uuidString
    let defaults = UserDefaults(suiteName: name)!
    defer { defaults.removePersistentDomain(forName: name) }
    body(defaults)
  }
  func testOptInPersistsIndependentlyOfMenuAndNotch() {
    suite { defaults in
      let preferences = Preferences(defaults: defaults)
      XCTAssertFalse(preferences.startInBackground)
      preferences.menuEnabled = false
      preferences.notchEnabled = false
      preferences.startInBackground = true
      let restarted = Preferences(defaults: defaults)
      XCTAssertTrue(restarted.startInBackground)
      XCTAssertFalse(restarted.menuEnabled)
      XCTAssertFalse(restarted.notchEnabled)
      preferences.startInBackground = false
      XCTAssertFalse(Preferences(defaults: defaults).startInBackground)
    }
  }
  func testInvalidBackgroundValuesDefaultOffWithoutSuppressingFreshSetup() {
    for value in ["true", 1, [true]] as [Any] {
      suite { defaults in
        defaults.set(value, forKey: Preferences.startInBackgroundKey)
        let preferences = Preferences(defaults: defaults)
        XCTAssertFalse(preferences.startInBackground)
        XCTAssertTrue(SetupFlow(defaults: defaults, preferences: preferences).needsAutomaticSetup)
      }
    }
  }
}
