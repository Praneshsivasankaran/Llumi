import Foundation
import Observation

enum NotchPhase: String { case hidden, compact, expanded }
struct NotchState: Equatable {
  private(set) var phase: NotchPhase = .hidden
  private(set) var providers: [ProviderID] = []
  mutating func reconcile(activity: ActivitySnapshot, enabled: Bool) {
    providers = enabled ? activity.providers : []
    if providers.isEmpty { phase = .hidden } else if phase == .hidden { phase = .compact }
  }
  mutating func hover(_ inside: Bool) {
    guard !providers.isEmpty else {
      phase = .hidden
      return
    }
    phase = inside ? .expanded : .compact
  }
}
struct ProviderGlance: Identifiable {
  let snapshot: UsageSnapshot
  var id: ProviderID { snapshot.provider }
  var percentage: String {
    snapshot.primary?.remaining.map(UsageSnapshot.percent)
      ?? (snapshot.state == .loading ? "…" : "--")
  }
  var compactText: String {
    snapshot.primary?.remaining != nil ? percentage + " left" : percentage
  }
  var accessibility: String {
    accessibility(at: Date())
  }
  func accessibility(at now: Date) -> String {
    UsageCopy.detailSummary(snapshot, now: now)
  }
}
enum NotchDetailLayout {
  static func height(rows: [ProviderGlance], availableHeight: CGFloat) -> CGFloat {
    let windows = rows.map { $0.snapshot.detailWindows.count }.max() ?? 0
    let stale = rows.contains { $0.snapshot.state == .stale }
    let desired = 110 + CGFloat(windows) * 86 + (stale ? 22 : 0)
    return min(max(0, availableHeight), min(420, max(180, desired)))
  }
}
@MainActor @Observable final class NotchPresentation {
  var state = NotchState()
  var rows: [ProviderGlance] = []
}
