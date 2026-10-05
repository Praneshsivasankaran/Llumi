import CoreGraphics
import Foundation

struct MonitorAnchor: Codable, Equatable {
  static let defaultsKey = "notchPlacementV1"
  let schema: Int
  let displayID: UInt32
  let centerX: Double
  let topY: Double

  init?(displayID: UInt32, centerX: Double, topY: Double, schema: Int = 1) {
    guard schema == 1, displayID != 0, centerX.isFinite, topY.isFinite,
      (0...1).contains(centerX), (0...1).contains(topY)
    else { return nil }
    self.schema = schema
    self.displayID = displayID
    self.centerX = centerX
    self.topY = topY
  }
  static func load(from defaults: UserDefaults) -> MonitorAnchor? {
    guard let data = defaults.data(forKey: defaultsKey), data.count <= 512,
      let decoded = try? JSONDecoder().decode(Self.self, from: data)
    else { return nil }
    return Self(displayID: decoded.displayID, centerX: decoded.centerX,
      topY: decoded.topY, schema: decoded.schema)
  }
  func save(to defaults: UserDefaults) {
    if let data = try? JSONEncoder().encode(self) { defaults.set(data, forKey: Self.defaultsKey) }
  }
}

struct MonitorDisplay: Equatable {
  let id: UInt32
  let frame: CGRect
  let visible: CGRect
}

struct MonitorDragGesture {
  static let threshold: CGFloat = 5
  let origin: CGPoint
  private(set) var isDragging = false
  mutating func move(to point: CGPoint) {
    guard point.x.isFinite, point.y.isFinite else { return }
    if hypot(point.x - origin.x, point.y - origin.y) >= Self.threshold { isDragging = true }
  }
}

enum MonitorGeometry {
  static func frame(screen: CGRect, visible: CGRect, safeTop: CGFloat, contentWidth: CGFloat)
    -> CGRect
  {
    let width = min(max(160, contentWidth + 24), visible.width)
    let height = min(24, visible.height)
    let x = max(visible.minX, min(screen.midX - width / 2, visible.maxX - width))
    let y = max(visible.minY, min(visible.maxY, screen.maxY - safeTop) - height - 2)
    return CGRect(x: x, y: y, width: width, height: height)
  }

  static func clamped(_ frame: CGRect, to visible: CGRect) -> CGRect {
    let width = min(max(0, frame.width), visible.width)
    let height = min(max(0, frame.height), visible.height)
    return CGRect(
      x: min(max(frame.minX, visible.minX), visible.maxX - width),
      y: min(max(frame.minY, visible.minY), visible.maxY - height),
      width: width, height: height)
  }
  static func anchor(frame: CGRect, on display: MonitorDisplay) -> MonitorAnchor? {
    guard display.visible.width > 0, display.visible.height > 0 else { return nil }
    let frame = clamped(frame, to: display.visible)
    return MonitorAnchor(
      displayID: display.id,
      centerX: Double((frame.midX - display.visible.minX) / display.visible.width),
      topY: Double((frame.maxY - display.visible.minY) / display.visible.height))
  }
  static func frame(size: CGSize, anchor: MonitorAnchor, visible: CGRect) -> CGRect {
    clamped(CGRect(
      x: visible.minX + CGFloat(anchor.centerX) * visible.width - size.width / 2,
      y: visible.minY + CGFloat(anchor.topY) * visible.height - size.height,
      width: size.width, height: size.height), to: visible)
  }
  static func display(for anchor: MonitorAnchor, among displays: [MonitorDisplay],
    fallbackID: UInt32?) -> MonitorDisplay? {
    displays.first { $0.id == anchor.displayID }
      ?? displays.first { $0.id == fallbackID }
      ?? displays.first
  }
  static func display(at point: CGPoint, among displays: [MonitorDisplay]) -> MonitorDisplay? {
    if let containing = displays.first(where: { $0.frame.contains(point) }) { return containing }
    // Gaps between monitors still permit a continuous drag to the nearest screen.
    return displays.min {
      distance(point, from: $0.frame) < distance(point, from: $1.frame)
    }
  }
  private static func distance(_ point: CGPoint, from rect: CGRect) -> CGFloat {
    let x = min(max(point.x, rect.minX), rect.maxX)
    let y = min(max(point.y, rect.minY), rect.maxY)
    return hypot(point.x - x, point.y - y)
  }
  static func draggedFrame(start: CGRect, pressed: CGPoint, current: CGPoint,
    visible: CGRect) -> CGRect {
    clamped(start.offsetBy(dx: current.x - pressed.x, dy: current.y - pressed.y), to: visible)
  }
}
