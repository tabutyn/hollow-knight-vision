import CoreGraphics

struct RecentCapturedFrameIdentifier: Hashable {
    let captureGeneration: UInt64
    let sourceFrameIdentifier: UInt64
}

struct RecentCapturedFrame: Identifiable {
    let captureGeneration: UInt64
    let sourceFrameIdentifier: UInt64
    let image: CGImage
    let detections: [LiveObjectDetection]

    var id: RecentCapturedFrameIdentifier {
        RecentCapturedFrameIdentifier(
            captureGeneration: captureGeneration,
            sourceFrameIdentifier: sourceFrameIdentifier
        )
    }

    func replacingDetections(_ detections: [LiveObjectDetection]) -> RecentCapturedFrame {
        RecentCapturedFrame(
            captureGeneration: captureGeneration,
            sourceFrameIdentifier: sourceFrameIdentifier,
            image: image,
            detections: detections
        )
    }
}

struct RecentCapturedFrameBuffer {
    static let defaultCapacity = 64

    private(set) var frames = [RecentCapturedFrame]()
    let capacity: Int
    private var captureGeneration: UInt64?

    init(capacity: Int = Self.defaultCapacity) {
        self.capacity = max(1, capacity)
    }

    mutating func append(_ frame: RecentCapturedFrame) {
        if let captureGeneration {
            guard frame.captureGeneration >= captureGeneration else { return }
            if frame.captureGeneration > captureGeneration {
                frames.removeAll(keepingCapacity: true)
                self.captureGeneration = frame.captureGeneration
            }
        } else {
            captureGeneration = frame.captureGeneration
        }
        frames.removeAll { $0.id == frame.id }
        frames.insert(frame, at: 0)
        if frames.count > capacity {
            frames.removeLast(frames.count - capacity)
        }
    }

    mutating func replaceDetections(
        _ detections: [LiveObjectDetection],
        captureGeneration: UInt64,
        sourceFrameIdentifier: UInt64
    ) {
        let identifier = RecentCapturedFrameIdentifier(
            captureGeneration: captureGeneration,
            sourceFrameIdentifier: sourceFrameIdentifier
        )
        guard let index = frames.firstIndex(where: { $0.id == identifier }) else { return }
        frames[index] = frames[index].replacingDetections(detections)
    }

    mutating func removeAll() {
        frames.removeAll(keepingCapacity: true)
        captureGeneration = nil
    }
}
