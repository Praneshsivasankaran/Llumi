import SwiftUI

// Both dialogs are native views. Website content is opened only when requested.
struct UpdatePromptView: View {
  @Bindable var updates: UpdatePresentation
  var body: some View {
    VStack(alignment: .leading, spacing: 20) {
      HStack(spacing: 12) {
        MeterMark()
        Text(title).font(.title2.bold())
      }
      if let completed = updates.completedUpdate {
        Text("Llumi \(completed.version) is ready.")
        HStack {
          Link("View release notes", destination: completed.releaseNotesURL)
          Spacer()
          Button("Continue") { updates.dismissPrompt() }
            .buttonStyle(UpdateButtonStyle(primary: true)).keyboardShortcut(.defaultAction)
        }
      } else if let version = updates.readyVersion {
        Text(version.isEmpty ? "An update is ready to install." : "Llumi \(version) is ready to install.")
        Text("Restart now, or keep using Llumi. The update will install when you quit.")
          .font(.callout).foregroundStyle(.secondary)
        HStack {
          if let url = updates.readyNotesURL { Link("View release notes", destination: url) }
          Spacer()
          Button("Later") { updates.dismissPrompt() }
            .buttonStyle(UpdateButtonStyle(primary: false)).keyboardShortcut(.cancelAction)
          Button("Restart and update") { updates.restartPreparedUpdate() }
            .buttonStyle(UpdateButtonStyle(primary: true)).keyboardShortcut(.defaultAction)
        }
      }
    }.padding(28).frame(width: 480).fixedSize(horizontal: false, vertical: true)
      .background(Color(nsColor: .windowBackgroundColor))
  }
  private var title: String {
    updates.completedUpdate == nil ? "Update ready" : "The app has been updated"
  }
}

private struct UpdateButtonStyle: ButtonStyle {
  let primary: Bool
  func makeBody(configuration: Configuration) -> some View {
    configuration.label.padding(.horizontal, 14).padding(.vertical, 7)
      .foregroundStyle(primary ? Color.white : Color.primary)
      .background((primary ? Color.accentColor : Color.primary.opacity(0.08))
        .opacity(configuration.isPressed ? 0.75 : 1), in: RoundedRectangle(cornerRadius: 7))
  }
}
