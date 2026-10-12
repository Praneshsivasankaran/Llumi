import Foundation

// Launch at login and notch visibility remain independent of background operation.
enum BackgroundPolicy {
  enum InitialSurface: Equatable { case none, setup, usage }
  static func initialSurface(startInBackground: Bool, loginLaunch: Bool,
    needsSetup: Bool) -> InitialSurface {
    if startInBackground { return .none }
    if needsSetup { return .setup }
    return loginLaunch ? .none : .usage
  }
  static func showsMenu(startInBackground: Bool, menuEnabled: Bool) -> Bool {
    startInBackground || menuEnabled
  }
  static func showsDock(startInBackground: Bool, fullInterfaceVisible: Bool) -> Bool {
    !startInBackground || fullInterfaceVisible
  }
}
