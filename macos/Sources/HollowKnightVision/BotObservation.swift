import CoreGraphics
import Foundation

/// Policy input shared with BotPython/hkv_bot_dataset.py. Hacker can project
/// authored floors and use exact-frame Knight position when vision loses it.
enum BotObservationContract {
    static let schemaVersion = 2
    static let legacyFeatureCount = 144
    static let featureCount = legacyFeatureCount + 11
    // Clips store exact segments beside the compact schema 2 measurements.
    // Schema 3 policies additionally receive a screen-space grid at each history step.
    static let screenPolicyVersion = 3
    static let screenPolicyNames = featureNames + BotTerrainGrid.names
    static let blockadePolicyVersion = 4
    static let blockadePolicyNames = Array(screenPolicyNames.prefix(13))
        + ["blockade_dx", "blockade_dy", "blockade_direction"] + Array(screenPolicyNames.dropFirst(16))
    static let geoPolicyVersion = 5
    static let geoPolicyNames = blockadePolicyNames + ["geo_cache_dx", "geo_cache_dy", "geo_cache_present"]

    static let generalPolicyVersion = 6
    static let generalPolicyNames = geoPolicyNames + ["atlas_present", "atlas_left", "atlas_right", "health_known", "health_fraction"]

    static let atlasMapVersion = 7
    static let atlasMapNames = generalPolicyNames + ["atlas_grid_present"] + (0..<15).map { "atlas_missing_\($0)" }

    static let roadVersion = 8
    static let roadNames = atlasMapNames + ["road_present", "road_dx", "road_dy", "road_clearance", "road_distance", "road_frontier"]

    static let pickupVersion = 9
    static let pickupNames = roadNames + ["joni_blessing_dx", "joni_blessing_dy", "joni_blessing_present"]

    static let cocoonVersion = 10
    static let cocoonNames = pickupNames + ["lifeblood_cocoon_dx", "lifeblood_cocoon_dy", "lifeblood_cocoon_present"]

    static let doorVersion = 11
    static let doorNames = cocoonNames + BotTerrainGrid.doorNames

    static let sharedPathVersion = 12
    static let sharedPathNames = doorNames + ["shared_path_present", "shared_path_dx", "shared_path_dy"]

    static let compactGeometryPolicyVersion = 13
    static let nearestGeometryPolicyVersion = 14
    static let groundContactPolicyVersion = 15
    static let landingGeometryPolicyVersion = 16
    // Recording schemas remain readable. The current policy retires the old
    // nearest-floor/wall lists, raster maps and unused Joni columns entirely.
    static let compactScalarIndices = sharedPathNames.indices.filter { i in
        let name = sharedPathNames[i]
        return !(name.hasPrefix("terrain_") && name != "terrain_valid")
            && !name.hasPrefix("wall_") && name != "walls_analyzed"
            && !name.hasPrefix("screen_") && !name.hasPrefix("joni_blessing_")
    }
    static let compactScalarNames = compactScalarIndices.map { sharedPathNames[$0] }
    static let compactGeometryNames = compactScalarNames + BotCompactMarkings.names

