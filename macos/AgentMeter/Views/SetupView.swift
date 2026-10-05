import AppKit
import SwiftUI

struct SetupView: View {
  @Bindable var model: Presentation
  @Bindable var flow: SetupFlow
  let finished: () -> Void
  var body: some View {
    Group {
      if flow.step == .welcome {
        VStack(spacing: 28) {
          MeterMark(dimension: 74.8).frame(width: 84, height: 84)
          Text("Llumi").font(.system(size: 36, weight: .semibold, design: .rounded))
          Button("Get Started") { flow.next() }
            .buttonStyle(.borderedProminent).controlSize(.large)
            .keyboardShortcut(.defaultAction)
            .padding(.top, 8)
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
      } else {
        VStack(alignment: .leading, spacing: 22) {
          HStack(spacing: 10) {
            MeterMark()
            Text("Llumi").font(.headline)
            Spacer()
          }
          ScrollView {
            VStack(alignment: .leading, spacing: 20) { content }
              .frame(maxWidth: .infinity, alignment: .leading)
          }
          Divider()
          HStack {
            Button("Back") { flow.back() }
            Spacer()
            Button(nextTitle) {
              if flow.step == .done { flow.complete(); finished() }
              else {
                flow.next()
                if flow.step == .verify { model.refreshAction() }
              }
            }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
          }
        }
      }
    }.padding(28).frame(minWidth: 540, idealWidth: 580, minHeight: 510, idealHeight: 580)
      .background(Color(nsColor: .windowBackgroundColor))
      .preferredColorScheme(.light)
  }
  private var nextTitle: String {
    if flow.step == .done { return "Start Llumi" }
    if flow.step == .verify && !flow.selected.contains(where: { status($0) == .ready }) {
      return "Finish Anyway"
    }
    return "Continue"
  }
  private func status(_ provider: ProviderID) -> SetupStatus {
    SetupStatus(snapshot: model.usage[provider] ?? UsageSnapshot(provider: provider))
  }
  @ViewBuilder private var content: some View {
    switch flow.step {
    case .welcome:
      EmptyView()
    case .providers:
      heading("Choose your providers", "Select either or both. You can also set them up later.")
      ForEach(ProviderID.allCases, id: \.self) { provider in
        ProviderSwitchRow(provider: provider, preferences: model.preferences,
          detail: model.installations[provider] == nil ? "Not detected" : "Installed")
          .padding(.vertical, 10)
      }
      checkAgain
    case .codex: providerInstructions(.codex)
    case .claude: providerInstructions(.claude)
    case .verify:
      CheckSetupView(model: model)
    case .preferences:
      Text("Make it yours").font(.title.bold())
      Text("These are the same preferences you’ll find in Settings.").foregroundStyle(.secondary)
      SettingsView(preferences: model.preferences, login: model.loginItem)
        .frame(height: 365)
    case .done:
      heading("You’re all set", "Llumi will keep your allowance up to date. Setup is always available from the Help menu.")
      statuses
      Text("The notch appears when you use a supported coding-agent session. Close the main window to keep monitoring in the background.")
        .foregroundStyle(.secondary)
    }
  }
  private func heading(_ title: String, _ subtitle: String) -> some View {
    VStack(alignment: .leading, spacing: 8) {
      Text(title).font(.title.bold())
      Text(subtitle).foregroundStyle(.secondary)
    }
  }
  private var statuses: some View {
    VStack(spacing: 16) {
      ForEach(ProviderID.allCases, id: \.self) { provider in
        HStack {
          ProviderMark(provider: provider)
          Text(provider == .claude ? "Claude Code" : "Codex")
          Spacer()
          Label(model.preferences.isEnabled(provider) ? status(provider).rawValue : "Monitoring off",
            systemImage: model.preferences.isEnabled(provider) && status(provider) == .ready ? "checkmark.circle" : "circle.dotted")
            .foregroundStyle(.secondary)
        }
      }
    }.padding(.vertical, 8)
  }
  private var checkAgain: some View {
    HStack {
      Button("Retry") { model.refreshAction() }.disabled(model.manuallyRefreshing)
      if model.manuallyRefreshing { ProgressView().controlSize(.small) }
    }
  }
  private func providerInstructions(_ provider: ProviderID) -> some View {
    VStack(alignment: .leading, spacing: 22) {
      heading(provider == .codex ? "Set up Codex" : "Set up Claude Code",
        "Llumi uses the locally installed command-line tool. Install it and sign in to get started.")
      Text("Open Terminal, paste each command, then press Return.")
        .font(.callout).foregroundStyle(.secondary)
      CommandBlock(title: "1. Install \(provider == .codex ? "Codex" : "Claude Code")",
        command: ProviderSetup.install(provider))
      CommandBlock(title: "2. Sign in",
        command: ProviderSetup.login(provider))
      HStack {
        checkAgain
        Spacer()
        Link("Official setup guide ↗", destination: ProviderSetup.documentation(provider))
      }
      SetupDemoView(provider: provider)
    }
  }
}

struct ProviderSwitchRow: View {
  let provider: ProviderID
  @Bindable var preferences: Preferences
  let detail: String
  private var title: String { provider == .claude ? "Claude Code" : "Codex" }
  var body: some View {
    HStack(spacing: 12) {
      ProviderMark(provider: provider, size: 25).foregroundStyle(provider.accent)
      VStack(alignment: .leading, spacing: 4) {
        Text(title).font(.headline)
        Text(detail).font(.callout).foregroundStyle(.secondary)
      }
      Spacer(minLength: 12)
      Toggle("Monitor \(title)", isOn: Binding(
        get: { preferences.isEnabled(provider) },
        set: { preferences.setEnabled(provider, $0) }))
        .toggleStyle(.switch).labelsHidden().accessibilityLabel("Monitor \(title)")
    }
  }
}

private struct CommandBlock: View {
  let title: String
  let command: String
  @State private var copied = false
  var body: some View {
    VStack(alignment: .leading, spacing: 10) {
      Text(title).font(.headline)
      HStack(spacing: 12) {
        Text(command).font(.system(.callout, design: .monospaced)).textSelection(.enabled)
          .frame(maxWidth: .infinity, alignment: .leading)
        Button(copied ? "Copied" : "Copy") {
          NSPasteboard.general.clearContents()
          copied = NSPasteboard.general.setString(command, forType: .string)
        }.accessibilityLabel("Copy \(title) command")
      }.padding(12).background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
    }.onChange(of: command) { _, _ in copied = false }
  }
}

struct CheckSetupView: View {
  @Bindable var model: Presentation
  @State private var copied = false
  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text("Check Setup").font(.title2.bold())
      ForEach(ProviderID.allCases, id: \.self) { provider in
        let snapshot = model.usage[provider] ?? UsageSnapshot(provider: provider)
        VStack(alignment: .leading, spacing: 6) {
          ProviderSwitchRow(provider: provider, preferences: model.preferences,
            detail: model.preferences.isEnabled(provider) ? SetupStatus(snapshot: snapshot).rawValue : "Monitoring off")
          if model.preferences.isEnabled(provider) {
            Text(SetupDiagnostic(snapshot).summary).font(.caption).foregroundStyle(.secondary)
              .fixedSize(horizontal: false, vertical: true)
          }
        }
      }
      HStack {
        Button("Retry") { copied = false; model.refreshAction() }.disabled(model.manuallyRefreshing)
        if model.manuallyRefreshing { ProgressView().controlSize(.small) }
        Spacer()
        Button(copied ? "Copied" : "Copy Diagnostics") {
          let report = SetupDiagnostics.report(model.usage,
            version: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
            build: Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String)
          NSPasteboard.general.clearContents()
          copied = NSPasteboard.general.setString(report, forType: .string)
        }
      }
    }.padding(20)
  }
}
