import AppKit
import Combine
import Foundation
import ImageIO

/// The capture archive runs at display rate. Incomplete archives may still
/// contain usable frames, and legacy clips fall back to their saved images.
@MainActor final class BotClipPlayback: ObservableObject {
    struct Frame {
        let timestamp: Double
        let url: URL
    }

    @Published private(set) var image: NSImage?
    @Published private(set) var displayedTimestamp: Double?
    private(set) var usesSourceFrames = false
    private var frames = [Frame]()
    private var clipID: UUID?
    private var requestedIndex: Int?
    private var loadedIndex: Int?
    private var loading = false
    private var generation = 0

    func load(_ clip: BotSnippet) {
        guard clipID != clip.id else { return }
        clipID = clip.id
        generation += 1
        requestedIndex = nil
        loadedIndex = nil
        image = nil
        displayedTimestamp = nil
        let directory = InputPathStore.defaultRootURL()
            .appendingPathComponent("frames-\(clip.manifest.sourceReplayID.uuidString.lowercased())")
        let manifestURL = directory.appendingPathComponent("manifest.json")
        if let data = try? Data(contentsOf: manifestURL),
           let archive = try? JSONDecoder().decode(VisualReplayManifest.self, from: data),
           archive.pathID == clip.manifest.sourceReplayID {
            frames = archive.frames.compactMap { frame in
                guard frame.captureTimestamp.isFinite, frame.issue == nil,
                      frame.fileName == String(format: "%08d.png", frame.index),
                      let name = frame.fileName else { return nil }
                return Frame(timestamp: frame.captureTimestamp, url: directory.appendingPathComponent(name))
            }.sorted { $0.timestamp < $1.timestamp }
        } else {
            frames = []
        }
        usesSourceFrames = !frames.isEmpty
        if !usesSourceFrames {
            frames = clip.observations.compactMap { row in
                guard let path = row.imagePath, !path.hasPrefix("/"),
                      !path.split(separator: "/").contains("..") else { return nil }
                return Frame(timestamp: row.captureTimestamp, url: clip.directory.appendingPathComponent(path))
            }.sorted { $0.timestamp < $1.timestamp }
        }
        request(at: clip.manifest.startedAt)
    }

    func request(at time: Double) {
        guard !frames.isEmpty else { return }
        var low = 0, high = frames.count
        while low < high {
            let middle = (low + high) / 2
            if frames[middle].timestamp <= time { low = middle + 1 } else { high = middle }
        }
        requestedIndex = max(0, low - 1)
        schedule()
    }

    private func schedule() {
        guard !loading, let index = requestedIndex, index != loadedIndex,
              frames.indices.contains(index) else { return }
        let frame = frames[index], epoch = generation
        loading = true
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let image: NSImage? = autoreleasepool {
                guard let source = CGImageSourceCreateWithURL(frame.url as CFURL, nil),
                      let cg = CGImageSourceCreateImageAtIndex(source, 0, [
                        kCGImageSourceShouldCacheImmediately: true
                      ] as CFDictionary) else { return nil }
                return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
            }
            DispatchQueue.main.async {
                guard let self else { return }
                self.loading = false
                if self.generation == epoch {
                    self.loadedIndex = index
                    if let image { self.image = image; self.displayedTimestamp = frame.timestamp }
                }
                self.schedule()
            }
        }
    }
}
