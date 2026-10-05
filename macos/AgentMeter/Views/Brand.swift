import AppKit
import SwiftUI

struct MeterMark: View {
  var dimension: CGFloat = 22
  private static let appIcon = Bundle.main.url(forResource: "Llumi", withExtension: "icns")
    .flatMap { NSImage(contentsOf: $0) }
  var body: some View {
    Group {
      if let appIcon = Self.appIcon {
        Image(nsImage: appIcon)
          .renderingMode(.original)
          .resizable()
          .interpolation(.high)
          .scaledToFit()
      }
    }.frame(width: dimension, height: dimension).accessibilityHidden(true)
  }
}
// Bundled vector development marks. No network images or embedded provider app assets.
struct ProviderMark: View {
  let provider: ProviderID
  var size: CGFloat = 22
  var body: some View {
    Group {
      if provider == .codex {
        Image(systemName: "chevron.left.forwardslash.chevron.right").font(
          .system(size: size * 0.66, weight: .semibold)
        ).frame(width: size, height: size)
      } else {
        ZStack {
          ForEach(0..<12) { i in
            Capsule().frame(width: size * 0.085, height: size * 0.36).offset(y: -size * 0.29)
              .rotationEffect(.degrees(Double(i) * 30))
          }
        }.frame(width: size, height: size)
      }
    }.accessibilityHidden(true)
  }
}
extension ProviderID {
  var accent: Color {
    self == .codex
      ? Color(red: 0.19, green: 0.68, blue: 0.55) : Color(red: 0.81, green: 0.46, blue: 0.32)
  }
  func notchAccent(for scheme: ColorScheme) -> Color {
    if scheme == .light {
      return self == .codex
        ? Color(red: 0.08, green: 0.40, blue: 0.31) : Color(red: 0.58, green: 0.27, blue: 0.14)
    }
    return self == .codex
      ? Color(red: 0.45, green: 0.9, blue: 0.77) : Color(red: 1, green: 0.73, blue: 0.56)
  }
}
struct AllowanceBar: View {
  let remaining: Double?
  let color: Color
  var body: some View {
    GeometryReader { g in
      ZStack(alignment: .leading) {
        Capsule().fill(.primary.opacity(0.10))
        if let remaining { Capsule().fill(color).frame(width: g.size.width * remaining / 100) }
      }
    }.frame(height: 5).accessibilityLabel("Remaining allowance").accessibilityValue(
      remaining.map(UsageSnapshot.percent) ?? "Unknown")
  }
}
enum UsageCopy {
  static func duration(_ minutes: Int?) -> String? {
    guard let m = minutes else { return nil }
    if m % 1440 == 0 { return "\(m/1440) days" }
    if m % 60 == 0 { return "\(m/60) hours" }
    return "\(m) minutes"
  }
  static func updated(_ date: Date?, now: Date) -> String {
    guard let date else { return "Not updated yet" }
    let m = max(0, Int(now.timeIntervalSince(date) / 60))
    return m < 1 ? "Updated just now" : "Updated \(m)m ago"
  }
  static func windowLabel(_ window: UsageWindow) -> String {
    window.scopeLabel + " · " + (duration(window.durationMinutes) ?? "Window not reported")
  }
  static func remaining(_ window: UsageWindow) -> String {
    window.remaining.map { UsageSnapshot.percent($0) + " remaining" } ?? "Remaining not reported"
  }
  static func resetLines(_ window: UsageWindow, now: Date) -> [String] {
    guard window.reset != nil else { return ["Reset not reported"] }
    var lines = [window.resetText(at: now)]
    if let reset = window.reset {
      lines.append("Reset: " + reset.formatted(.dateTime.month(.abbreviated).day().hour().minute()))
    }
    return lines
  }
  static func observation(_ date: Date, now: Date) -> String {
    let minutes = max(0, Int(now.timeIntervalSince(date) / 60))
    if minutes < 1 { return "Observed just now" }
    if minutes < 60 { return "Observed \(minutes)m ago" }
    if minutes < 1440 { return "Observed \(minutes / 60)h \(minutes % 60)m ago" }
    return "Observed \(minutes / 1440)d \((minutes % 1440) / 60)h ago"
  }
  static func stateMessage(_ snapshot: UsageSnapshot) -> String {
    switch snapshot.state {
    case .loading: "Checking allowance…"
    case .notInstalled: "Install the command-line provider to view allowance."
    case .signedOut: "Sign in through the command-line provider."
    case .notReported: "No remaining time allowance was reported."
    case .unsupportedBilling: "Llumi can’t monitor this billing mode."
    case .unsupportedAllowance: "Llumi can’t read this allowance format."
    case .unavailable: "Couldn’t retrieve allowance. Retry to check."
    case .live, .stale: noGeneralMessage(snapshot)
    }
  }
  private static func noGeneralMessage(_ snapshot: UsageSnapshot) -> String {
    let general = snapshot.consumerWindows.filter { $0.scope == .general }
    if general.isEmpty { return "No general allowance reported." }
    if general.contains(where: { $0.used != nil && $0.durationMinutes == nil }) {
      return "General time window isn’t reported."
    }
    return "General remaining allowance isn’t reported."
  }
  static func staleObservation(_ snapshot: UsageSnapshot, now: Date) -> String? {
    guard snapshot.state == .stale, let date = snapshot.reading?.date else { return nil }
    return "Stale · " + observation(date, now: now)
  }
  static func detailSummary(_ snapshot: UsageSnapshot, now: Date) -> String {
    let headline = snapshot.primary.map(remaining) ?? stateMessage(snapshot)
    var parts = [snapshot.provider.title + ", " + headline, snapshot.state.rawValue]
    for window in snapshot.detailWindows {
      parts.append(windowLabel(window))
      parts.append(remaining(window))
      parts.append(contentsOf: resetLines(window, now: now))
    }
    if let observation = staleObservation(snapshot, now: now) { parts.append(observation) }
    return parts.joined(separator: ". ")
  }
}

// Official provider artwork, bundled locally; no runtime network loading.
struct NotchProviderMark: View {
  let provider: ProviderID
  var size: CGFloat = 22
  var assetBundle: Bundle? = nil
  var body: some View {
    Image(provider == .codex ? "CodexLogo" : "ClaudeLogo", bundle: assetBundle)
      .renderingMode(.template)
      .resizable()
      .scaledToFit()
      .frame(width: size, height: size)
      .foregroundStyle(.primary)
      .accessibilityHidden(true)
  }
}
