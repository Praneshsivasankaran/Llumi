import SwiftUI

struct UsagePopoverView: View {
  let model: Presentation
  let refresh: () -> Void
  let settings: () -> Void
  let open: () -> Void
  let quit: () -> Void
  private let blue = Color(red: 0.35, green: 0.61, blue: 1)
  private var providers: [ProviderID] {
    ProviderID.allCases.filter { model.preferences.isEnabled($0) }
  }
  var body: some View {
    TimelineView(.periodic(from: .now, by: 30)) { context in
      VStack(alignment: .leading, spacing: 0) {
        HStack(spacing: 9) {
          ThreeBarsMark(dimension: 21).foregroundStyle(.white)
          Text("Llumi").font(.system(size: 16, weight: .semibold))
          Spacer()
          Button(action: refresh) {
            if model.manuallyRefreshing {
              ProgressView().controlSize(.small).frame(width: 18, height: 18)
            } else {
              Image(systemName: "arrow.clockwise").frame(width: 18, height: 18)
            }
          }.buttonStyle(.plain).foregroundStyle(blue).padding(7)
            .background(.white.opacity(0.05), in: RoundedRectangle(cornerRadius: 7))
            .disabled(model.manuallyRefreshing).help("Refresh usage")
            .accessibilityLabel("Refresh usage")
        }.padding(.horizontal, 18).padding(.top, 17).padding(.bottom, 14)
        Rectangle().fill(.white.opacity(0.08)).frame(height: 1)
        ScrollView {
          VStack(alignment: .leading, spacing: 12) {
            if providers.isEmpty {
              Text("Choose a provider in Settings to start monitoring.")
                .font(.callout).foregroundStyle(.secondary).padding(.vertical, 28)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            ForEach(providers, id: \.self) { provider in
              PopoverProviderRow(snapshot: model.usage[provider] ?? UsageSnapshot(provider: provider),
                now: context.date, blue: blue)
            }
          }.padding(14)
        }.frame(maxHeight: .infinity)
        Rectangle().fill(.white.opacity(0.08)).frame(height: 1)
        HStack(spacing: 10) {
          Button("Open Llumi", action: open).buttonStyle(.plain)
            .font(.callout.weight(.medium)).foregroundStyle(blue)
          Spacer(minLength: 5)
          Button(action: settings) { Image(systemName: "gearshape") }
            .buttonStyle(.plain).help("Settings").accessibilityLabel("Settings")
          Button("Quit", action: quit).buttonStyle(.plain).font(.callout)
        }.padding(.horizontal, 18).padding(.vertical, 14)
      }.frame(width: 360, height: 520)
        .background(Color(red: 0.13, green: 0.14, blue: 0.16))
        .foregroundStyle(Color.white.opacity(0.94))
        .preferredColorScheme(.dark)
    }
  }
}

private struct PopoverProviderRow: View {
  let snapshot: UsageSnapshot
  let now: Date
  let blue: Color
  var body: some View {
    VStack(alignment: .leading, spacing: 10) {
      HStack(spacing: 8) {
        ProviderMark(provider: snapshot.provider, size: 19)
        Text(snapshot.provider.title).font(.callout.weight(.semibold))
        Spacer(minLength: 5)
        Text(snapshot.state.rawValue).font(.caption)
          .foregroundStyle(snapshot.state == .live ? blue : Color.white.opacity(0.55))
      }
      if snapshot.reading != nil && !snapshot.consumerWindows.isEmpty {
        ForEach(snapshot.consumerWindows) { window in
          VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
              Text(UsageCopy.windowLabel(window)).font(.caption.weight(.medium))
                .foregroundStyle(Color.white.opacity(0.76))
              Spacer(minLength: 3)
              Text(UsageCopy.remaining(window)).font(.caption.weight(.medium)).monospacedDigit()
            }
            AllowanceBar(remaining: window.remaining, color: blue).frame(height: 4)
            ForEach(UsageCopy.resetLines(window, now: now), id: \.self) { line in
              Text(line).font(.system(size: 10)).foregroundStyle(Color.white.opacity(0.53))
            }
          }
        }
      } else {
        Text(UsageCopy.stateMessage(snapshot)).font(.caption).foregroundStyle(.secondary)
          .fixedSize(horizontal: false, vertical: true)
      }
      VStack(alignment: .leading, spacing: 3) {
        Text(snapshot.reading.map { "Last success · " + UsageCopy.observation($0.date, now: now) }
          ?? "No successful refresh yet")
        if snapshot.state == .stale {
          Text("Refresh failed · showing the last reported usage")
        }
        if let retry = SetupRetryPresentation.message(snapshot, at: now) { Text(retry) }
      }.font(.system(size: 10)).foregroundStyle(Color.white.opacity(0.53))
    }.padding(13).frame(maxWidth: .infinity, alignment: .leading)
      .background(.white.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
      .overlay(RoundedRectangle(cornerRadius: 10).stroke(.white.opacity(0.065), lineWidth: 1))
      .accessibilityElement(children: .combine)
  }
}
