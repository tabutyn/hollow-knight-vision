import AppKit
import CoreGraphics
import MetalKit
import OSLog
import SwiftUI
import simd

struct LayerSceneDrawTile {
  let roomID: UUID
  let layerID: UUID
  let image: CGImage
  let bounds: CGRect
  let factor: Double
  let order: Int
  let roomPosition: CGPoint
  let roomScale: Double
  let isLiveOverlay: Bool
  let opacity: Double

  init(
    roomID: UUID, layerID: UUID, image: CGImage, bounds: CGRect, factor: Double,
    order: Int, roomPosition: CGPoint, roomScale: Double, isLiveOverlay: Bool = false,
    opacity: Double = 1
  ) {
    self.roomID = roomID
    self.layerID = layerID
    self.image = image
    self.bounds = bounds
    self.factor = factor
    self.order = order
    self.roomPosition = roomPosition
    self.roomScale = roomScale
    self.isLiveOverlay = isLiveOverlay
    self.opacity = opacity
  }
}

enum LayerSceneViewMode: String, CaseIterable { case room, world }

enum LayerSceneFramingMode: String, CaseIterable { case freeFly, fit, current }

enum LayerScenePointerPolicy {
  static func forwardsGamePointer(framingMode: LayerSceneFramingMode?) -> Bool {
    framingMode != .freeFly
  }
}

enum LayerSceneRoomPointerPhase { case began, changed, ended }

struct LayerSceneRoomRegion: Equatable, Identifiable {
  let id: UUID
  let bounds: CGRect
}

struct LayerSceneViewTransform: Equatable {
  var zoom: CGFloat
  var pan: CGPoint
}

final class LayerSceneViewportTransformStore {
  var transform = LayerSceneViewTransform(zoom: 1, pan: .zero)
}

/// Animates zoom and pan together; an interrupted transition starts at the
/// transform actually on screen, rather than jumping to its old destination.
struct LayerSceneViewportTransition {
  static let duration: TimeInterval = 0.36
  var start: LayerSceneViewTransform
  var target: LayerSceneViewTransform
  var startedAt: TimeInterval

  func value(at timestamp: TimeInterval) -> LayerSceneViewTransform {
    let progress = min(1, max(0, (timestamp - startedAt) / Self.duration))
    let eased = progress * progress * (3 - 2 * progress)
    return LayerSceneViewTransform(
      zoom: start.zoom + (target.zoom - start.zoom) * eased,
      pan: CGPoint(
        x: start.pan.x + (target.pan.x - start.pan.x) * eased,
        y: start.pan.y + (target.pan.y - start.pan.y) * eased))
  }

  func isFinished(at timestamp: TimeInterval) -> Bool {
    timestamp - startedAt >= Self.duration
  }
}

/// Follows a changing Fit target at display rate rather than snapping on each
/// atlas tile update. The time-based response is independent of capture cadence.
enum LayerSceneViewportFitFollower {
  static let responseTime: TimeInterval = 0.22

  static func value(
    from current: LayerSceneViewTransform,
    toward target: LayerSceneViewTransform,
    elapsed: TimeInterval
  ) -> LayerSceneViewTransform {
    guard elapsed.isFinite, elapsed > 0 else { return current }
    let alpha = CGFloat(1 - exp(-min(elapsed, 0.05) / responseTime))
    return LayerSceneViewTransform(
      zoom: current.zoom + (target.zoom - current.zoom) * alpha,
      pan: CGPoint(
        x: current.pan.x + (target.pan.x - current.pan.x) * alpha,
        y: current.pan.y + (target.pan.y - current.pan.y) * alpha
      )
    )
  }
}

/// Settles a screen-space translation without changing viewport zoom.
///
/// Callers supply monotonic-clock timestamps, which keeps this independent of
/// the display link and makes its behavior deterministic in tests.
struct LayerSceneTranslationSettler {
  static let defaultDuration: TimeInterval = 0.2

  private(set) var currentOffset: CGPoint
  private(set) var targetOffset: CGPoint
  let duration: TimeInterval
  private var startOffset: CGPoint
  private var startedAt: TimeInterval?
  private var lastSampledAt: TimeInterval?

  init(offset: CGPoint = .zero, duration: TimeInterval = defaultDuration) {
    let initial = Self.isFinite(offset) ? offset : .zero
    currentOffset = initial
    targetOffset = initial
    startOffset = initial
    self.duration = duration.isFinite ? max(0, duration) : 0
  }

  /// Starts a correction from the currently displayed offset.
  mutating func settle(to target: CGPoint, at timestamp: TimeInterval) {
    guard Self.isFinite(target), timestamp.isFinite else { return }
    let displayed = offset(at: timestamp)
    startOffset = displayed
    currentOffset = displayed
    targetOffset = target
    startedAt = lastSampledAt
  }

  /// Returns the displayed offset at `timestamp`, clamped to the target.
  mutating func offset(at timestamp: TimeInterval) -> CGPoint {
    guard timestamp.isFinite else { return currentOffset }
    let sampledAt = max(timestamp, lastSampledAt ?? timestamp)
    lastSampledAt = sampledAt
    guard let startedAt else { return currentOffset }
    guard duration > 0 else {
      currentOffset = targetOffset
      self.startedAt = nil
      return currentOffset
    }
    let progress = min(1, max(0, (sampledAt - startedAt) / duration))
    currentOffset = CGPoint(
      x: startOffset.x + (targetOffset.x - startOffset.x) * progress,
      y: startOffset.y + (targetOffset.y - startOffset.y) * progress)
    if progress == 1 { self.startedAt = nil }
    return currentOffset
  }

  private static func isFinite(_ point: CGPoint) -> Bool {
    point.x.isFinite && point.y.isFinite
  }
}

enum LayerSceneFeatureReviewMarkerKind: CaseIterable, Equatable {
  case cameraConsistentGround, groundFeature, occludedGroundFeature
  case groundFeatureDepthError, selectedGroundFeature, transitionDoor
}

enum LayerSceneFeatureReviewLineKind: CaseIterable, Equatable {
  case currentGround, persistentGround, roomTransition, roomBoundary
  case positiveGroundTruth, negativeGroundTruth, selectedGroundTruth
}

enum LayerSceneFeatureReviewCoordinateSpace: Equatable {
  case atlas, live
}

struct LayerSceneGroundFeatureDetails: Equatable {
  let segmentID: Int
  let sequenceIndex: Int
  /// Atlas-space center of the 16x12 reference patch.
  let worldPosition: CGPoint
  let referencePixels: [UInt8]
  let referenceOpacity: [UInt8]

  init(
    segmentID: Int,
    sequenceIndex: Int,
    worldPosition: CGPoint,
    referencePixels: [UInt8],
    referenceOpacity: [UInt8] = []
  ) {
    self.segmentID = segmentID
    self.sequenceIndex = sequenceIndex
    self.worldPosition = worldPosition
    self.referencePixels = referencePixels
    self.referenceOpacity = referenceOpacity
  }
}

enum LayerSceneFeatureReviewPalette {
  static func color(for kind: LayerSceneFeatureReviewMarkerKind) -> Color {
    let value = rgba(for: kind)
    return Color(
      red: Double(value.x), green: Double(value.y), blue: Double(value.z),
      opacity: Double(value.w))
  }

  static func color(for kind: LayerSceneFeatureReviewLineKind) -> Color {
    let value = rgba(for: kind)
    return Color(
      red: Double(value.x), green: Double(value.y), blue: Double(value.z),
      opacity: Double(value.w))
  }

