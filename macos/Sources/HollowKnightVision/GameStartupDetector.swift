import CoreGraphics

enum GameStartupScreen: Equatable {
    case title
    case profileOne
}

struct GameStartupEvidence: Equatable {
    let screen: GameStartupScreen?
    let gameplayLikely: Bool
    let selectedOption: String?

    init(
        screen: GameStartupScreen?,
        gameplayLikely: Bool,
        selectedOption: String? = nil
    ) {
        self.screen = screen
        self.gameplayLikely = gameplayLikely
        self.selectedOption = selectedOption
    }

    static let unknown = GameStartupEvidence(screen: nil, gameplayLikely: false)
}

enum GameStartupAction: Equatable {
    case moveSelectionUp
    case selectStartGame
    case selectProfileOne
}

enum GameStartupPhase: Equatable {
    case awaitingTitle
    case awaitingProfile
    case awaitingGameplay
    case gameplay

    var status: String {
        switch self {
        case .awaitingTitle: return "Finding Start Game"
        case .awaitingProfile: return "Finding profile 1"
        case .awaitingGameplay: return "Waiting for gameplay"
        case .gameplay: return "Tracking"
        }
    }
}

struct GameStartupDecision: Equatable {
    let phase: GameStartupPhase
    let action: GameStartupAction?

    var admitsWorldFrames: Bool { phase == .gameplay }
}

/// Keeps menu automation one-shot and requires several matching observations
/// before sending a select. It also gates all world writes until gameplay has
/// remained recognizable across consecutive sampled frames.
struct GameStartupCoordinator {
    private(set) var phase: GameStartupPhase = .awaitingTitle
    private var stableTitleFrames = 0
    private var stableProfileFrames = 0
    private var stableGameplayFrames = 0
    private var navigationCooldownFrames = 0
    let requiredStableFrames: Int

    // Vision samples every other 30 Hz capture. Eight observations leave the
    // review boxes visible for roughly half a second before selection.
    init(requiredStableFrames: Int = 8) {
        self.requiredStableFrames = max(1, requiredStableFrames)
    }

    mutating func reset(preservingGameplay: Bool = false) {
        if preservingGameplay, phase == .gameplay {
            stableTitleFrames = 0
            stableProfileFrames = 0
            stableGameplayFrames = 0
            return
        }
        self = GameStartupCoordinator(requiredStableFrames: requiredStableFrames)
    }

    /// The game-side checkpoint receiver only reports success after loading a
    /// gameplay scene. This avoids making deterministic developer replay wait
    /// for asynchronous HUD inference to rediscover a state already proven by
    /// that receiver. Normal launch/navigation never calls this path.
    mutating func restoreGameplaySession() {
        phase = .gameplay
        stableTitleFrames = 0
        stableProfileFrames = 0
        stableGameplayFrames = 0
    }

    mutating func observe(_ evidence: GameStartupEvidence) -> GameStartupDecision {
        if phase == .gameplay {
            // A real return to either menu must close the world-write gate.
            // Keep tracking only while no menu cue is recognized.
            switch evidence.screen {
            case .title:
                phase = .awaitingTitle
            case .profileOne:
                phase = .awaitingProfile
            case .none:
                return GameStartupDecision(phase: .gameplay, action: nil)
            }
            stableTitleFrames = 0
            stableProfileFrames = 0
            stableGameplayFrames = 0
        }

        if evidence.gameplayLikely {
            stableGameplayFrames += 1
            stableTitleFrames = 0
            stableProfileFrames = 0
            if stableGameplayFrames >= requiredStableFrames {
                phase = .gameplay
            }
            return GameStartupDecision(phase: phase, action: nil)
        }
        stableGameplayFrames = 0

        switch evidence.screen {
        case .title where phase == .awaitingTitle
            || phase == .awaitingProfile || phase == .awaitingGameplay:
            // Back from Select Profile returns to the title screen. The
            // navigation phase must follow that return, not wait for gameplay.
            if phase != .awaitingTitle { phase = .awaitingTitle; stableTitleFrames = 0 }
            if navigationCooldownFrames > 0 {
                navigationCooldownFrames -= 1
                stableTitleFrames = 0
                return GameStartupDecision(phase: phase, action: nil)
            }
            stableTitleFrames += 1
            stableProfileFrames = 0
            if stableTitleFrames >= requiredStableFrames {
                guard let selected = evidence.selectedOption else {
                    stableTitleFrames = 0
                    return GameStartupDecision(phase: phase, action: nil)
                }
                if selected.caseInsensitiveCompare("Start Game") != .orderedSame {
                    stableTitleFrames = 0
                    navigationCooldownFrames = max(16, requiredStableFrames * 4)
                    return GameStartupDecision(
                        phase: phase,
                        action: .moveSelectionUp
                    )
                }
                phase = .awaitingProfile
                return GameStartupDecision(
                    phase: phase,
                    action: .selectStartGame
                )
            }
        case .profileOne where phase == .awaitingTitle || phase == .awaitingProfile:
            if navigationCooldownFrames > 0 {
                navigationCooldownFrames -= 1
                stableProfileFrames = 0
                return GameStartupDecision(phase: phase, action: nil)
            }
            stableProfileFrames += 1
            stableTitleFrames = 0
            if stableProfileFrames >= requiredStableFrames {
                guard let selected = evidence.selectedOption else {
                    stableProfileFrames = 0
                    return GameStartupDecision(phase: phase, action: nil)
                }
                if selected.caseInsensitiveCompare("1.") != .orderedSame {
                    stableProfileFrames = 0
                    navigationCooldownFrames = max(16, requiredStableFrames * 4)
                    return GameStartupDecision(
                        phase: phase,
                        action: .moveSelectionUp
                    )
                }
                phase = .awaitingGameplay
                return GameStartupDecision(
                    phase: phase,
                    action: .selectProfileOne
                )
            }
        case .none:
            stableTitleFrames = 0
            stableProfileFrames = 0
        default:
            break
        }
        return GameStartupDecision(phase: phase, action: nil)
    }

