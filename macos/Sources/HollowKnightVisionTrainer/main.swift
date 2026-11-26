import Darwin
import Foundation
import HollowKnightVisionTrainingRuntime

private enum LauncherError: LocalizedError {
    case trainerMissing(String)
    case environmentMissing(String)

    var errorDescription: String? {
        switch self {
        case .trainerMissing(let path):
            return "Incremental trainer is missing: \(path)"
        case .environmentMissing(let path):
            return "Incremental trainer environment is missing. Run the app's Contents/Resources/setup-incremental-trainer.sh (expected \(path))."
        }
    }
}

private func launch() throws -> Never {
    let fileManager = FileManager.default
    let executable = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
    let helpersDirectory = executable.deletingLastPathComponent()
    let contentsDirectory = helpersDirectory.deletingLastPathComponent()
    var forwarded = Array(CommandLine.arguments.dropFirst())
    let transfer = forwarded.first == "--library-transfer"
    if transfer { forwarded.removeFirst() }
    let script = transfer ? "hkv_library_transfer.py" : "hkv_incremental_trainer.py"
    let trainer = contentsDirectory.appendingPathComponent("Resources/TrainingPython/\(script)")
    guard fileManager.fileExists(atPath: trainer.path) else {
        throw LauncherError.trainerMissing(trainer.path)
    }

    let python = try TrainingEnvironment().pythonURL()
    guard fileManager.isExecutableFile(atPath: python.path) else {
        throw LauncherError.environmentMissing(python.path)
    }

    let arguments = [python.path, "-B", trainer.path] + forwarded
    var pointers = arguments.map { strdup($0) }
    pointers.append(nil)
    execv(python.path, &pointers)
    let detail = String(cString: strerror(errno))
    throw NSError(
        domain: NSPOSIXErrorDomain,
        code: Int(errno),
        userInfo: [NSLocalizedDescriptionKey: "Cannot start incremental trainer: \(detail)"]
    )
}

do {
    if CommandLine.arguments.count >= 3, CommandLine.arguments[1] == "--register-python" {
        try TrainingEnvironment().register(
            pythonURL: URL(fileURLWithPath: CommandLine.arguments[2]),
            onlyIfUnset: CommandLine.arguments.dropFirst(3).contains("--if-unset")
        )
        exit(EXIT_SUCCESS)
    }
    try launch()
} catch {
    FileHandle.standardError.write(Data("Training failed: \(error.localizedDescription)\n".utf8))
    exit(EXIT_FAILURE)
}