  static func rgba(for kind: LayerSceneFeatureReviewMarkerKind) -> SIMD4<Float> {
    switch kind {
    case .cameraConsistentGround: SIMD4<Float>(0.27, 1, 0.47, 0.95)
    case .groundFeature: SIMD4<Float>(0.75, 0.31, 1, 0.95)
    case .occludedGroundFeature: SIMD4<Float>(1, 0.50, 0, 0.95)
    case .groundFeatureDepthError: SIMD4<Float>(1, 0.24, 0.24, 0.95)
    case .selectedGroundFeature: SIMD4<Float>(1, 1, 1, 1)
    case .transitionDoor: SIMD4<Float>(0.12, 0.90, 1, 0.95)
    }
  }

  static func rgba(for kind: LayerSceneFeatureReviewLineKind) -> SIMD4<Float> {
    switch kind {
    case .currentGround: SIMD4<Float>(0.27, 1, 0.47, 0.95)
    case .persistentGround: SIMD4<Float>(0.75, 0.31, 1, 0.86)
    case .roomTransition: SIMD4<Float>(1, 0.52, 0.12, 0.95)
    case .roomBoundary: SIMD4<Float>(0.12, 0.90, 1, 0.95)
    case .positiveGroundTruth: SIMD4<Float>(0.27, 1, 0.47, 1)
    case .negativeGroundTruth: SIMD4<Float>(1, 0.18, 0.18, 1)
    case .selectedGroundTruth: SIMD4<Float>(1, 1, 1, 1)
    }
  }
}

struct LayerSceneFeatureReviewOverlay {
  struct Marker {
    var worldPosition: CGPoint
    var kind: LayerSceneFeatureReviewMarkerKind
    /// Optional atlas-space dimensions. Persistent ground cells use this to
    /// remain true 16x12 world cells instead of fixed-size screen markers.
    var worldSize: CGSize?
    var groundFeatureDetails: LayerSceneGroundFeatureDetails?
    var coordinateSpace: LayerSceneFeatureReviewCoordinateSpace
    var isVisible: Bool
    var opacity: Float

    init(
      worldPosition: CGPoint,
      kind: LayerSceneFeatureReviewMarkerKind,
      worldSize: CGSize? = nil,
      groundFeatureDetails: LayerSceneGroundFeatureDetails? = nil,
      coordinateSpace: LayerSceneFeatureReviewCoordinateSpace = .atlas,
      isVisible: Bool = true,
      opacity: Float = 1
    ) {
      self.worldPosition = worldPosition
      self.kind = kind
      self.worldSize = worldSize
      self.groundFeatureDetails = groundFeatureDetails
      self.coordinateSpace = coordinateSpace
      self.isVisible = isVisible
      self.opacity = min(1, max(0, opacity))
    }
  }
  struct Line {
    var start: CGPoint
    var end: CGPoint
    var kind: LayerSceneFeatureReviewLineKind
    var coordinateSpace: LayerSceneFeatureReviewCoordinateSpace = .atlas
  }
  var markers: [Marker]
  var lines: [Line]
}

struct LayerSceneOverlayQuad {
  var kind: LayerSceneFeatureReviewMarkerKind?
  var lineKind: LayerSceneFeatureReviewLineKind?
  /// Screen-space points in Metal triangle-strip order.
  var points: [CGPoint]
  var opacity: Float = 1
}

enum LayerSceneFeatureReviewGeometry {
  static let maximumMarkers = 2_000
  static let maximumLines = 4_096
  static let markerSize: CGFloat = 8
  static let lineWidth: CGFloat = 1

  static func screenPoint(world: CGPoint, zoom: CGFloat, pan: CGPoint) -> CGPoint? {
    guard world.x.isFinite, world.y.isFinite, zoom.isFinite, zoom > 0,
      pan.x.isFinite, pan.y.isFinite
    else { return nil }
    let point = CGPoint(x: world.x * zoom + pan.x, y: world.y * zoom + pan.y)
    return point.x.isFinite && point.y.isFinite ? point : nil
  }

  static func markerQuads(
    _ overlay: LayerSceneFeatureReviewOverlay?, zoom: CGFloat, pan: CGPoint
  ) -> [LayerSceneOverlayQuad] {
    guard let overlay else { return [] }
    let valid = overlay.markers.lazy.filter(\.isVisible).compactMap { marker in
      screenPoint(world: marker.worldPosition, zoom: zoom, pan: pan).map { (marker, $0) }
    }.prefix(maximumMarkers)
    return valid.flatMap { item -> [LayerSceneOverlayQuad] in
      let (marker, center) = item
      let halfWidth = marker.worldSize.map { max(1, $0.width * zoom * 0.5) }
        ?? markerSize * 0.5
      let halfHeight = marker.worldSize.map { max(1, $0.height * zoom * 0.5) }
        ?? markerSize * 0.5
      let thickness = marker.kind == .selectedGroundFeature ? lineWidth + 1 : lineWidth
      let left = center.x - halfWidth, right = center.x + halfWidth
      let bottom = center.y - halfHeight, top = center.y + halfHeight
      return [
        markerQuad(marker.kind, marker.opacity, left, bottom, right, bottom + thickness),
        markerQuad(marker.kind, marker.opacity, left, top - thickness, right, top),
        markerQuad(marker.kind, marker.opacity, left, bottom + thickness, left + thickness, top - thickness),
        markerQuad(marker.kind, marker.opacity, right - thickness, bottom + thickness, right, top - thickness),
      ]
    }
  }

  static func groundFeature(
    at screenPoint: CGPoint,
    in overlay: LayerSceneFeatureReviewOverlay?,
    zoom: CGFloat,
    atlasPan: CGPoint,
    livePan: CGPoint
  ) -> LayerSceneGroundFeatureDetails? {
    guard let overlay, zoom.isFinite, zoom > 0 else { return nil }
    for marker in overlay.markers.reversed() {
      guard let details = marker.groundFeatureDetails,
        let center = self.screenPoint(
          world: marker.worldPosition,
          zoom: zoom,
          pan: marker.coordinateSpace == .atlas ? atlasPan : livePan
        )
      else { continue }
      let halfWidth = max(
        6,
        (marker.worldSize?.width ?? markerSize) * zoom * 0.5
      )
      let halfHeight = max(
        6,
        (marker.worldSize?.height ?? markerSize) * zoom * 0.5
      )
      if abs(screenPoint.x - center.x) <= halfWidth,
        abs(screenPoint.y - center.y) <= halfHeight {
        return details
      }
    }
    return nil
  }

  private static func markerQuad(
    _ kind: LayerSceneFeatureReviewMarkerKind,
    _ opacity: Float,
    _ left: CGFloat,
    _ bottom: CGFloat,
    _ right: CGFloat,
    _ top: CGFloat
  ) -> LayerSceneOverlayQuad {
    LayerSceneOverlayQuad(kind: kind, lineKind: nil, points: [
      CGPoint(x: left, y: bottom), CGPoint(x: right, y: bottom),
      CGPoint(x: left, y: top), CGPoint(x: right, y: top),
    ], opacity: opacity)
  }

