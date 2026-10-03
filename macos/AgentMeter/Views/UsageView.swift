import SwiftUI

struct UsageView: View {
  let model: Presentation
  var body: some View {
    GeometryReader { geometry in
      ScrollView {
        TimelineView(.periodic(from: .now, by: 30)) { context in
          VStack(alignment: .leading, spacing: 24) {
            HStack(alignment: .top, spacing: 12) {
              VStack(alignment: .leading, spacing: 5) {
                Text("Usage").font(.title2.bold())
                Text("Your AI coding allowance at a glance.").font(.callout).foregroundStyle(
                  .secondary)
              }
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
                    model.usage.values.compactMap { $0.reading?.date }.min(), now: context.date)
                ).font(.caption2).foregroundStyle(.secondary)
              }
            }
            let columns =
              geometry.size.width >= 560
              ? [GridItem(.flexible(), alignment: .top), GridItem(.flexible(), alignment: .top)]
              : [GridItem(.flexible())]
            LazyVGrid(columns: columns, alignment: .leading, spacing: 16) {
              ForEach(ProviderID.allCases, id: \.self) { p in
                ProviderSection(
                  snapshot: model.usage[p] ?? UsageSnapshot(provider: p), now: context.date)
              }
            }
            Text(
              "Allowance comes from your signed-in command-line providers. Use the same subscription account across your clients."
            ).font(.caption).foregroundStyle(.secondary).fixedSize(
              horizontal: false, vertical: true)
          }.padding(24)
        }
      }
    }
  }
}
private struct ProviderSection: View {
  let snapshot: UsageSnapshot
  let now: Date
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