    static func policyNames(for version: Int) -> [String]? {
        switch version {
        case landingGeometryPolicyVersion: compactGeometryNames + ["knight_ground_contact"] + BotLandingGeometry.names
        case groundContactPolicyVersion: compactGeometryNames + ["knight_ground_contact"]
        case nearestGeometryPolicyVersion: compactGeometryNames
        case compactGeometryPolicyVersion: compactScalarNames + BotCompactMarkings.legacyNames
        case sharedPathVersion: sharedPathNames
        case doorVersion: doorNames
        case cocoonVersion: cocoonNames
        case pickupVersion: pickupNames
        case roadVersion: roadNames
        case atlasMapVersion: atlasMapNames
        case generalPolicyVersion: generalPolicyNames
        case geoPolicyVersion: geoPolicyNames
        case blockadePolicyVersion: blockadePolicyNames
        case screenPolicyVersion: screenPolicyNames
        default: names(for: version)
        }
    }
    static let historyLength = 4
    // Legacy recording list only; schema 14 uses 16 closest shared typed markings.
    static let terrainCapacity = 16
    static let enemyCapacity = 8
    static let sampleInterval = 0.1
    static let maximumDetectionAge = 0.2
    static let maximumCaptureAge = 0.35
    static let actionSizes = [3, 3, 2, 2]
    static let baseFeatureNames = [
        "knight_x", "knight_y", "knight_width", "knight_height", "knight_confidence",
        "detection_age", "terrain_valid", "capture_age", "room_changed",
        "previous_horizontal", "previous_aim", "previous_jump", "previous_attack",
        "target_x", "target_y", "target_valid",
    ]
    static let legacyFeatureNames: [String] = baseFeatureNames
            + (0..<terrainCapacity).flatMap { i in
                ["start_x", "end_x", "y", "confidence", "present"].map { "terrain_\(i)_\($0)" }
            }
            + (0..<enemyCapacity).flatMap { i in
                ["x", "y", "width", "height", "confidence", "present"].map { "enemy_\(i)_\($0)" }
            }

    static let wallFeatureNames = ["walls_analyzed"] + ["left", "right"].flatMap { side in
        ["x", "bottom_y", "top_y", "solid_side", "present"].map { "wall_\(side)_\($0)" }
    }
    static let featureNames = legacyFeatureNames + wallFeatureNames

    static func names(for version: Int) -> [String]? {
        switch version {
        case 1: legacyFeatureNames
        case schemaVersion: featureNames
        default: nil
        }
    }

    static func button(keyCode: UInt16) -> String {
        switch keyCode {
        case 123: "left"
        case 124: "right"
        case 125: "down"
        case 126: "up"
        case 6: "z"
        case 7: "x"
        case 0: "a"
        case 34: "inventory"
        case 35, 53: "pause"
        default: "key-\(keyCode)"
        }
    }
}

struct BotTerrainSegment: Codable, Equatable {
    let minimumX: Double
    let maximumX: Double
    let y: Double
    let confidence: Double
}

struct BotObservation: Codable, Equatable {
    let type: String
    let captureTimestamp: Double
    let capturedAt: Double
    let availableAt: Double
    let generation: UInt64
    let roomID: Int
    let roomRevision: UInt64
    let detectionTimestamp: Double?
    let detectionFrameID: UInt64?
    let modelVersion: String?
    let bestKnightConfidence: Double?
    var features: [Double]
    let issues: [String]
    var imagePath: String? = nil
    var terrainSource: String? = nil
    var humanTerrainCount: Int? = nil
    var knightSource: String? = nil
    var enemySource: String? = nil
    var geoCaches: [BotObjectBounds]? = nil
    var joniBlessings: [BotObjectBounds]? = nil // Historical version 9 captures.
    var lifebloodCocoons: [BotObjectBounds]? = nil
    var authoredTerrain: BotScreenTerrain? = nil
    var detectedTerrain: BotScreenTerrain? = nil
    var screenTerrain: BotScreenTerrain? { terrainSource == "human-authored" ? authoredTerrain : detectedTerrain }
    var atlasView: BotAtlasView? = nil
    var roadHint: BotRoadHint? = nil
    var policyScene: BotPolicyScene? = nil
    var hackerPathVersion: Int? = nil
    var pathAlignment: Double? = nil
    var atlasPathVersion: Int? = nil
    // Causal, measured forward distance on the current ground path, in Knight heights.
    var pathProgress: Double? = nil
    // Cumulative measurements for offline reward labels. Never added to policy inputs.
    var enemyOutcomes: BotEnemyOutcomes? = nil
    var blueHealthOutcomes: BotBlueHealthOutcomes? = nil
    // A recorded zero direction distinguishes evaluated frames from legacy clips.
    var driveDirection: Int? = nil
    // Hacker can distinguish a grounded shove from airborne knockback.
    var grounded: Bool? = nil
    var blockadePose: BotBlockadePose? = nil
    var blockade: BotBlockadeEvidence? = nil
    var visualContext: BotVisualTrackingContext? = nil
    var knightPrediction: BotKnightPrediction? = nil
    var isUsable: Bool { issues.isEmpty }
    var knightPredictionDeadline: Double {
        guard knightSource == "visual-predicted" || knightPrediction != nil else { return .infinity }
        guard knightSource == "visual-predicted", terrainSource == "detected",
              let context = visualContext, context.hasValidFrameDimensions,
              let prediction = knightPrediction, !prediction.cameraCompensated || context.camera != nil,
              abs(captureTimestamp - prediction.lastSeenAt - prediction.age) <= 0.000_001,
              prediction.observedIssues.contains("missing-knight"),
              prediction.observedIssues.allSatisfy(BotKnightPrediction.canBridge),
              prediction.age.isFinite, prediction.age > 0, prediction.age <= prediction.maximumAge + 0.000_001,
              prediction.uncertainty.isFinite, (0...0.06).contains(prediction.uncertainty)
        else { return -.infinity }
        return capturedAt + prediction.maximumAge - prediction.age
    }
}