  static func lineQuads(
    _ overlay: LayerSceneFeatureReviewOverlay?, zoom: CGFloat, pan: CGPoint
  ) -> [LayerSceneOverlayQuad] {
    guard let overlay else { return [] }
    return overlay.lines.lazy.compactMap { line in
      guard let start = screenPoint(world: line.start, zoom: zoom, pan: pan),
        let end = screenPoint(world: line.end, zoom: zoom, pan: pan)
      else { return nil }
      let dx = end.x - start.x, dy = end.y - start.y
      let length = hypot(dx, dy)
      guard length.isFinite, length > .leastNonzeroMagnitude else { return nil }
      let offset = CGPoint(x: -dy / length * lineWidth * 0.5, y: dx / length * lineWidth * 0.5)
      return LayerSceneOverlayQuad(kind: nil, lineKind: line.kind, points: [
        CGPoint(x: start.x - offset.x, y: start.y - offset.y),
        CGPoint(x: start.x + offset.x, y: start.y + offset.y),
        CGPoint(x: end.x - offset.x, y: end.y - offset.y),
        CGPoint(x: end.x + offset.x, y: end.y + offset.y),
      ], opacity: 1)
    }.prefix(maximumLines).map { $0 }
  }
}

enum LayerSceneProjection {
  static func isFinite(_ rect: CGRect) -> Bool {
    rect.minX.isFinite && rect.minY.isFinite && rect.width.isFinite && rect.height.isFinite
      && rect.maxX.isFinite && rect.maxY.isFinite && rect.midX.isFinite && rect.midY.isFinite
      && rect.width > 0 && rect.height > 0
  }

  static func bounds(
    for tiles: [LayerSceneDrawTile], mode: LayerSceneViewMode, camera: CGPoint
  ) -> CGRect? {
    let frames = tiles.compactMap { tile -> CGRect? in
      let frame = project(tile, mode: mode, camera: camera)
      return isFinite(frame) ? frame : nil
    }
    guard let first = frames.first else { return nil }
    let combined = frames.dropFirst().reduce(first) { $0.union($1) }
    return isFinite(combined) ? combined : nil
  }

  static func project(
    _ tile: LayerSceneDrawTile, mode: LayerSceneViewMode, camera: CGPoint
  ) -> CGRect {
    switch mode {
    case .room:
      return tile.bounds.offsetBy(
        dx: -CGFloat(tile.factor) * camera.x, dy: -CGFloat(tile.factor) * camera.y)
    case .world:
      return CGRect(
        x: tile.roomPosition.x + tile.bounds.minX * tile.roomScale,
        y: tile.roomPosition.y + tile.bounds.minY * tile.roomScale,
        width: tile.bounds.width * tile.roomScale, height: tile.bounds.height * tile.roomScale)

    }
  }

  static func unproject(
    _ point: CGPoint, tile: LayerSceneDrawTile, mode: LayerSceneViewMode, camera: CGPoint
  ) -> CGPoint? {
    let projected = project(tile, mode: mode, camera: camera)
    guard isFinite(projected), point.x.isFinite, point.y.isFinite, projected.contains(point) else {
      return nil
    }
    let u = (point.x - projected.minX) / projected.width
    let v = (point.y - projected.minY) / projected.height
    return CGPoint(
      x: tile.bounds.minX + u * tile.bounds.width, y: tile.bounds.minY + v * tile.bounds.height)
  }
}

struct LayerSceneViewport: NSViewRepresentable {
  let tiles: [LayerSceneDrawTile]
  let mode: LayerSceneViewMode
  let cameraPosition: CGPoint
  let hiddenLayerIDs: Set<UUID>
  let selectedLayerID: UUID?


  let fitRequest: Int
  let focusPoint: CGPoint?
  let autoFollow: Bool
  var framingMode: LayerSceneFramingMode? = nil
  var framingRequest: Int = 0
  var transformStore: LayerSceneViewportTransformStore? = nil
  var fitBounds: CGRect? = nil
  var followBounds: CGRect? = nil
  var followRequest: Int = 0
  let onSelect: (UUID, UUID, CGPoint) -> Void
  let onInteraction: () -> Void
  var onGamePointerActivity: () -> Void = {}
  var onGamePointerEvent: (GamePointerEvent) -> Bool = { _ in false }
  var onGroundFeatureSelect: (LayerSceneGroundFeatureDetails) -> Void = { _ in }
  var groundLabelEditingEnabled = false
  var onGroundLabelPointer: (LayerSceneGroundLabelPointerPhase, CGPoint, CGFloat) -> Void = {
    _, _, _ in
  }
  var roomEditingEnabled = false
  var roomRegions = [LayerSceneRoomRegion]()
  var onRoomPointer: (LayerSceneRoomPointerPhase, UUID, CGPoint) -> Void = {
    _, _, _ in
  }
  var featureReviewOverlay: LayerSceneFeatureReviewOverlay? = nil
  var showsCheckerboardBackground = true
  var worldBasisOffset: CGPoint = .zero

  static func shouldFit(
    fitRequestChanged: Bool, modeChanged: Bool, presentsInitialContent: Bool
  ) -> Bool {
    fitRequestChanged || modeChanged || presentsInitialContent
  }

  func makeCoordinator() -> Coordinator { Coordinator() }
  func makeNSView(context: Context) -> LayerMetalView {
    let view = LayerMetalView()
    view.coordinator = context.coordinator
    context.coordinator.onSelect = onSelect
    context.coordinator.onInteraction = onInteraction
    context.coordinator.onGamePointerActivity = onGamePointerActivity
    context.coordinator.onGamePointerEvent = onGamePointerEvent
    context.coordinator.onGroundFeatureSelect = onGroundFeatureSelect
    context.coordinator.groundLabelEditingEnabled = groundLabelEditingEnabled
    context.coordinator.onGroundLabelPointer = onGroundLabelPointer
    context.coordinator.roomEditingEnabled = roomEditingEnabled
    context.coordinator.roomRegions = roomRegions
    context.coordinator.onRoomPointer = onRoomPointer
    context.coordinator.transformStore = transformStore
    if framingMode == .freeFly, let transformStore {
      context.coordinator.apply(transformStore.transform)
    }
    context.coordinator.featureReviewOverlay = featureReviewOverlay
    context.coordinator.showsCheckerboardBackground = showsCheckerboardBackground
    context.coordinator.setInitialWorldBasisOffset(worldBasisOffset)
    view.setAccessibilityElement(true)
    view.setAccessibilityRole(.group)
    view.setAccessibilityLabel(groundLabelEditingEnabled ? "Ground line atlas" : "Debug atlas")
    return view
  }
  func updateNSView(_ view: LayerMetalView, context: Context) {
    let c = context.coordinator
    let priorMode = c.mode
    c.tiles = tiles.filter { !hiddenLayerIDs.contains($0.layerID) }
    c.mode = mode
    c.camera = cameraPosition


    c.focusPoint = focusPoint
    c.fitBounds = fitBounds
    c.followBounds = followBounds
    c.transformStore = transformStore
    c.showsCheckerboardBackground = showsCheckerboardBackground
    c.groundLabelEditingEnabled = groundLabelEditingEnabled
    c.onGroundLabelPointer = onGroundLabelPointer
    c.roomEditingEnabled = roomEditingEnabled
    c.roomRegions = roomRegions
    c.onRoomPointer = onRoomPointer
    view.setAccessibilityLabel(groundLabelEditingEnabled ? "Ground line atlas" : "Debug atlas")
    if let framingMode {
      c.onSelect = onSelect
      c.onInteraction = onInteraction
      c.onGamePointerEvent = onGamePointerEvent
      c.onGroundFeatureSelect = onGroundFeatureSelect
      c.featureReviewOverlay = featureReviewOverlay
      c.updateWorldBasisOffset(worldBasisOffset, at: ProcessInfo.processInfo.systemUptime)
      c.setFraming(
        framingMode, request: framingRequest, in: view.bounds.size,
        at: ProcessInfo.processInfo.systemUptime)
      view.needsDisplay = true
      return
    }
    let followRequested = c.followRequest != followRequest
    c.followRequest = followRequest
    c.setAutoFollow(autoFollow)
    c.onSelect = onSelect
    c.onInteraction = onInteraction
    c.onGamePointerActivity = onGamePointerActivity
    c.onGamePointerEvent = onGamePointerEvent
    c.onGroundFeatureSelect = onGroundFeatureSelect
    c.featureReviewOverlay = featureReviewOverlay
    c.updateWorldBasisOffset(worldBasisOffset, at: ProcessInfo.processInfo.systemUptime)
    if Self.shouldFit(
      fitRequestChanged: c.fitRequest != fitRequest,
      modeChanged: priorMode != mode,
      presentsInitialContent: c.didReceiveTiles == false
    ) {
      c.fitRequest = fitRequest
      c.didReceiveTiles = c.projectedBounds != nil
      c.hasManualViewportTransform = false
      c.fit(in: view.bounds.size)
    }
    if followRequested { c.hasManualViewportTransform = false }
    if c.autoFollow, !c.hasManualViewportTransform, let focus = c.focusPoint,
       focus.x.isFinite, focus.y.isFinite {
      if let followBounds = c.followBounds {
        c.frame(followBounds, centeredOn: focus, in: view.bounds.size)
      } else {
        c.center(on: focus, in: view.bounds.size)
      }
    }
    view.needsDisplay = true
  }

