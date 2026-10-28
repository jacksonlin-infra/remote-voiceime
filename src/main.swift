import AppKit

if CommandLine.arguments.contains("--version") {
    print("\(ProductInfo.name) \(ProductInfo.versionLabel)")
    print(Bundle.main.bundleURL.path)
    exit(0)
}

if CommandLine.arguments.contains("--self-test") {
    fputs("Run scripts/test.sh for isolated tests; the app will not start.\n", stderr)
    exit(2)
}

let app = NSApplication.shared
let delegate = VoiceApp()
app.setActivationPolicy(.accessory)
app.delegate = delegate
app.run()
