import SwiftUI

struct MainView: View {
  @Bindable var model: Presentation
  var body: some View {
    NavigationSplitView {
      VStack(alignment: .leading, spacing: 16) {
        HStack(spacing: 9) {
          MeterMark()
          Text("Llumi").font(.headline)
        }.padding(.horizontal, 15).padding(.top, 15)
        List(Destination.allCases, selection: $model.destination) { destination in
          Label(destination.title, systemImage: destination.symbol).tag(destination)
        }.listStyle(.sidebar)
      }.navigationSplitViewColumnWidth(min: 150, ideal: 166, max: 190)
        .toolbar(removing: .sidebarToggle)
    } detail: {
      VStack(spacing: 0) {
        if let version = model.updates.availableVersion {
          HStack(spacing: 12) {
            Image(systemName: "arrow.down.circle.fill").font(.title2).foregroundStyle(.tint)
            Text("Llumi \(version) is available").font(.callout.weight(.medium))
            Spacer(minLength: 8)
            Button("Later") { model.updates.dismissAction() }
            Button("Update…") { model.updates.checkAction() }
              .buttonStyle(.borderedProminent).disabled(!model.updates.canCheck)
          }.padding(14)
            .background(.tint.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 24).padding(.top, 20)
        }
        Group {
          switch model.destination ?? .usage {
          case .usage: UsageView(model: model)
          case .settings:
            SettingsView(preferences: model.preferences, login: model.loginItem, checks: model)
          case .about: AboutView()
          }
        }
      }.frame(minWidth: 410, minHeight: 420).background(Color(nsColor: .windowBackgroundColor))
    }.navigationSplitViewStyle(.balanced)
  }
}
struct SettingsView: View {
  @Bindable var preferences: Preferences
  let login: LoginItem
  var checks: Presentation? = nil
  var body: some View {
    VStack(alignment: .leading, spacing: 20) {
      Text("Settings").font(.title2.bold())
      ScrollViewReader { proxy in
        Form {
          Section("General") {
            Toggle(isOn: Binding(get: { login.enabled }, set: { login.setEnabled($0) })) {
              Label("Launch at Login", systemImage: "power")
            }
            if let message = login.message {
              Text(message).font(.caption).foregroundStyle(.secondary)
            }
            HStack {
              Toggle(isOn: $preferences.notchEnabled) {
                Label("Notch Monitor", systemImage: "macbook")
              }
              if let checks {
                Button("Reset Position") { checks.resetNotchPositionAction() }
                  .help("Move the notch back to the top center of the screen")
              }
            }
            Toggle(isOn: $preferences.menuEnabled) {
              Label("Menu Bar Icon", systemImage: "menubar.rectangle")
            }
          }
          Section("Appearance") {
            Picker(selection: $preferences.appearance) {
              ForEach(AppAppearance.allCases) { Text($0.title).tag($0) }
            } label: {
              Label("Appearance", systemImage: "circle.lefthalf.filled")
            }
          }
          if let checks {
            Section("Updates") {
              HStack {
                Label("Llumi \(releaseVersion)", systemImage: "arrow.down.circle")
                Spacer()
                if checks.updates.checking { ProgressView().controlSize(.small) }
                Button("Check for Updates…") { checks.updates.checkAction() }
                  .disabled(!checks.updates.canCheck)
              }
              Toggle("Automatically check for updates", isOn: Binding(
                get: { checks.updates.automaticChecks },
                set: { checks.updates.automaticChecks = $0 }))
                .toggleStyle(.switch)
            }
            Section {
              CheckSetupView(model: checks).id("setup-checks")
            }
          }
        }.formStyle(.grouped).scrollContentBackground(.hidden)
          .onAppear {
            if let checks, checks.setupCheckRequest > 0 {
              proxy.scrollTo("setup-checks", anchor: .top)
            }
          }
          .onChange(of: checks?.setupCheckRequest) { _, _ in
            proxy.scrollTo("setup-checks", anchor: .top)
          }
      }
    }.padding(24).onAppear { login.synchronize() }
  }
  private var releaseVersion: String {
    Bundle.main.object(forInfoDictionaryKey: "LlumiReleaseVersion") as? String
      ?? Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.1.3"
  }
}
private struct AboutView: View {
  var body: some View {
    VStack(spacing: 16) {
      MeterMark().scaleEffect(2).frame(height: 50)
      Text("Llumi").font(.title.bold())
      Text(
        "Version \(Bundle.main.object(forInfoDictionaryKey:"LlumiReleaseVersion") as? String ?? "1.1.3")"
      ).font(.callout).foregroundStyle(.secondary)
      Text("Track your AI coding usage.").font(.callout).foregroundStyle(.secondary)
    }.frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}
