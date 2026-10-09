import Darwin
import Foundation

private enum LauncherError: LocalizedError {
    case trainerMissing(String)
    case environmentMissing(String)

    var errorDescription: String? {
        switch self {
        case .trainerMissing(let path):
            return "Incremental trainer is missing: \(path)"
        case .environmentMissing(let path):
            return "Incremental trainer environment is missing. Run setup-incremental-trainer.sh (expected \(path))."
        }
    }
}

private func launch() throws -> Never {
    let fileManager = FileManager.default
    let executable = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
    let helpersDirectory = executable.deletingLastPathComponent()
    let contentsDirectory = helpersDirectory.deletingLastPathComponent()
    let trainer = contentsDirectory
        .appendingPathComponent("Resources/TrainingPython/hkv_incremental_trainer.py")
    guard fileManager.fileExists(atPath: trainer.path) else {
        throw LauncherError.trainerMissing(trainer.path)
    }

    let packageDirectory = contentsDirectory
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
    let defaultPython = packageDirectory.appendingPathComponent(".training-venv/bin/python")
    let environmentPython = ProcessInfo.processInfo.environment["HKV_TRAINER_PYTHON"]
        .map { URL(fileURLWithPath: $0) }
    let python = environmentPython ?? defaultPython
    guard fileManager.isExecutableFile(atPath: python.path) else {
        throw LauncherError.environmentMissing(python.path)
    }

    let arguments = [python.path, trainer.path] + Array(CommandLine.arguments.dropFirst())
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
    try launch()
} catch {
    FileHandle.standardError.write(Data("Training failed: \(error.localizedDescription)\n".utf8))
    exit(EXIT_FAILURE)
}
