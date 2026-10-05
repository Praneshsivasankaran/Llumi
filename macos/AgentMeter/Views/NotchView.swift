import SwiftUI

struct NotchView: View {
  @Environment(\.colorScheme) private var colorScheme
  let model: NotchPresentation
  var assetBundle: Bundle? = nil
  var body: some View {
    Group {
      if model.state.phase == .expanded {
        TimelineView(.periodic(from: .now, by: 30)) { context in
          ScrollView {
            HStack(alignment: .top, spacing: 18) {
              ForEach(model.rows) { row in
                if row.id != model.rows.first?.id { Divider() }
                VStack(alignment: .leading, spacing: 12) {
                  HStack(spacing: 8) {
                    NotchProviderMark(provider: row.id, size: 23, assetBundle: assetBundle)
                    Text(row.id.title).font(.caption.weight(.semibold))
                    Spacer(minLength: 4)
                    Text(row.snapshot.state.rawValue).font(.caption2).foregroundStyle(.secondary)
                  }
                  if row.snapshot.primary != nil {
                    HStack(alignment: .firstTextBaseline, spacing: 5) {
                      Text(row.percentage).font(.system(size: 17, weight: .semibold)).monospacedDigit()
                        .foregroundStyle(row.id.notchAccent(for: colorScheme))
                      Text("remaining").font(.caption).foregroundStyle(.secondary)
                    }
                    AllowanceBar(remaining: row.snapshot.primary?.remaining,
                      color: row.id.notchAccent(for: colorScheme))
                  } else {
                    Text(UsageCopy.stateMessage(row.snapshot)).font(.caption).foregroundStyle(.secondary)
                      .fixedSize(horizontal: false, vertical: true)
                  }
                  VStack(alignment: .leading, spacing: 14) {
                    ForEach(row.snapshot.detailWindows) { window in
                      VStack(alignment: .leading, spacing: 4) {
                        Text(UsageCopy.windowLabel(window)).font(.caption.weight(.medium))
                        Text(UsageCopy.remaining(window)).font(.caption.weight(.medium)).monospacedDigit()
                        ForEach(UsageCopy.resetLines(window, now: context.date), id: \.self) { line in
                          Text(line).font(.caption2).foregroundStyle(.secondary)
                        }
                      }.fixedSize(horizontal: false, vertical: true)
                    }
                  }
                  if let observation = UsageCopy.staleObservation(row.snapshot, now: context.date) {
                    Text(observation).font(.caption2).foregroundStyle(.secondary)
                  }
                }.frame(maxWidth: .infinity, alignment: .leading)
                  .accessibilityElement(children: .ignore)
                  .accessibilityLabel(row.accessibility(at: context.date))
              }
            }.padding(18)
          }
        }
      } else {
        HStack(spacing: 12) {
          ForEach(model.rows) { row in
            if row.id != model.rows.first?.id {
              Rectangle().fill(.primary.opacity(0.22)).frame(width: 1, height: 16)
            }
            HStack(spacing: 7) {
              NotchProviderMark(provider: row.id, size: 19, assetBundle: assetBundle)
              Text(row.compactText).font(.system(size: 13, weight: .semibold)).monospacedDigit()
                .foregroundStyle(row.id.notchAccent(for: colorScheme))
              if row.snapshot.state == .stale {
                Circle().fill(.secondary).frame(width: 4, height: 4).accessibilityLabel("Stale")
              }
            }.accessibilityElement(children: .ignore).accessibilityLabel(row.accessibility)
          }
        }.padding(.horizontal, 16)
      }
    }.frame(maxWidth: .infinity, maxHeight: .infinity)
      .foregroundStyle(.primary)
  }
}
