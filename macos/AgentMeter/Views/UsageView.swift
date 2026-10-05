import SwiftUI

struct UsageView: View {
  let model: Presentation
  @State private var cardHeights: [ProviderID: CGFloat] = [:]
  private var enabledProviders: [ProviderID] {
    ProviderID.allCases.filter { model.preferences.isEnabled($0) }
  }
  private var uniformCardHeight: CGFloat {
    enabledProviders.compactMap { cardHeights[$0] }.max() ?? 0
  }
  var body: some View {
    GeometryReader { geometry in
      ScrollView {
        TimelineView(.periodic(from: .now, by: 30)) { context in
          VStack(alignment: .leading, spacing: 24) {
            HStack(alignment: .top, spacing: 12) {
              Text("Usage").font(.title2.bold())
              Spacer(minLength: 8)
              VStack(alignment: .trailing, spacing: 3) {
                Button {
                  model.refreshAction()
                } label: {
                  if model.manuallyRefreshing {
                    ProgressView().controlSize(.small).frame(width: 18, height: 18)
                  } else {
                    Image(systemName: "arrow.clockwise").frame(width: 18, height: 18)
                  }
                }.buttonStyle(.borderless).padding(6).background(
                  .quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 7)
                ).help("Refresh allowance").accessibilityLabel("Refresh allowance").disabled(
                  model.manuallyRefreshing)
                Text(
                  UsageCopy.updated(
                    enabledProviders.compactMap { model.usage[$0]?.reading?.date }.min(), now: context.date)
                ).font(.caption2).foregroundStyle(.secondary)
              }
            }
            if enabledProviders.isEmpty {
              VStack(spacing: 16) {
                Image(systemName: "slider.horizontal.3").font(.system(size: 28)).foregroundStyle(.secondary)
                Text("Choose a provider in Settings to start monitoring.")
                  .font(.callout).multilineTextAlignment(.center)
                Button("Open Settings") { model.destination = .settings }
                  .buttonStyle(.borderedProminent)
              }.frame(maxWidth: .infinity).padding(.vertical, 50)
            } else {
              let columns = geometry.size.width >= 560 && enabledProviders.count > 1
                ? [GridItem(.flexible(), alignment: .top), GridItem(.flexible(), alignment: .top)]
                : [GridItem(.flexible())]
              LazyVGrid(columns: columns, alignment: .leading, spacing: 16) {
                ForEach(enabledProviders, id: \.self) { p in
                  ProviderSection(snapshot: model.usage[p] ?? UsageSnapshot(provider: p),
                    now: context.date, minimumHeight: uniformCardHeight)
                }
              }.onPreferenceChange(ProviderCardHeightKey.self) { heights in
                if cardHeights != heights { cardHeights = heights }
              }
            }
          }.padding(24)
        }
      }
    }
  }
}
private struct ProviderCardHeightKey: PreferenceKey {
  static let defaultValue: [ProviderID: CGFloat] = [:]
  static func reduce(value: inout [ProviderID: CGFloat], nextValue: () -> [ProviderID: CGFloat]) {
    value.merge(nextValue(), uniquingKeysWith: max)
  }
}
private struct ProviderSection: View {
  let snapshot: UsageSnapshot
  let now: Date
  let minimumHeight: CGFloat
  var body: some View {
    VStack(alignment: .leading, spacing: 18) {
      HStack(spacing: 10) {
        ProviderMark(provider: snapshot.provider, size: 25).foregroundStyle(
          snapshot.provider.accent)
        Text(snapshot.provider.title).font(.headline)
        Spacer(minLength: 4)
        Text(snapshot.state.rawValue).font(.caption).foregroundStyle(.secondary)
      }
      if let reading = snapshot.reading {
        let primary = snapshot.primary
        if let primary {
          HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(primary.remaining.map(UsageSnapshot.percent) ?? "--").font(
              .system(size: 24, weight: .semibold, design: .rounded)
            ).monospacedDigit()
            Text("remaining").font(.callout).foregroundStyle(.secondary)
          }
          AllowanceBar(remaining: primary.remaining, color: snapshot.provider.accent)
        }
        VStack(alignment: .leading, spacing: 16) {
          ForEach(snapshot.consumerWindows.sorted { a, b in a.id == primary?.id && b.id != primary?.id }) {
            window in
            VStack(alignment: .leading, spacing: 5) {
              HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(label(window)).font(
                  .callout.weight(window.id == primary?.id ? .medium : .regular))
                Spacer(minLength: 4)
                if window.id != primary?.id {
                  Text(window.remaining.map(UsageSnapshot.percent) ?? "--").font(
                    .callout.weight(.medium)
                  ).monospacedDigit()
                }
              }
              Text(window.resetText(at: now)).font(.caption).foregroundStyle(.secondary)
              if let reset = window.reset, reset > now {
                Text(reset, format: .dateTime.month(.abbreviated).day().hour().minute()).font(
                  .caption2
                ).foregroundStyle(.secondary)
              }
            }
          }
        }
        if snapshot.consumerWindows.isEmpty {
          Text(unavailable).font(.callout).foregroundStyle(.secondary)
        }
        if snapshot.state == .stale {
          Text(UsageCopy.updated(reading.date, now: now) + " · last verified allowance").font(
            .caption
          ).foregroundStyle(.secondary)
        }
      } else {
        Text(snapshot.state == .loading ? "Checking allowance…" : unavailable).font(.callout)
          .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).padding(
            .vertical, 5)
      }
    }.padding(18).frame(maxWidth: .infinity, alignment: .topLeading)
      .fixedSize(horizontal: false, vertical: true)
      .background(GeometryReader { geometry in
        Color.clear.preference(key: ProviderCardHeightKey.self,
          value: [snapshot.provider: geometry.size.height])
      })
      .frame(minHeight: minimumHeight, alignment: .topLeading)
      .background(.background.opacity(0.65), in: RoundedRectangle(cornerRadius: 13))
      .overlay(RoundedRectangle(cornerRadius: 13).stroke(.primary.opacity(0.07), lineWidth: 1))
  }
  private var unavailable: String {
    switch snapshot.state {
    case .notInstalled: "Install the command-line provider to view allowance."
    case .signedOut: "Sign in through the command-line provider."
    default: "Verified allowance is temporarily unavailable."
    }
  }
  private func label(_ window: UsageWindow) -> String {
    let duration = UsageCopy.duration(window.durationMinutes)
    if snapshot.provider == .codex { return window.label + (duration.map { " · \($0)" } ?? "") }
    return window.claudeDisplayLabel ?? ""
  }
}
