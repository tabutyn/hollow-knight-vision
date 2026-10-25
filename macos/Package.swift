// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "HollowKnightVision",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "HollowKnightVision", targets: ["HollowKnightVision"]),
        .executable(name: "HollowKnightVisionTrainer", targets: ["HollowKnightVisionTrainer"]),
    ],
    targets: [
        .executableTarget(
            name: "HollowKnightVision",
            resources: [.process("Resources")]
        ),
        .executableTarget(name: "HollowKnightVisionTrainer"),
        .testTarget(name: "HollowKnightVisionTests", dependencies: ["HollowKnightVision"],
                    resources: [
                        .process("hud-stencil-cases.json"),
                        .process("hud-stencil-reference.json"),
                    ]),
    ],
    swiftLanguageModes: [.v5]
)
