// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "WAI",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "wai", targets: ["wai"]),
        .executable(name: "WAIApp", targets: ["WAIApp"]),
        .library(name: "WAIKit", targets: ["WAIKit"]),
    ],
    dependencies: [
        // In-app updates (the app only). build.sh embeds the framework in Contents/Frameworks and signs it.
        .package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.10.0"),
    ],
    targets: [
        // The engine: crawler, extractors, index store, the embedder interface and worker client, search. No UI.
        .target(name: "WAIKit"),
        // llama.cpp (scripts/build-llama.sh builds it into Vendor/llama) and EmbeddingGemma 2 on it.
        .systemLibrary(name: "CLlama", path: "Sources/CLlama"),
        .target(
            name: "LlamaRuntime",
            dependencies: ["CLlama", "WAIKit"],
            linkerSettings: [.unsafeFlags(["-L\(Context.packageDirectory)/Vendor/llama/lib"])]
        ),
        // Developer CLI: estimate / index / search / status / eval. Doubles as the eval harness, and the app runs it as
        // its indexing helper and its query encoder.
        .executableTarget(name: "wai", dependencies: ["WAIKit", "LlamaRuntime"]),
        // The menubar + hotkey app. build.sh wraps it into build.noindex/WAI.app. (The product can't be called
        // "WAI": on a case-insensitive disk it would collide with the `wai` CLI binary.)
        .executableTarget(
            name: "WAIApp",
            dependencies: ["WAIKit", .product(name: "Sparkle", package: "Sparkle")],
            swiftSettings: [.defaultIsolation(MainActor.self)],
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])]
        ),
        .testTarget(name: "WAIKitTests", dependencies: ["WAIKit"]),
        .testTarget(name: "LlamaRuntimeTests", dependencies: ["LlamaRuntime"]),
    ]
)