/// Each fresh visual observation can authorize one short action, even through
/// a sustained occlusion. A stopped stream cannot renew the input lease.
enum BotKnightOcclusionWindow {
    static func deadline(for row: BotObservation) -> Double? {
        guard row.terrainSource == "detected", row.features.count >= BotObservationContract.legacyFeatureCount,
              row.issues == ["missing-knight"], row.knightSource == nil, row.knightPrediction == nil,
              row.features.prefix(5).allSatisfy({ $0 == 0 }), row.features[6] == 1,
              let detected = row.detectionTimestamp,
              (0...BotObservationContract.maximumDetectionAge).contains(row.captureTimestamp - detected),
              (0...BotObservationContract.maximumCaptureAge).contains(row.availableAt - row.capturedAt),
              stride(from: 20, to: 96, by: 5).contains(where: { row.features[$0] == 1 })
                || row.detectedTerrain?.walls.isEmpty == false
        else { return nil }
        return row.capturedAt + BotObservationContract.maximumCaptureAge
    }
}

struct BotActorBox {
    let centerX: Double
    let centerY: Double
    let width: Double
    let height: Double
    var isValid: Bool { [centerX, centerY, width, height].allSatisfy(\.isFinite) && width >= 0 && height >= 0 }
    var rect: CGRect { CGRect(x: centerX - width / 2, y: centerY - height / 2, width: width, height: height) }
}

struct BotModActorInput {
    let frameID: UInt64
    let knight: BotActorBox?
    let enemies: [BotActorBox]?
    var issues: [String] = []
    var geoCaches: [BotActorBox]? = nil
    var lifebloodCocoons: [BotActorBox]? = nil
}