  final class Coordinator {
    var tiles = [LayerSceneDrawTile]()
    var mode: LayerSceneViewMode = .room
    var camera = CGPoint.zero


    var zoom: CGFloat = 1
    var pan = CGPoint.zero
    var fitRequest = -1
    var followRequest = -1
    var didReceiveTiles = false
    var hasManualViewportTransform = false
    var focusPoint: CGPoint?
    var fitBounds: CGRect?
    var followBounds: CGRect?
    var autoFollow = false
    var framingMode: LayerSceneFramingMode?
    var framingRequest = -1
    var currentZoomMultiplier: CGFloat = 1
    var currentFocusAnchor = CGPoint(x: 0.5, y: 0.5)
    var viewportTransition: LayerSceneViewportTransition?
    var fitFollowTarget: LayerSceneViewTransform?
    var fitFollowLastAt: TimeInterval?
    var transformStore: LayerSceneViewportTransformStore?
    var featureReviewOverlay: LayerSceneFeatureReviewOverlay?
    var showsCheckerboardBackground = true
    var authoritativeWorldBasisOffset = CGPoint.zero
    var worldBasisSettler = LayerSceneTranslationSettler()
    var didReceiveWorldBasisOffset = false
    var onSelect: (UUID, UUID, CGPoint) -> Void = { _, _, _ in }
    var onInteraction: () -> Void = {}
    var onGamePointerActivity: () -> Void = {}
    var onGamePointerEvent: (GamePointerEvent) -> Bool = { _ in false }
    var onGroundFeatureSelect: (LayerSceneGroundFeatureDetails) -> Void = { _ in }
    var groundLabelEditingEnabled = false
    var onGroundLabelPointer: (LayerSceneGroundLabelPointerPhase, CGPoint, CGFloat) -> Void = {
      _, _, _ in
    }
    var roomEditingEnabled = false
    var roomRegions = [LayerSceneRoomRegion]()
    var onRoomPointer: (LayerSceneRoomPointerPhase, UUID, CGPoint) -> Void = {
      _, _, _ in
    }
    var projectedBounds: CGRect? {
      LayerSceneProjection.bounds(
        for: tiles, mode: mode, camera: camera)
    }
    func setAutoFollow(_ enabled: Bool) {
      if enabled && !autoFollow {
        hasManualViewportTransform = false
      }
      autoFollow = enabled
    }
    var transform: LayerSceneViewTransform {
      LayerSceneViewTransform(zoom: zoom, pan: pan)
    }
    func apply(_ transform: LayerSceneViewTransform) {
      zoom = transform.zoom
      pan = transform.pan
      transformStore?.transform = transform
    }
    func cancelViewportTransition() {
      advanceViewportTransition(at: ProcessInfo.processInfo.systemUptime)
      viewportTransition = nil
    }
    func advanceViewportTransition(at timestamp: TimeInterval) {
      if let viewportTransition {
        apply(viewportTransition.value(at: timestamp))
        if viewportTransition.isFinished(at: timestamp) {
          self.viewportTransition = nil
          fitFollowLastAt = timestamp
        }
        return
      }
      guard let fitFollowTarget else { return }
      let elapsed = fitFollowLastAt.map { timestamp - $0 } ?? 0
      fitFollowLastAt = timestamp
      apply(LayerSceneViewportFitFollower.value(
        from: transform, toward: fitFollowTarget, elapsed: elapsed
      ))
    }
    func targetTransform(for framing: LayerSceneFramingMode, in size: CGSize)
      -> LayerSceneViewTransform? {
      guard size.width.isFinite, size.height.isFinite, size.width > 1, size.height > 1
      else { return nil }
      switch framing {
      case .freeFly:
        return nil
      case .fit:
        guard let bounds = effectiveFitBounds else { return nil }
        let scale = max(0.08, min(12,
          min(size.width / bounds.width, size.height / bounds.height) * 0.9))
        return LayerSceneViewTransform(zoom: scale, pan: CGPoint(
          x: size.width * 0.5 - bounds.midX * scale,
          y: size.height * 0.5 - bounds.midY * scale))
      case .current:
        guard let focus = focusPoint, focus.x.isFinite, focus.y.isFinite,
          let bounds = followBounds, LayerSceneProjection.isFinite(bounds)
        else { return nil }
        let baseScale = min(size.width / bounds.width, size.height / bounds.height) * 0.88
        let scale = max(0.08, min(12, baseScale * currentZoomMultiplier))
        return LayerSceneViewTransform(zoom: scale, pan: CGPoint(
          x: size.width * currentFocusAnchor.x - focus.x * scale,
          y: size.height * currentFocusAnchor.y - focus.y * scale))
      }
    }
    var effectiveFitBounds: CGRect? {
      if let fitBounds, LayerSceneProjection.isFinite(fitBounds),
        fitBounds.width > 0, fitBounds.height > 0 {
        return fitBounds
      }
      return projectedBounds
    }
    func setFraming(
      _ framing: LayerSceneFramingMode, request: Int, in size: CGSize,
      at timestamp: TimeInterval
    ) {
      let changed = framingMode != framing || framingRequest != request
      if changed, framing == .current {
        currentZoomMultiplier = 1
        currentFocusAnchor = CGPoint(x: 0.5, y: 0.5)
      }
      framingMode = framing
      framingRequest = request
      if framing == .freeFly {
        if changed { cancelViewportTransition() }
        fitFollowTarget = nil
        fitFollowLastAt = nil
        transformStore?.transform = transform
        return
      }
      guard let target = targetTransform(for: framing, in: size) else { return }
      if fitFollowTarget != nil, viewportTransition == nil {
        // Finish the old target's elapsed time before installing new bounds.
        // Otherwise a delayed atlas update spends that time on the new target
        // and creates a visible first-frame jump.
        advanceViewportTransition(at: timestamp)
      }
      fitFollowTarget = framing == .fit ? target : nil
      if framing != .fit { fitFollowLastAt = nil }
      if !didReceiveTiles {
        // Initial camera framing should be immediate; only button presses animate.
        apply(target)
        didReceiveTiles = true
        fitFollowLastAt = timestamp
        return
      }
      if changed {
        advanceViewportTransition(at: timestamp)
        viewportTransition = LayerSceneViewportTransition(
          start: transform, target: target, startedAt: timestamp)
      } else if viewportTransition != nil {
        viewportTransition?.target = target
        advanceViewportTransition(at: timestamp)
      } else if framing != .fit {
        apply(target)
      }
    }
    func zoom(by factor: CGFloat, around screenPoint: CGPoint) {
      guard factor.isFinite, factor > 0, screenPoint.x.isFinite, screenPoint.y.isFinite,
        zoom.isFinite, zoom > 0, pan.x.isFinite, pan.y.isFinite
      else { return }
      let priorZoom = zoom
      let nextZoom = max(0.08, min(12, priorZoom * factor))
      let worldPoint = CGPoint(
        x: (screenPoint.x - pan.x) / priorZoom,
        y: (screenPoint.y - pan.y) / priorZoom
      )
      zoom = nextZoom
      pan = CGPoint(
        x: screenPoint.x - worldPoint.x * nextZoom,
        y: screenPoint.y - worldPoint.y * nextZoom
      )
    }
    /// Current remains attached to the moving live viewport. Scrolling changes
    /// zoom while the live-frame focus stays at the viewport center.
    func zoomCurrent(by factor: CGFloat, around screenPoint: CGPoint, in size: CGSize) {
      guard framingMode == .current,
        let focus = focusPoint, focus.x.isFinite, focus.y.isFinite,
        let bounds = followBounds, LayerSceneProjection.isFinite(bounds),
        size.width > 0, size.height > 0
      else { return }
      zoom(by: factor, around: screenPoint)
      let baseScale = min(size.width / bounds.width, size.height / bounds.height) * 0.88
      guard baseScale.isFinite, baseScale > 0 else { return }
      currentZoomMultiplier = zoom / baseScale
      currentFocusAnchor = CGPoint(x: 0.5, y: 0.5)
      pan = CGPoint(
        x: size.width * 0.5 - focus.x * zoom,
        y: size.height * 0.5 - focus.y * zoom
      )
      transformStore?.transform = transform
    }
    func setInitialWorldBasisOffset(_ offset: CGPoint) {
      guard offset.x.isFinite, offset.y.isFinite else { return }
      authoritativeWorldBasisOffset = offset
      worldBasisSettler = LayerSceneTranslationSettler(offset: offset)
      didReceiveWorldBasisOffset = true
    }
    func updateWorldBasisOffset(_ offset: CGPoint, at timestamp: TimeInterval) {
      guard offset.x.isFinite, offset.y.isFinite else { return }
      guard didReceiveWorldBasisOffset else {
        setInitialWorldBasisOffset(offset)
        return
      }
      guard offset != authoritativeWorldBasisOffset else { return }
      authoritativeWorldBasisOffset = offset
      worldBasisSettler.settle(to: offset, at: timestamp)
    }
    func atlasPan(at timestamp: TimeInterval) -> CGPoint {
      let displayed = worldBasisSettler.offset(at: timestamp)
      return CGPoint(
        x: pan.x + (displayed.x - authoritativeWorldBasisOffset.x) * zoom,
        y: pan.y + (displayed.y - authoritativeWorldBasisOffset.y) * zoom
      )
    }
    func fit(in size: CGSize) {
      guard size.width.isFinite, size.height.isFinite, size.width > 1, size.height > 1 else { return }
      guard let bounds = effectiveFitBounds else {
        zoom = 1
        pan = .zero
        return
      }
      zoom = max(0.08, min(12, min(size.width / bounds.width, size.height / bounds.height) * 0.9))
      pan = CGPoint(
        x: size.width * 0.5 - bounds.midX * zoom, y: size.height * 0.5 - bounds.midY * zoom)
    }
    func center(on point: CGPoint, in size: CGSize) {
      guard zoom.isFinite, zoom > 0, size.width > 0, size.height > 0 else { return }
      pan = CGPoint(
        x: size.width * 0.5 - point.x * zoom,
        y: size.height * 0.5 - point.y * zoom
      )
    }
    func frame(
      _ bounds: CGRect, centeredOn point: CGPoint, in size: CGSize,
      surroundingScale: CGFloat = 0.88
    ) {
      guard LayerSceneProjection.isFinite(bounds), size.width > 0, size.height > 0,
        surroundingScale.isFinite, surroundingScale > 0
      else { return }
      zoom = max(
        0.08,
        min(12, min(size.width / bounds.width, size.height / bounds.height) * surroundingScale)
      )
      center(on: point, in: size)
    }
    var displayedLiveFrame: CGRect? {
      guard let liveTile = tiles.last(where: \.isLiveOverlay) else { return nil }
      let projected = LayerSceneProjection.project(
        liveTile,
        mode: mode,
        camera: camera
      )
      guard LayerSceneProjection.isFinite(projected) else { return nil }
      return CGRect(
        x: projected.minX * zoom + pan.x,
        y: projected.minY * zoom + pan.y,
        width: projected.width * zoom,
        height: projected.height * zoom
      )
    }
  }
}

