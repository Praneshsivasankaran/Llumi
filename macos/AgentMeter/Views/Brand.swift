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
}

// Official provider artwork, bundled locally; no runtime network loading.
struct NotchProviderMark: View {
  let provider: ProviderID
  var size: CGFloat = 22
  var body: some View {
    Image(provider == .codex ? "CodexLogo" : "ClaudeLogo")
      .renderingMode(.template)
      .resizable()
      .scaledToFit()
      .frame(width: size, height: size)
      .foregroundStyle(.primary)
      .accessibilityHidden(true)
  }
}