enum BotObservationBuilder {
    /// Image points are bottom-left pixels; all lengths use frame width as the
    /// reference scale, preserving aspect ratio. Slots use stable geometric ties.
    static func make(captureTimestamp: Double, capturedAt: Double, availableAt: Double,
                     generation: UInt64, roomID: Int, roomRevision: UInt64,
                     frameSize: CGSize, detections batch: LiveObjectDetectionBatch?,
                     terrain: [BotTerrainSegment], terrainValid: Bool,
                     transitioning: Bool, terrainSource: String = "detected",
                     humanTerrainCount: Int = 0,
                     modActors: BotModActorInput? = nil,
                     visualCameraPosition: CGPoint? = nil) -> BotObservation {
        var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
        var issues = [String]()
        let width = Double(frameSize.width), height = Double(frameSize.height)
        let validSize = width.isFinite && height.isFinite && width > 0 && height > 0
            && width <= 65_536 && height <= 65_536
        if !validSize { issues.append("invalid-frame-size") }
        let scale = validSize ? width : 1
        let captureAge = availableAt - capturedAt
        if ![captureTimestamp, capturedAt, availableAt, captureAge].allSatisfy(\.isFinite)
            || captureAge < 0 || captureAge > BotObservationContract.maximumCaptureAge {
            issues.append("stale-capture")
        }
        if transitioning { issues.append("room-transition") }
        let age = batch.map { captureTimestamp - $0.sourceTimestamp }
        let validBatch = batch.map {
            $0.captureGeneration == generation && age!.isFinite
                && age! >= 0 && age! <= BotObservationContract.maximumDetectionAge
        } ?? false
        if !validBatch && modActors == nil { issues.append("stale-or-missing-detection") }
        let detections = validBatch ? batch!.detections.filter {
            $0.confidence.isFinite && (0.5...1).contains($0.confidence)
                && $0.sourceFrameIdentifier == batch!.sourceFrameIdentifier
        } : []
        let knight = detections.filter {
            LabelingClassIdentity.matches($0.classIdentifier, "game.playable-knight")
        }.max { $0.confidence < $1.confidence }
        let knightRect = knight?.imageRect(width: max(1, Int(validSize ? width : 1)),
                                          height: max(1, Int(validSize ? height : 1)))
        let modKnight = modActors?.knight.flatMap { actor -> BotActorBox? in
            guard validSize, actor.isValid, (0...width).contains(actor.centerX),
                  (0...height).contains(actor.centerY) else { return nil }
            return actor
        }
        if let modActors {
            issues.append(contentsOf: modActors.issues)
            if modKnight == nil { issues.append("missing-hacker-knight") }
            if modActors.enemies == nil { issues.append("missing-hacker-enemies") }
            if modActors.enemies?.contains(where: { !$0.isValid }) == true { issues.append("invalid-hacker-enemies") }
        } else if knightRect == nil { issues.append("missing-knight") }
        if !terrainValid { issues.append("missing-terrain-analysis") }
        let center = modActors != nil
            ? modKnight.map { CGPoint(x: $0.centerX, y: $0.centerY) } ?? .zero
            : knightRect.map { CGPoint(x: $0.midX, y: $0.midY) } ?? .zero
        if let actor = modKnight {
            features[0] = actor.centerX / scale; features[1] = actor.centerY / scale
            features[2] = actor.width / scale; features[3] = actor.height / scale; features[4] = 1
        } else if modActors == nil, let knight, let rect = knightRect {
            features[0] = Double(rect.midX) / scale
            features[1] = Double(rect.midY) / scale
            features[2] = Double(rect.width) / scale
            features[3] = Double(rect.height) / scale
            features[4] = knight.confidence
        }
        features[5] = min(1, max(0, (modActors != nil ? 0 : (age ?? BotObservationContract.maximumDetectionAge))
                                    / BotObservationContract.maximumDetectionAge))
        features[6] = terrainValid ? 1 : 0
        features[7] = captureAge.isFinite ? min(1, max(0, captureAge / BotObservationContract.maximumCaptureAge)) : 1
        // room_changed and previous actions are filled at export after temporal
        // segmentation. Schema 4 supplies the mapped blockade in slots 13–15
        // when assembling policy history; older checkpoints retain zeroes.
        let lines = terrain.filter {
            [$0.minimumX, $0.maximumX, $0.y, $0.confidence].allSatisfy(\.isFinite)
                && $0.maximumX > $0.minimumX && (0...1).contains($0.confidence)
        }.sorted {
            let a = hypot(($0.minimumX + $0.maximumX) / 2 - Double(center.x), $0.y - Double(center.y))
            let b = hypot(($1.minimumX + $1.maximumX) / 2 - Double(center.x), $1.y - Double(center.y))
            if a != b { return a < b }
            if $0.y != $1.y { return $0.y < $1.y }
            return $0.minimumX < $1.minimumX
        }
        for (i, line) in lines.prefix(BotObservationContract.terrainCapacity).enumerated() {
            let index = 16 + i * 5
            features.replaceSubrange(index..<(index + 5), with: [
                (line.minimumX - Double(center.x)) / scale,
                (line.maximumX - Double(center.x)) / scale,
                (line.y - Double(center.y)) / scale, line.confidence, 1,
            ])
        }
        let visualEnemies = detections.filter {
            LabelingClassIdentity.canonicalIdentifier($0.classIdentifier).hasPrefix("enemies.")
        }.compactMap { detection -> (LiveObjectDetection, CGRect)? in
            guard let rect = detection.imageRect(width: max(1, Int(validSize ? width : 1)),
                                                 height: max(1, Int(validSize ? height : 1))) else { return nil }
            return (detection, rect)
        }
        let enemies: [(CGRect, Double)] = (modActors != nil
            ? (modActors?.enemies ?? []).filter(\.isValid).map { ($0.rect, 1) }
            : visualEnemies.map { ($0.1, $0.0.confidence) }).sorted {
            let a = hypot($0.0.midX - center.x, $0.0.midY - center.y)
            let b = hypot($1.0.midX - center.x, $1.0.midY - center.y)
            if a != b { return a < b }
            if $0.0.minY != $1.0.minY { return $0.0.minY < $1.0.minY }
            return $0.0.minX < $1.0.minX
        }
        for (i, enemy) in enemies.prefix(BotObservationContract.enemyCapacity).enumerated() {
            let index = 96 + i * 6
            features.replaceSubrange(index..<(index + 6), with: [
                Double(enemy.0.midX - center.x) / scale, Double(enemy.0.midY - center.y) / scale,
                Double(enemy.0.width) / scale, Double(enemy.0.height) / scale, enemy.1, 1,
            ])
        }
        let cacheRects: [CGRect]?
        if let modActors {
            cacheRects = modActors.geoCaches?.filter(\.isValid).map(\.rect)
        } else if validBatch && validSize {
            cacheRects = detections.filter { LabelingClassIdentity.matches($0.classIdentifier, "world.geo-deposit") }
                .compactMap { $0.imageRect(width: Int(width), height: Int(height)) }
        } else { cacheRects = nil }
        func normalizedBounds(_ rects: [CGRect]?) -> [BotObjectBounds]? {
            rects?.compactMap { rect in
                guard validSize, (0...width).contains(rect.midX), (0...height).contains(rect.midY) else { return nil }
                return BotObjectBounds(x: rect.midX / scale, y: rect.midY / scale,
                                       width: rect.width / scale, height: rect.height / scale)
            }
        }
        return BotObservation(type: "observation", captureTimestamp: captureTimestamp,
            capturedAt: capturedAt, availableAt: availableAt, generation: generation,
            roomID: roomID, roomRevision: roomRevision, detectionTimestamp: modActors != nil ? captureTimestamp : batch?.sourceTimestamp,
            detectionFrameID: modActors?.frameID ?? batch?.sourceFrameIdentifier,
            modelVersion: modActors != nil ? "receiver-actors-v1" : knight?.modelVersion.displayName,
            bestKnightConfidence: validBatch && modActors == nil ? batch?.detections.filter {
                LabelingClassIdentity.matches($0.classIdentifier, "game.playable-knight") && $0.confidence.isFinite
            }.map(\.confidence).max() : nil,
            features: features.map { $0.isFinite ? min(4, max(-4, $0)) : 0 }, issues: issues,
            terrainSource: terrainSource, humanTerrainCount: humanTerrainCount,
            knightSource: modActors != nil ? "hacker-frame" : (knightRect != nil ? "visual" : nil),
            enemySource: modActors != nil ? "hacker-frame" : "visual", geoCaches: normalizedBounds(cacheRects),
            lifebloodCocoons: normalizedBounds(modActors?.lifebloodCocoons?.filter(\.isValid).map(\.rect)),
            visualContext: validSize && modActors == nil ? BotVisualTrackingContext(frameWidth: width,
                frameHeight: height / scale, cameraX: visualCameraPosition.map { Double($0.x) / scale },
                cameraY: visualCameraPosition.map { Double($0.y) / scale }) : nil)
    }
}
