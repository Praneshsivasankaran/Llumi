import SwiftUI

struct UsageView: View {
  let model: Presentation
  var assetBundle: Bundle? = nil
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
                    now: context.date, minimumHeight: uniformCardHeight, assetBundle: assetBundle)
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
  @Environment(\.colorScheme) private var colorScheme
  let snapshot: UsageSnapshot
  let now: Date
  let minimumHeight: CGFloat
  let assetBundle: Bundle?
  var body: some View {
    VStack(alignment: .leading, spacing: 18) {
      HStack(spacing: 10) {
        ProviderMark(provider: snapshot.provider, size: 25, assetBundle: assetBundle)
          .foregroundStyle(snapshot.provider.accent)
        Text(snapshot.provider.title).font(.headline)
        Spacer(minLength: 4)
        Text(snapshot.state.rawValue).font(.caption).foregroundStyle(.secondary)
      }
      if snapshot.reading != nil {
        let primary = snapshot.primary
        if let primary {
          HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(primary.remaining.map(UsageSnapshot.percent) ?? "--").font(
              .system(size: 24, weight: .semibold, design: .rounded)
            ).monospacedDigit()
            Text("remaining").font(.callout).foregroundStyle(.secondary)
          }
          AllowanceBar(remaining: primary.remaining, color: snapshot.provider.accent)
        } else {
          Text(UsageCopy.stateMessage(snapshot)).font(.callout).foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
        }
        VStack(alignment: .leading, spacing: 16) {
          ForEach(snapshot.consumerWindows) { window in
            VStack(alignment: .leading, spacing: 5) {
              Text(UsageCopy.windowLabel(window)).font(
                .callout.weight(window.id == primary?.id ? .medium : .regular))
              Text(UsageCopy.remaining(window)).font(.callout.weight(.medium)).monospacedDigit()
              ForEach(UsageCopy.resetLines(window, now: now), id: \.self) { line in
                Text(line).font(.caption).foregroundStyle(.secondary)
              }
            }.fixedSize(horizontal: false, vertical: true)
          }
        }
        if let observation = UsageCopy.staleObservation(snapshot, now: now) {
          Text(observation).font(.caption).foregroundStyle(.secondary)
        }
      } else {
        Text(UsageCopy.stateMessage(snapshot)).font(.callout)
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
      .background {
        let shape = RoundedRectangle(cornerRadius: 13)
        if colorScheme == .light {
          shape.fill(Color(red: 0.96, green: 0.96, blue: 0.97))
        } else {
          shape.fill(.background.opacity(0.65))
        }
      }
      .overlay(RoundedRectangle(cornerRadius: 13).stroke(
        .primary.opacity(colorScheme == .light ? 0.14 : 0.07), lineWidth: 1))
      .shadow(color: colorScheme == .light ? .black.opacity(0.05) : .clear, radius: 8, y: 2)
      .accessibilityElement(children: .ignore)
      .accessibilityLabel(UsageCopy.detailSummary(snapshot, now: now))
  }
}