final class LayerMetalView: MTKView, MTKViewDelegate {
  override var isOpaque: Bool { false }
  weak var coordinator: LayerSceneViewport.Coordinator?
  private var renderer: LayerMetalRenderer?
  private let performanceLog = Logger(
    subsystem: "com.ballroller.hollow-knight-vision", category: "presentation")
  private let pointerLog = Logger(
    subsystem: "com.ballroller.hollow-knight-vision", category: "game-pointer")
  private var submittedFrameCount = 0
  private var submissionWindowStart = ProcessInfo.processInfo.systemUptime
  private var drag: CGPoint?
  private var dragStart: CGPoint?
  private var dragged = false
  private var passingPrimaryMouse = false
  private var selectedGroundFeature = false
  private var editingGroundLabel = false
  private var editingRoomID: UUID?
  private var pointerTrackingArea: NSTrackingArea?
  private var pointerWithinLiveFrame = false
  private var lastForwardedPointer: CGPoint?
  private var lastPointerDiagnosticAt: TimeInterval = 0
  required init(coder: NSCoder) {
    super.init(coder: coder)
    configure()
  }
  override init(frame frameRect: NSRect, device: MTLDevice?) {
    super.init(frame: frameRect, device: device)
    configure()
  }
  convenience init() { self.init(frame: .zero, device: MTLCreateSystemDefaultDevice()) }
  private func configure() {
    device = device ?? MTLCreateSystemDefaultDevice()
    colorPixelFormat = .bgra8Unorm
    clearColor = MTLClearColorMake(0, 0, 0, 0)
    layer?.isOpaque = false
    framebufferOnly = false
    // Two slots reduced measured presentation latency by one display frame.
    // Keep VSync while limiting queued presentation work.
    if let metalLayer = layer as? CAMetalLayer {
      metalLayer.maximumDrawableCount = 2
    }
    // SwiftUI only publishes when capture produces a new image.  Keep the
    // Metal presentation clock alive between those publications so a sparse
    // ScreenCaptureKit stream does not also pause canvas drawing, panning, or
    // window compositing.
    enableSetNeedsDisplay = false
    preferredFramesPerSecond = 60
    isPaused = false
    delegate = self
    renderer = device.flatMap { try? LayerMetalRenderer($0) }
  }
  override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
  override func updateTrackingAreas() {
    if let pointerTrackingArea { removeTrackingArea(pointerTrackingArea) }
    let area = NSTrackingArea(
      rect: bounds,
      options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
      owner: self,
      userInfo: nil
    )
    addTrackingArea(area)
    pointerTrackingArea = area
    super.updateTrackingAreas()
  }
  func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
    guard let coordinator else { return }
    if let framingMode = coordinator.framingMode {
      coordinator.setFraming(
        framingMode, request: coordinator.framingRequest, in: view.bounds.size,
        at: ProcessInfo.processInfo.systemUptime)
    } else if !coordinator.hasManualViewportTransform {
      coordinator.fit(in: view.bounds.size)
    }
  }
  func draw(in view: MTKView) {
    coordinator?.advanceViewportTransition(at: ProcessInfo.processInfo.systemUptime)
    guard renderer?.draw(in: view, coordinator: coordinator) == true else { return }
    submittedFrameCount += 1
    let now = ProcessInfo.processInfo.systemUptime
    let elapsed = now - submissionWindowStart
    guard elapsed >= 1 else { return }
    let framesPerSecond = Double(submittedFrameCount) / elapsed
    performanceLog.notice("metalSubmitFPS=\(framesPerSecond, privacy: .public)")
    submittedFrameCount = 0
    submissionWindowStart = now
  }
  override func scrollWheel(with event: NSEvent) {
    guard let coordinator else { return }
    coordinator.cancelViewportTransition()
    let cursor = convert(event.locationInWindow, from: nil)
    let factor = pow(1.001, event.scrollingDeltaY)
    if coordinator.framingMode == .current {
      coordinator.zoomCurrent(by: factor, around: cursor, in: bounds.size)
      coordinator.hasManualViewportTransform = false
    } else {
      coordinator.zoom(by: factor, around: cursor)
      coordinator.hasManualViewportTransform = true
      coordinator.onInteraction()
    }
    coordinator.transformStore?.transform = coordinator.transform
    needsDisplay = true
  }
  override func mouseDown(with event: NSEvent) {
    let location = convert(event.locationInWindow, from: nil)
    if !event.modifierFlags.contains(.shift) {
      if let coordinator, coordinator.roomEditingEnabled,
         coordinator.zoom.isFinite, coordinator.zoom > 0 {
        let pan = coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime)
        let point = CGPoint(
          x: (location.x - pan.x) / coordinator.zoom,
          y: (location.y - pan.y) / coordinator.zoom
        )
        if let room = coordinator.roomRegions
          .filter({ $0.bounds.contains(point) })
          .min(by: { roomEdgeDistance(point, $0.bounds) < roomEdgeDistance(point, $1.bounds) }) {
          editingRoomID = room.id
          editingGroundLabel = false
          passingPrimaryMouse = false
          selectedGroundFeature = false
          drag = nil
          dragStart = nil
          dragged = false
          coordinator.onRoomPointer(.began, room.id, point)
          needsDisplay = true
          return
        }
      }
      if let coordinator, coordinator.groundLabelEditingEnabled,
         coordinator.zoom.isFinite, coordinator.zoom > 0 {
        let pan = coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime)
        let point = CGPoint(
          x: (location.x - pan.x) / coordinator.zoom,
          y: (location.y - pan.y) / coordinator.zoom
        )
        editingGroundLabel = true
        passingPrimaryMouse = false
        selectedGroundFeature = false
        drag = nil
        dragStart = nil
        dragged = false
        coordinator.onGroundLabelPointer(.began, point, 8 / coordinator.zoom)
        needsDisplay = true
        return
      }
      if let coordinator,
        let feature = LayerSceneFeatureReviewGeometry.groundFeature(
          at: location,
          in: coordinator.featureReviewOverlay,
          zoom: coordinator.zoom,
          atlasPan: coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime),
          livePan: coordinator.pan
        ) {
        selectedGroundFeature = true
        passingPrimaryMouse = false
        drag = nil
        dragStart = nil
        dragged = false
        coordinator.onGroundFeatureSelect(feature)
        return
      }
      passingPrimaryMouse = forwardPointer(.leftDown, at: location, event: event)
      // The live frame owns ordinary clicks. The empty canvas owns drags,
      // so a panned-away game remains recoverable without a modifier key.
      drag = passingPrimaryMouse ? nil : location
      dragStart = drag
      dragged = false
      return
    }
    drag = location
    dragStart = drag
    dragged = false
  }
  override func mouseDragged(with event: NSEvent) {
    if let editingRoomID, let coordinator,
       coordinator.zoom.isFinite, coordinator.zoom > 0 {
      let location = convert(event.locationInWindow, from: nil)
      let pan = coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime)
      let point = CGPoint(
        x: (location.x - pan.x) / coordinator.zoom,
        y: (location.y - pan.y) / coordinator.zoom
      )
      coordinator.onRoomPointer(.changed, editingRoomID, point)
      needsDisplay = true
      return
    }
    if editingGroundLabel, let coordinator,
       coordinator.zoom.isFinite, coordinator.zoom > 0 {
      let location = convert(event.locationInWindow, from: nil)
      let pan = coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime)
      let point = CGPoint(
        x: (location.x - pan.x) / coordinator.zoom,
        y: (location.y - pan.y) / coordinator.zoom
      )
      coordinator.onGroundLabelPointer(.changed, point, 8 / coordinator.zoom)
      needsDisplay = true
      return
    }
    if passingPrimaryMouse {
      _ = forwardPointer(
        .leftDragged,
        at: convert(event.locationInWindow, from: nil),
        event: event
      )
      return
    }
    guard let previous = drag, let start = dragStart, let c = coordinator else { return }
    let now = convert(event.locationInWindow, from: nil)
    if hypot(now.x - start.x, now.y - start.y) > 4 { dragged = true }
    c.cancelViewportTransition()
    c.pan.x += now.x - previous.x
    c.pan.y += now.y - previous.y
    drag = now
    c.hasManualViewportTransform = true
    c.transformStore?.transform = c.transform
    c.onInteraction()
    needsDisplay = true
  }
  override func mouseUp(with event: NSEvent) {
    if let editingRoomID {
      self.editingRoomID = nil
      if let coordinator, coordinator.zoom.isFinite, coordinator.zoom > 0 {
        let location = convert(event.locationInWindow, from: nil)
        let pan = coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime)
        let point = CGPoint(
          x: (location.x - pan.x) / coordinator.zoom,
          y: (location.y - pan.y) / coordinator.zoom
        )
        coordinator.onRoomPointer(.ended, editingRoomID, point)
        needsDisplay = true
      }
      return
    }
    if editingGroundLabel {
      editingGroundLabel = false
      if let coordinator, coordinator.zoom.isFinite, coordinator.zoom > 0 {
        let location = convert(event.locationInWindow, from: nil)
        let pan = coordinator.atlasPan(at: ProcessInfo.processInfo.systemUptime)
        let point = CGPoint(
          x: (location.x - pan.x) / coordinator.zoom,
          y: (location.y - pan.y) / coordinator.zoom
        )
        coordinator.onGroundLabelPointer(.ended, point, 8 / coordinator.zoom)
        needsDisplay = true
      }
      return
    }
    if selectedGroundFeature {
      selectedGroundFeature = false
      return
    }
    if passingPrimaryMouse {
      _ = forwardPointer(
        .leftUp,
        at: convert(event.locationInWindow, from: nil),
        event: event,
        fallbackPoint: lastForwardedPointer
      )
      passingPrimaryMouse = false
      return
    }
    guard !dragged, let c = coordinator else { return }
    let p = convert(event.locationInWindow, from: nil)
    let screen = CGPoint(x: (p.x - c.pan.x) / c.zoom, y: (p.y - c.pan.y) / c.zoom)
    for tile in c.tiles.sorted(by: { $0.order > $1.order }) {
      guard
        let q = LayerSceneProjection.unproject(
          screen, tile: tile, mode: c.mode, camera: c.camera
        )
      else { continue }
      let local = CGPoint(x: q.x - tile.bounds.minX, y: q.y - tile.bounds.minY)
      guard alpha(at: local, in: tile.image) > 0 else { continue }
      c.onSelect(tile.roomID, tile.layerID, q)
      break
    }
  }
  override func mouseMoved(with event: NSEvent) {
    _ = forwardPointer(
      .moved,
      at: convert(event.locationInWindow, from: nil),
      event: event
    )
  }
  override func mouseExited(with event: NSEvent) {
    sendPointerExit()
  }
  private func roomEdgeDistance(_ point: CGPoint, _ bounds: CGRect) -> CGFloat {
    [
      abs(point.x - bounds.minX), abs(point.x - bounds.maxX),
      abs(point.y - bounds.minY), abs(point.y - bounds.maxY)
    ].min() ?? .greatestFiniteMagnitude
  }
  private func forwardPointer(
    _ kind: GamePointerEventKind,
    at location: CGPoint,
    event: NSEvent,
    fallbackPoint: CGPoint? = nil
  ) -> Bool {
    guard let coordinator else { return false }
    guard LayerScenePointerPolicy.forwardsGamePointer(
      framingMode: coordinator.framingMode
    ) else { return false }
    coordinator.advanceViewportTransition(at: ProcessInfo.processInfo.systemUptime)
    let displayedLiveFrame = coordinator.displayedLiveFrame ?? .null
    let projected = GamePointerProjection.normalizedPoint(
        viewPoint: location,
        displayedLiveFrame: displayedLiveFrame
      )
    let now = ProcessInfo.processInfo.systemUptime
    if kind == .leftDown || (kind == .moved && now - lastPointerDiagnosticAt >= 1) {
      lastPointerDiagnosticAt = now
      pointerLog.notice(
        "view pointer kind=\(String(describing: kind), privacy: .public) insideCurrent=\(projected != nil, privacy: .public) cursorX=\(Double(location.x), privacy: .public) cursorY=\(Double(location.y), privacy: .public) frameX=\(Double(displayedLiveFrame.minX), privacy: .public) frameY=\(Double(displayedLiveFrame.minY), privacy: .public) frameW=\(Double(displayedLiveFrame.width), privacy: .public) frameH=\(Double(displayedLiveFrame.height), privacy: .public)"
      )
    }
    guard let normalized = projected ?? fallbackPoint else {
      sendPointerExit()
      return false
    }
    let forwarded = coordinator.onGamePointerEvent(GamePointerEvent(
      kind: kind,
      normalizedPoint: normalized,
      clickCount: event.clickCount
    ))
    if forwarded {
      pointerWithinLiveFrame = projected != nil
      lastForwardedPointer = normalized
      if projected == nil { sendPointerExit() }
    }
    return forwarded
  }
  private func sendPointerExit() {
    guard pointerWithinLiveFrame, let coordinator else { return }
    _ = coordinator.onGamePointerEvent(GamePointerEvent(
      kind: .exited,
      normalizedPoint: .zero,
      clickCount: 1
    ))
    pointerWithinLiveFrame = false
    if !passingPrimaryMouse { lastForwardedPointer = nil }
  }
  private func alpha(at point: CGPoint, in image: CGImage) -> UInt8 {
    guard image.width > 0, image.height > 0 else { return 0 }
    let x = min(image.width - 1, max(0, Int(point.x.rounded())))
    let y = min(image.height - 1, max(0, Int(point.y.rounded())))
    var bytes = [UInt8](repeating: 0, count: image.width * image.height * 4)
    guard
      let context = CGContext(
        data: &bytes, width: image.width, height: image.height, bitsPerComponent: 8,
        bytesPerRow: image.width * 4, space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue
          | CGImageAlphaInfo.premultipliedLast.rawValue)
    else { return 0 }
    context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    return bytes[((image.height - 1 - y) * image.width + x) * 4 + 3]
  }
}

