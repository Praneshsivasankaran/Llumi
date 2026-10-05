import AppKit
import SwiftUI

struct SetupDemoView: View {
  let provider: ProviderID
  @Environment(\.accessibilityReduceMotion) private var reduceMotion
  @State private var playing = true
  @State private var replayID = 0
  private var name: String { provider == .codex ? "CodexSetup" : "ClaudeSetup" }
  private var providerTitle: String { provider == .codex ? "Codex" : "Claude Code" }
  var body: some View {
    VStack(alignment: .leading, spacing: 10) {
      HStack {
        Text("Setup demo").font(.headline)
        Spacer()
        if !reduceMotion {
          Button {
            playing.toggle()
          } label: {
            Label(playing ? "Pause" : "Play", systemImage: playing ? "pause.fill" : "play.fill")
          }.buttonStyle(.borderless).font(.caption)
          Button {
            replayID += 1
            playing = true
          } label: {
            Label("Replay", systemImage: "arrow.counterclockwise")
          }.buttonStyle(.borderless).font(.caption)
        }
      }
      AnimatedSetupImage(name: name, playing: playing, replayID: replayID, reduceMotion: reduceMotion)
        .aspectRatio(16.0 / 9.0, contentMode: .fit)
        .frame(maxWidth: .infinity)
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.primary.opacity(0.08), lineWidth: 1))
        .accessibilityLabel("\(providerTitle) setup demo. Copy the install command into Terminal, then run the sign-in command.")
    }.padding(.top, 8)
  }
}

private struct AnimatedSetupImage: NSViewRepresentable {
  let name: String
  let playing: Bool
  let replayID: Int
  let reduceMotion: Bool
  final class Coordinator {
    var resource: String?
    var replayID = -1
  }
  func makeCoordinator() -> Coordinator { Coordinator() }
  func makeNSView(context: Context) -> NSImageView {
    let view = NSImageView()
    view.imageScaling = .scaleProportionallyUpOrDown
    view.imageAlignment = .alignCenter
    view.animates = false
    view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    view.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
    return view
  }
  func updateNSView(_ view: NSImageView, context: Context) {
    let fileExtension = reduceMotion ? "png" : "gif"
    let resource = "\(name).\(fileExtension)"
    if context.coordinator.resource != resource || context.coordinator.replayID != replayID {
      view.animates = false
      view.image = nil
      if let url = Bundle.main.url(forResource: name, withExtension: fileExtension) {
        view.image = NSImage(contentsOf: url)
      }
      context.coordinator.resource = resource
      context.coordinator.replayID = replayID
    }
    view.animates = playing && !reduceMotion
  }
  static func dismantleNSView(_ view: NSImageView, coordinator: Coordinator) {
    view.animates = false
    view.image = nil
  }
}
