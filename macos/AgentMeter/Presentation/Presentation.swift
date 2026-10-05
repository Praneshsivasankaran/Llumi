import Foundation
import Observation

enum Destination: String, CaseIterable, Identifiable {
  case usage, settings, about
  var id: String { rawValue }
  var title: String { rawValue.capitalized }
  var symbol: String {
    switch self {
    case .usage: "chart.bar.xaxis"
    case .settings: "gearshape"
    case .about: "info.circle"
    }
  }
}

@MainActor @Observable final class Presentation {
  var destination: Destination? = .usage
  var setupCheckRequest = 0
  func showSetupChecks() {
    destination = .settings
    setupCheckRequest += 1
  }
  var manuallyRefreshing = false
  let preferences = Preferences()
  let updates = UpdatePresentation()
  let loginItem = LoginItem()
  var usage = Dictionary(
    uniqueKeysWithValues: ProviderID.allCases.map { ($0, UsageSnapshot(provider: $0)) })
  var activity = ActivitySnapshot()
  var installations: [ProviderID: Installation] = [:]
  var refreshAction: () -> Void = {}
  var resetNotchPositionAction: () -> Void = {}
  var compact: String {
    activity.providers.map { usage[$0]?.compact ?? $0.title }.joined(separator: "  |  ")
  }
}
