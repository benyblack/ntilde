import AppKit
import CoreServices
import Foundation

guard ProcessInfo.processInfo.environment["CI"] == "true", CommandLine.arguments.count == 3 else {
    fputs("Requires an isolated CI desktop\n", stderr)
    exit(2)
}
let operation = CommandLine.arguments[1]
let root = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
let filename = operation == "write" ? "peer-fixture.json" : "fixture.json"
let encoded = try JSONDecoder().decode([String: String].self, from: Data(contentsOf: root.appendingPathComponent(filename)))
let clipboard = NSPasteboard.general
func nativeType(_ mime: String) -> NSPasteboard.PasteboardType {
    guard let value = UTTypeCreatePreferredIdentifierForTag(kUTTagClassMIMEType, mime as CFString, nil) else { fatalError("Cannot map MIME fixture") }
    let identifier = value.takeRetainedValue() as String
    return NSPasteboard.PasteboardType(identifier)
}
if operation == "read" {
    for (mime, value) in encoded {
        guard let expected = Data(base64Encoded: value), let actual = clipboard.data(forType: nativeType(mime)), expected == actual else {
            fatalError("Native AppKit clipboard mismatch: \(mime)")
        }
    }
} else if operation == "write" {
    let item = NSPasteboardItem()
    for (mime, value) in encoded {
        guard let data = Data(base64Encoded: value), item.setData(data, forType: nativeType(mime)) else { fatalError("Invalid fixture") }
    }
    clipboard.clearContents()
    guard clipboard.writeObjects([item]) else { fatalError("Native clipboard write refused") }
    try Data("ok".utf8).write(to: root.appendingPathComponent("peer-write.ready"))
    // Stay alive until the parent terminates this known peer process.
    RunLoop.main.run()
} else { exit(2) }