    mutating func retry(_ action: GameStartupAction) {
        switch action {
        case .moveSelectionUp:
            navigationCooldownFrames = 0
            if phase == .awaitingTitle {
                stableTitleFrames = 0
            } else if phase == .awaitingProfile {
                stableProfileFrames = 0
            }
        case .selectStartGame:
            phase = .awaitingTitle
            stableTitleFrames = 0
        case .selectProfileOne:
            phase = .awaitingProfile
            stableProfileFrames = 0
        }
    }
}

/// Recognizes stable startup screen geometry for optional automatic navigation.
/// Regions are normalized to the title-bar-free 16:9 game image, so window size
/// is irrelevant. This detector emits state only; it owns no presentation boxes.
struct GameStartupDetector {
    private static let sampleWidth = 320

    func detect(
        in image: CGImage,
        menuStencil: MenuStencilResult? = nil,
        hudStencil: HUDStencilResult? = nil,
        gameplayIsLatched: Bool = false
    ) -> GameStartupEvidence {
        func menuEvidence() -> GameStartupEvidence? {
            guard menuStencil?.isMatch == true else { return nil }
            switch menuStencil?.context {
            case .mainTitle:
                return GameStartupEvidence(
                    screen: .title,
                    gameplayLikely: false,
                    selectedOption: menuStencil?.selectedOption
                )
            case .selectProfile:
                return GameStartupEvidence(
                    screen: .profileOne,
                    gameplayLikely: false,
                    selectedOption: menuStencil?.selectedOption
                )
            default:
                return .unknown
            }
        }
        if gameplayIsLatched {
            // Once gameplay is established, only positive menu evidence can
            // close it. Avoid repeating the startup downsample and HUD scan.
            if let menu = menuEvidence() { return menu }
            return .unknown
        }
        let sampleHeight = max(1, Int(
            (CGFloat(image.height) * CGFloat(Self.sampleWidth) / CGFloat(max(1, image.width))).rounded()
        ))
        guard let pixels = pixels(from: image, width: Self.sampleWidth, height: sampleHeight) else {
            if let menu = menuEvidence() { return menu }
            return GameStartupEvidence(
                screen: nil,
                gameplayLikely: hudStencil?.establishesGameplay == true
            )
        }
        let mana = brightBounds(
            pixels: pixels,
            sampleHeight: sampleHeight,
            normalizedROI: CGRect(x: 0.045, y: 0.035, width: 0.085, height: 0.18),
            minimumPixels: 8
        )
        let health = brightBounds(
            pixels: pixels,
            sampleHeight: sampleHeight,
            normalizedROI: CGRect(x: 0.115, y: 0.045, width: 0.16, height: 0.11),
            minimumPixels: 8
        )
        if hudStencil?.establishesGameplay == true || (mana != nil && health != nil) {
            // A transitioning menu tracker can briefly retain its last scene.
            // A verified health row is the primary gameplay cue; the coarse
            // fallback still requires both bright health and Soul regions.
            return GameStartupEvidence(screen: nil, gameplayLikely: true)
        }
        if let menu = menuEvidence() { return menu }
        if let profile = profileEvidence(pixels: pixels, sampleHeight: sampleHeight) {
            return profile
        }
        return .unknown
    }

