import Combine
import Foundation

@MainActor final class BotTrainingWorkspaceController: ObservableObject {
    let gameplay: BotTrainingController
    let hacker: BotTrainingController
    private var subscriptions = Set<AnyCancellable>()
    init(root: URL = BotModelStore.defaultRoot) {
        gameplay = BotTrainingController(store: BotModelStore(root: root, domain: .gameplay))
        hacker = BotTrainingController(store: BotModelStore(root: root, domain: .hacker))
        NotificationCenter.default.publisher(for: .botTrainingCapturePublished)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] notification in
                guard let directories = notification.object as? [URL],
                      directories.contains(where: { $0.deletingLastPathComponent().standardizedFileURL == root.appendingPathComponent("demonstrations").standardizedFileURL }) else { return }
                self?.refresh()
            }.store(in: &subscriptions)
        for controller in [gameplay, hacker] {
            controller.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }.store(in: &subscriptions)
        }
    }
    var isRunning: Bool { gameplay.isRunning || hacker.isRunning }
    var canTrain: Bool { gameplay.canTrain || hacker.canTrain }
    var newClipCount: Int {
        Set((gameplay.newCaptureManifests + hacker.newCaptureManifests).map(\.sourceReplayID)).count
    }
    func model(_ domain: BotDomain) -> BotTrainingController { domain == .gameplay ? gameplay : hacker }
    func refresh() { gameplay.refresh(); hacker.refresh() }
    func start(reload: @escaping () -> Void) {
        for controller in [gameplay, hacker] where controller.canTrain { controller.start(reload: reload) }
    }
    func cancel() { gameplay.cancel(); hacker.cancel() }
}
