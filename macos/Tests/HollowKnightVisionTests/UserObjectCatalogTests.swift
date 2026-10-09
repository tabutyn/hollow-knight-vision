import Foundation
import Testing
@testable import HollowKnightVision

@Test func userObjectCatalogPersistsPortableSchema() throws {
    let root = FileManager.default.temporaryDirectory
        .appendingPathComponent("hkv-catalog-\(UUID().uuidString)", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let url = root.appendingPathComponent("object-catalog-v1.json")
    let first = try UserObjectCatalogStore.add(name: "Moss Charger", group: .enemies, to: url)
    #expect(first.identifier == "enemies.moss-charger")
    let duplicate = try UserObjectCatalogStore.add(name: "Moss Charger", group: .enemies, to: url)
    #expect(duplicate == first)
    let second = try UserObjectCatalogStore.add(name: "Breakable Wall", group: .world, to: url)
    #expect(second.identifier == "world.breakable-wall")
    let document = UserObjectCatalogStore.load(from: url)
    #expect(document.schemaVersion == 1)
    #expect(document.objects.count == 2)
}
