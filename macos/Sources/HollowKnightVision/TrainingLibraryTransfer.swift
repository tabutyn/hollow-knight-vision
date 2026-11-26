import Foundation

enum TrainingLibraryTransfer {
    static func run(importing: Bool, bundle: URL, support: URL, helper: URL) throws -> String {
        let output = Pipe()
        let process = Process()
        process.executableURL = helper
        process.arguments = ["--library-transfer", importing ? "import" : "export", support.path, bundle.path]
        process.standardOutput = output
        process.standardError = output
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let message = String(decoding: data, as: UTF8.self).split(whereSeparator: \.isNewline).last
            .map(String.init) ?? "Vision did not return a transfer result."
        guard process.terminationStatus == EXIT_SUCCESS else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSLocalizedDescriptionKey: message])
        }
        return message
    }
}
