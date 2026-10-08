import AppKit
import SwiftUI

struct SetupDemoView: View {
  let provider: ProviderID
  @Environment(\.accessibilityReduceMotion) private var reduceMotion
  private var name: String { provider == .codex ? "CodexSetup" : "ClaudeSetup" }
  private var providerTitle: String { provider == .codex ? "Codex" : "Claude Code" }
  var body: some View {
    AnimatedSetupImage(name: name, reduceMotion: reduceMotion)
      .aspectRatio(16.0 / 9.0, contentMode: .fit)
      .frame(maxWidth: .infinity)
      .clipShape(RoundedRectangle(cornerRadius: 10))
      .overlay(RoundedRectangle(cornerRadius: 10).stroke(.primary.opacity(0.08), lineWidth: 1))
      .accessibilityLabel("How to install \(providerTitle) and sign in using Terminal.")
      .padding(.top, 8)
  }
}

private struct AnimatedSetupImage: NSViewRepresentable {
  let name: String
  let reduceMotion: Bool
  final class Coordinator {
    var resource: String?
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
    if context.coordinator.resource != resource {
      view.animates = false
      view.image = nil
      if let url = Bundle.main.url(forResource: name, withExtension: fileExtension) {
        view.image = NSImage(contentsOf: url)
      }
      context.coordinator.resource = resource
    }
    view.animates = !reduceMotion
  }
  static func dismantleNSView(_ view: NSImageView, coordinator: Coordinator) {
    view.animates = false
    view.image = nil
  }
}
