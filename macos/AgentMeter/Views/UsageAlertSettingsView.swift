import SwiftUI

struct UsageAlertSettingsView: View {
  @Bindable var controller: UsageAlertController
  @State private var settingError: String?
  var body: some View {
    VStack(alignment: .leading, spacing: 14) {
      Toggle("Enable usage alerts", isOn: $controller.settings.enabled)
        .toggleStyle(.switch)
      Text("Warn when a quota window reaches its threshold. Alerts do not pause agents or enforce spending limits.")
        .font(.caption).foregroundStyle(.secondary)
      if controller.settings.enabled {
        ForEach(ProviderID.allCases, id: \.self) { provider in
          providerControls(provider)
        }
      }
      HStack {
        Text(controller.permission.message).font(.caption).foregroundStyle(.secondary)
        Spacer(minLength: 8)
        Button("Test notification") { controller.sendTestNotification() }
      }
      if controller.permission == .denied || controller.permission == .allowed || controller.testMessage != nil {
        Button("Open notification settings") { controller.openNotificationSettings() }
      }
      if let testMessage = controller.testMessage {
        Text(testMessage).font(.caption).foregroundStyle(.secondary)
      }
      if let message = controller.message {
        Text(message).font(.caption).foregroundStyle(.secondary)
      }
      if let settingError {
        Text(settingError).font(.caption).foregroundStyle(.red)
      }
    }.onAppear { controller.refreshPermission() }
  }
  @ViewBuilder private func providerControls(_ provider: ProviderID) -> some View {
    let values = controller.settings.providers[provider] ?? UsageAlertProviderSettings()
    VStack(alignment: .leading, spacing: 10) {
      Toggle(provider.title, isOn: Binding(get: {
        controller.settings.providers[provider]?.enabled ?? true
      }, set: { enabled in
        var copy = controller.settings
        var value = copy.providers[provider] ?? UsageAlertProviderSettings()
        value.enabled = enabled
        copy.providers[provider] = value
        controller.settings = copy
      })).font(.callout.weight(.medium))
      ForEach(values.thresholds) { threshold in
        HStack {
          Toggle("Threshold", isOn: Binding(get: {
            controller.settings.providers[provider]?.thresholds.first { $0.id == threshold.id }?.enabled ?? false
          }, set: { update(provider, id: threshold.id, enabled: $0) }))
            .labelsHidden().accessibilityLabel("\(provider.title) \(threshold.percent)% threshold")
          TextField("Used percent", value: Binding(get: {
            controller.settings.providers[provider]?.thresholds.first { $0.id == threshold.id }?.percent ?? threshold.percent
          }, set: { update(provider, id: threshold.id, percent: $0) }), format: .number)
            .labelsHidden().frame(width: 52).textFieldStyle(.roundedBorder)
            .accessibilityLabel("\(provider.title) used percentage threshold")
          Text("% used").foregroundStyle(.secondary)
          Spacer()
        }.disabled(!values.enabled)
      }
    }
  }
  private func update(_ provider: ProviderID, id: UUID, percent: Int? = nil, enabled: Bool? = nil) {
    var copy = controller.settings
    guard var value = copy.providers[provider], let index = value.thresholds.firstIndex(where: { $0.id == id }) else { return }
    if let percent { value.thresholds[index].percent = percent }
    if let enabled { value.thresholds[index].enabled = enabled }
    copy.providers[provider] = value
    guard copy.isValid else {
      settingError = "Choose a distinct whole percentage from 1 to 100. Your saved thresholds are unchanged."
      return
    }
    settingError = nil
    controller.settings = copy
  }
}
