import Foundation

/// Earlier Windows/Python runs used 32 hex digits. Keep normal UUIDs everywhere
/// else, including dataset IDs and newly written restore-point metadata.
@propertyWrapper
struct TrainingRunIdentifier: Decodable, Equatable {
    var wrappedValue: UUID

    init(wrappedValue: UUID) { self.wrappedValue = wrappedValue }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let value = try container.decode(String.self)
        guard let identifier = Self.parse(value) else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Training run identifier must be a UUID.")
        }
        wrappedValue = identifier
    }

    static func parse(_ value: String) -> UUID? {
        if value.utf8.count == 36 { return UUID(uuidString: value) }
        let bytes = Array(value.utf8)
        guard bytes.count == 32, bytes.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { return nil }
        let groups = [0..<8, 8..<12, 12..<16, 16..<20, 20..<32]
        return UUID(uuidString: groups.map { String(decoding: bytes[$0], as: UTF8.self) }.joined(separator: "-"))
    }
}