    private func profileEvidence(
        pixels: [UInt8],
        sampleHeight: Int
    ) -> GameStartupEvidence? {
        guard let left = brightBounds(
            pixels: pixels, sampleHeight: sampleHeight,
            normalizedROI: CGRect(x: 0.075, y: 0.245, width: 0.075, height: 0.13), minimumPixels: 5
        ), let numberOne = brightBounds(
            pixels: pixels, sampleHeight: sampleHeight,
            normalizedROI: CGRect(x: 0.145, y: 0.245, width: 0.08, height: 0.13), minimumPixels: 7
        ), let right = brightBounds(
            pixels: pixels, sampleHeight: sampleHeight,
            normalizedROI: CGRect(x: 0.64, y: 0.245, width: 0.09, height: 0.13), minimumPixels: 5
        ), aligned([left, numberOne, right], tolerance: CGFloat(sampleHeight) * 0.055) else { return nil }
        return GameStartupEvidence(
            screen: .profileOne,
            gameplayLikely: false
        )
    }

    private func aligned(_ rects: [CGRect], tolerance: CGFloat) -> Bool {
        guard let minimum = rects.map(\.midY).min(), let maximum = rects.map(\.midY).max() else { return false }
        return maximum - minimum <= tolerance
    }

    /// Returns top-origin sample coordinates. Tiny one-pixel stars are removed
    /// before the remaining UI strokes are combined into one review rectangle.
    private func brightBounds(
        pixels: [UInt8],
        sampleHeight: Int,
        normalizedROI: CGRect,
        minimumPixels: Int
    ) -> CGRect? {
        let width = Self.sampleWidth
        let x0 = max(0, Int((normalizedROI.minX * CGFloat(width)).rounded(.down)))
        let x1 = min(width, Int((normalizedROI.maxX * CGFloat(width)).rounded(.up)))
        let y0 = max(0, Int((normalizedROI.minY * CGFloat(sampleHeight)).rounded(.down)))
        let y1 = min(sampleHeight, Int((normalizedROI.maxY * CGFloat(sampleHeight)).rounded(.up)))
        guard x0 < x1, y0 < y1 else { return nil }

        var mask = [Bool](repeating: false, count: width * sampleHeight)
        for y in y0..<y1 {
            for x in x0..<x1 {
                let index = (y * width + x) * 4
                let red = Int(pixels[index])
                let green = Int(pixels[index + 1])
                let blue = Int(pixels[index + 2])
                guard min(red, min(green, blue)) >= 145,
                      max(red, max(green, blue)) - min(red, min(green, blue)) <= 72 else { continue }
                mask[y * width + x] = true
            }
        }
        var visited = [Bool](repeating: false, count: mask.count)
        var retained = [(Int, Int)]()
        for y in y0..<y1 {
            for x in x0..<x1 {
                let start = y * width + x
                guard mask[start], !visited[start] else { continue }
                var queue = [(x, y)]
                var component = [(Int, Int)]()
                visited[start] = true
                var cursor = 0
                while cursor < queue.count {
                    let point = queue[cursor]
                    cursor += 1
                    component.append(point)
                    for neighborY in max(y0, point.1 - 1)...min(y1 - 1, point.1 + 1) {
                        for neighborX in max(x0, point.0 - 1)...min(x1 - 1, point.0 + 1) {
                            let index = neighborY * width + neighborX
                            if mask[index], !visited[index] {
                                visited[index] = true
                                queue.append((neighborX, neighborY))
                            }
                        }
                    }
                }
                let componentLowY = component.map(\.1).min() ?? y
                let componentHighY = component.map(\.1).max() ?? y
                if component.count >= 2, componentHighY > componentLowY {
                    retained += component
                }
            }
        }
        guard retained.count >= minimumPixels else { return nil }
        let lowX = retained.map(\.0).min()!
        let highX = retained.map(\.0).max()!
        let lowY = retained.map(\.1).min()!
        let highY = retained.map(\.1).max()!
        return CGRect(x: lowX, y: lowY, width: highX - lowX + 1, height: highY - lowY + 1)
    }

    private func pixels(from image: CGImage, width: Int, height: Int) -> [UInt8]? {
        var result = [UInt8](repeating: 0, count: width * height * 4)
        guard let context = CGContext(
            data: &result,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .low
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return result
    }
}
