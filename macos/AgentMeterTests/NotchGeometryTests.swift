import CoreGraphics
import Foundation
import XCTest

final class NotchGeometryTests: XCTestCase {
  private let left = MonitorDisplay(
    id: 1, frame: CGRect(x: -1600, y: 0, width: 1600, height: 1000),
    visible: CGRect(x: -1600, y: 30, width: 1600, height: 940))
  private let right = MonitorDisplay(
    id: 2, frame: CGRect(x: 100, y: -200, width: 1200, height: 800),
    visible: CGRect(x: 100, y: -170, width: 1200, height: 740))

  func testClickThresholdAndDragReturningToItsOrigin() {
    var gesture = MonitorDragGesture(origin: CGPoint(x: 200, y: 200))
    gesture.move(to: CGPoint(x: 202, y: 202))
    XCTAssertFalse(gesture.isDragging)
    gesture.move(to: CGPoint(x: 203, y: 204))
    XCTAssertTrue(gesture.isDragging)
    gesture.move(to: gesture.origin)
    XCTAssertTrue(gesture.isDragging, "A completed drag must never become an accidental click")
  }
  func testSavedCenterAndTopSurviveExpansionWithoutDrift() throws {
    let compact = CGRect(x: -1056, y: 596, width: 112, height: 34)
    let anchor = try XCTUnwrap(MonitorGeometry.anchor(frame: compact, on: left))
    let expanded = MonitorGeometry.frame(
      size: CGSize(width: 390, height: 174), anchor: anchor, visible: left.visible)
    XCTAssertEqual(expanded.midX, compact.midX, accuracy: 0.001)
    XCTAssertEqual(expanded.maxY, compact.maxY, accuracy: 0.001)
    let restored = MonitorGeometry.frame(size: compact.size, anchor: anchor, visible: left.visible)
    XCTAssertEqual(restored.minX, compact.minX, accuracy: 0.001)
    XCTAssertEqual(restored.minY, compact.minY, accuracy: 0.001)
  }
  func testExpansionAndOversizedFramesStayWithinUsableBounds() throws {
    let compact = CGRect(x: -1600, y: 30, width: 112, height: 34)
    let anchor = try XCTUnwrap(MonitorGeometry.anchor(frame: compact, on: left))
    XCTAssertTrue(left.visible.contains(MonitorGeometry.frame(
      size: CGSize(width: 390, height: 174), anchor: anchor, visible: left.visible)))
    XCTAssertEqual(MonitorGeometry.clamped(
      CGRect(x: -4000, y: -4000, width: 5000, height: 5000), to: left.visible), left.visible)
  }
  func testDraggingAcrossDisplaysRetainsGrabOffsetAndClampsEdges() throws {
    let start = CGRect(x: -1300, y: 600, width: 200, height: 34)
    let pressed = CGPoint(x: -1250, y: 610)
    let current = CGPoint(x: 600, y: 300)
    let destination = try XCTUnwrap(MonitorGeometry.display(at: current, among: [left, right]))
    XCTAssertEqual(destination.id, right.id)
    let moved = MonitorGeometry.draggedFrame(
      start: start, pressed: pressed, current: current, visible: destination.visible)
    XCTAssertEqual(moved.minX, 550)
    XCTAssertEqual(moved.minY, 290)
    let edge = MonitorGeometry.draggedFrame(
      start: start, pressed: pressed, current: CGPoint(x: 1290, y: 590), visible: right.visible)
    XCTAssertTrue(right.visible.contains(edge))
    XCTAssertEqual(edge.maxX, right.visible.maxX)
    XCTAssertEqual(edge.maxY, right.visible.maxY)
  }
  func testDisplayGapsChooseNearestScreenAndRemovalUsesMain() throws {
    XCTAssertEqual(MonitorGeometry.display(at: CGPoint(x: 80, y: 400), among: [left, right])?.id, 2)
    let anchor = try XCTUnwrap(MonitorAnchor(displayID: 2, centerX: 0.6, topY: 0.8))
    XCTAssertEqual(MonitorGeometry.display(for: anchor, among: [left], fallbackID: 1), left)
    let restored = MonitorGeometry.frame(
      size: CGSize(width: 390, height: 174), anchor: anchor, visible: left.visible)
    XCTAssertTrue(left.visible.contains(restored))
    XCTAssertNil(MonitorGeometry.display(for: anchor, among: [], fallbackID: 1))
  }
  func testNormalizedAnchorAdaptsToResolutionAndVisibleFrameChanges() throws {
    let anchor = try XCTUnwrap(MonitorAnchor(displayID: 2, centerX: 0.5, topY: 0.75))
    let first = MonitorGeometry.frame(
      size: CGSize(width: 200, height: 34), anchor: anchor, visible: right.visible)
    let resized = CGRect(x: 100, y: -170, width: 1600, height: 1000)
    let second = MonitorGeometry.frame(
      size: first.size, anchor: anchor, visible: resized)
    XCTAssertEqual(second.midX, resized.midX)
    XCTAssertEqual(second.maxY, resized.minY + resized.height * 0.75)
    XCTAssertNotEqual(second.origin, first.origin)
  }
  func testPersistenceValidatesSchemaCoordinatesAndDisplayID() throws {
    let name = "Llumi-notch-geometry-" + UUID().uuidString
    let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
    defer { defaults.removePersistentDomain(forName: name) }
    let anchor = try XCTUnwrap(MonitorAnchor(displayID: 2, centerX: 0.4, topY: 0.7))
    anchor.save(to: defaults)
    XCTAssertEqual(MonitorAnchor.load(from: defaults), anchor)
    for invalid in [
      #"{"schema":2,"displayID":2,"centerX":0.5,"topY":0.5}"#,
      #"{"schema":1,"displayID":0,"centerX":0.5,"topY":0.5}"#,
      #"{"schema":1,"displayID":2,"centerX":1.1,"topY":0.5}"#,
      #"{"schema":1,"displayID":2,"centerX":0.5,"topY":-0.1}"#,
      #"{"schema":1,"displayID":true,"centerX":0.5,"topY":0.5}"#,
      #"{"schema":1,"displayID":2,"centerX":"NaN","topY":0.5}"#,
    ] {
      defaults.set(Data(invalid.utf8), forKey: MonitorAnchor.defaultsKey)
      XCTAssertNil(MonitorAnchor.load(from: defaults))
    }
    for value in [Double.nan, .infinity, -.infinity] {
      XCTAssertNil(MonitorAnchor(displayID: 2, centerX: value, topY: 0.5))
      XCTAssertNil(MonitorAnchor(displayID: 2, centerX: 0.5, topY: value))
    }
    defaults.set("malformed", forKey: MonitorAnchor.defaultsKey)
    XCTAssertNil(MonitorAnchor.load(from: defaults))
    defaults.removeObject(forKey: MonitorAnchor.defaultsKey)
    XCTAssertNil(MonitorAnchor.load(from: defaults))
  }
}