private final class LayerMetalRenderer {
  private let device: MTLDevice
  private let queue: MTLCommandQueue
  private let pipeline: MTLRenderPipelineState
  private let backgroundPipeline: MTLRenderPipelineState
  private let solidColorPipeline: MTLRenderPipelineState
  private var textures = [ObjectIdentifier: (image: CGImage, texture: MTLTexture)]()
  init(_ device: MTLDevice) throws {
    self.device = device
    guard let queue = device.makeCommandQueue() else { throw CocoaError(.coderInvalidValue) }
    self.queue = queue
    let source =
      "#include <metal_stdlib>\nusing namespace metal; struct V{float2 p;float2 uv;}; struct O{float4 p[[position]];float2 uv;}; vertex O v(uint i[[vertex_id]],constant V* a[[buffer(0)]]){O o;o.p=float4(a[i].p,0,1);o.uv=a[i].uv;return o;} fragment float4 f(O x[[stage_in]],texture2d<float> t[[texture(0)]],constant float &opacity[[buffer(0)]]){constexpr sampler s(coord::normalized);return t.sample(s,x.uv)*opacity;}"
    let background =
      "\nfragment float4 background(O x[[stage_in]]){int checker=(int(floor(x.p.x/24))+int(floor(x.p.y/24)))&1;float c=checker?0.11:0.15;return float4(c,c,c,1);}\nfragment float4 solid(O x[[stage_in]],constant float4 &color[[buffer(0)]]){return color;}\n"
    let library = try device.makeLibrary(source: source + background, options: nil)
    guard let vertex = library.makeFunction(name: "v"),
      let fragment = library.makeFunction(name: "f")
    else { throw CocoaError(.coderInvalidValue) }
    let descriptor = MTLRenderPipelineDescriptor()
    descriptor.vertexFunction = vertex
    descriptor.fragmentFunction = fragment
    descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
    descriptor.colorAttachments[0].isBlendingEnabled = true
    descriptor.colorAttachments[0].sourceRGBBlendFactor = .one
    descriptor.colorAttachments[0].destinationRGBBlendFactor = .oneMinusSourceAlpha
    // Transparent debug pixels must preserve the opacity of the game underneath.
    descriptor.colorAttachments[0].sourceAlphaBlendFactor = .one
    descriptor.colorAttachments[0].destinationAlphaBlendFactor = .oneMinusSourceAlpha
    pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
    descriptor.fragmentFunction = library.makeFunction(name: "background")
    descriptor.colorAttachments[0].isBlendingEnabled = false
    backgroundPipeline = try device.makeRenderPipelineState(descriptor: descriptor)
    descriptor.fragmentFunction = library.makeFunction(name: "solid")
    descriptor.colorAttachments[0].isBlendingEnabled = true
    descriptor.colorAttachments[0].sourceRGBBlendFactor = .sourceAlpha
    descriptor.colorAttachments[0].destinationRGBBlendFactor = .oneMinusSourceAlpha
    solidColorPipeline = try device.makeRenderPipelineState(descriptor: descriptor)
  }
  @discardableResult
  func draw(in view: MTKView, coordinator c: LayerSceneViewport.Coordinator?) -> Bool {
    guard let c, view.bounds.width > 0, view.bounds.height > 0,
      c.zoom.isFinite, c.pan.x.isFinite, c.pan.y.isFinite,
      let pass = view.currentRenderPassDescriptor, let drawable = view.currentDrawable,
      let command = queue.makeCommandBuffer(),
      let encoder = command.makeRenderCommandEncoder(descriptor: pass)
    else { return false }
    let active = Set(c.tiles.map { ObjectIdentifier($0.image) })
    textures = textures.filter { active.contains($0.key) }
    if c.showsCheckerboardBackground {
      encoder.setRenderPipelineState(backgroundPipeline)
      var backgroundVertices = [
        SIMD4<Float>(-1, -1, 0, 0), SIMD4<Float>(1, -1, 1, 0), SIMD4<Float>(-1, 1, 0, 1),
        SIMD4<Float>(1, 1, 1, 1),
      ]
      encoder.setVertexBytes(
        &backgroundVertices, length: MemoryLayout<SIMD4<Float>>.stride * 4, index: 0)
      encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
    }
    encoder.setRenderPipelineState(pipeline)
    let atlasPan = c.atlasPan(at: ProcessInfo.processInfo.systemUptime)
    var liveCaptureTimestamp: Double?
    for tile in c.tiles.sorted(by: { $0.order < $1.order }) {
      guard let texture = texture(tile.image) else { continue }
      let r = LayerSceneProjection.project(
        tile, mode: c.mode, camera: c.camera)
      guard LayerSceneProjection.isFinite(r) else { continue }
      let tilePan = tile.isLiveOverlay ? c.pan : atlasPan
      let x0 = Float((r.minX * c.zoom + tilePan.x) / view.bounds.width * 2 - 1)
      let x1 = Float((r.maxX * c.zoom + tilePan.x) / view.bounds.width * 2 - 1)
      let y0 = Float((r.minY * c.zoom + tilePan.y) / view.bounds.height * 2 - 1)
      let y1 = Float((r.maxY * c.zoom + tilePan.y) / view.bounds.height * 2 - 1)
      guard x0.isFinite, x1.isFinite, y0.isFinite, y1.isFinite else { continue }
      if tile.isLiveOverlay, let captured = LivePresentationTiming.shared.timestamp(for: tile.image) {
        liveCaptureTimestamp = captured
      }
      var v = [
        SIMD4<Float>(x0, y0, 0, 1), SIMD4<Float>(x1, y0, 1, 1), SIMD4<Float>(x0, y1, 0, 0),
        SIMD4<Float>(x1, y1, 1, 0),
      ]
      encoder.setVertexBytes(&v, length: MemoryLayout<SIMD4<Float>>.stride * 4, index: 0)
      encoder.setFragmentTexture(texture, index: 0)
      var opacity = Float(min(1, max(0, tile.opacity)))
      encoder.setFragmentBytes(&opacity, length: MemoryLayout<Float>.stride, index: 0)
      encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
    }
    // These quads are generated directly in screen pixels. The world-to-screen projection
    // is the same transform used by tiles, while the marker and line widths stay readable.
    encoder.setRenderPipelineState(solidColorPipeline)
    let overlay = c.featureReviewOverlay
    let worldLines = overlay.map {
      LayerSceneFeatureReviewOverlay(
        markers: [],
        lines: $0.lines.filter { $0.coordinateSpace == .atlas }
      )
    }
    for quad in LayerSceneFeatureReviewGeometry.lineQuads(worldLines, zoom: c.zoom, pan: atlasPan) {
      drawSolid(quad, color: color(for: quad.lineKind!), in: view, encoder: encoder)
    }
    let liveLines = overlay.map {
      LayerSceneFeatureReviewOverlay(
        markers: [],
        lines: $0.lines.filter { $0.coordinateSpace == .live }
      )
    }
    for quad in LayerSceneFeatureReviewGeometry.lineQuads(liveLines, zoom: c.zoom, pan: c.pan) {
      drawSolid(quad, color: color(for: quad.lineKind!), in: view, encoder: encoder)
    }
    let worldMarkers = overlay.map {
      LayerSceneFeatureReviewOverlay(
        markers: $0.markers.filter {
          $0.coordinateSpace == .atlas
        },
        lines: []
      )
    }
    for quad in LayerSceneFeatureReviewGeometry.markerQuads(worldMarkers, zoom: c.zoom, pan: atlasPan) {
      drawSolid(quad, color: color(for: quad.kind!), in: view, encoder: encoder)
    }
    let liveMarkers = overlay.map {
      LayerSceneFeatureReviewOverlay(
        markers: $0.markers.filter {
          $0.coordinateSpace == .live
        },
        lines: []
      )
    }
    for quad in LayerSceneFeatureReviewGeometry.markerQuads(liveMarkers, zoom: c.zoom, pan: c.pan) {
      drawSolid(quad, color: color(for: quad.kind!), in: view, encoder: encoder)
    }
    encoder.endEncoding()
    if let capturedAt = liveCaptureTimestamp {
      let submittedAt = ProcessInfo.processInfo.systemUptime
      LivePresentationTiming.shared.submitted(capturedAt: capturedAt, at: submittedAt)
      command.addCompletedHandler { completed in
        LivePresentationTiming.shared.completed(submittedAt: submittedAt,
          gpuStart: completed.gpuStartTime, gpuEnd: completed.gpuEndTime)
      }
      drawable.addPresentedHandler { presented in
        LivePresentationTiming.shared.presented(capturedAt: capturedAt,
          submittedAt: submittedAt, at: presented.presentedTime)
      }
    }
    command.present(drawable)
    command.commit()
    return true
  }
  private func texture(_ image: CGImage) -> MTLTexture? {
    let key = ObjectIdentifier(image)
    if let cached = textures[key], cached.image === image { return cached.texture }
    let loader = MTKTextureLoader(device: device)
    let t = try? loader.newTexture(
      cgImage: image, options: [.SRGB: false, .origin: MTKTextureLoader.Origin.topLeft])
    if let t { textures[key] = (image, t) }
    return t
  }
  private func drawSolid(
    _ quad: LayerSceneOverlayQuad, color: SIMD4<Float>, in view: MTKView,
    encoder: MTLRenderCommandEncoder
  ) {
    guard quad.points.count == 4 else { return }
    var vertices = quad.points.map {
      SIMD4<Float>(
        Float($0.x / view.bounds.width * 2 - 1), Float($0.y / view.bounds.height * 2 - 1), 0, 1)
    }
    guard vertices.allSatisfy({ $0.x.isFinite && $0.y.isFinite }) else { return }
    var solidColor = color
    solidColor.w *= quad.opacity
    encoder.setVertexBytes(&vertices, length: MemoryLayout<SIMD4<Float>>.stride * 4, index: 0)
    encoder.setFragmentBytes(&solidColor, length: MemoryLayout<SIMD4<Float>>.stride, index: 0)
    encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
  }
  private func color(for kind: LayerSceneFeatureReviewMarkerKind) -> SIMD4<Float> {
    LayerSceneFeatureReviewPalette.rgba(for: kind)
  }
  private func color(for kind: LayerSceneFeatureReviewLineKind) -> SIMD4<Float> {
    LayerSceneFeatureReviewPalette.rgba(for: kind)
  }
}
