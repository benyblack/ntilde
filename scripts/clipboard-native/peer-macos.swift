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
func binaryPayload(_ data: Data) -> Data {
    guard data.count >= 4 else { fatalError("Missing binary fixture length") }
    let length = data.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
    guard Int(length) + 4 == data.count else { fatalError("Invalid binary fixture length") }
    return data.subdata(in: 4..<data.count)
}
if operation == "read" {
    for (mime, value) in encoded {
        let actual = mime == "text/plain" ? clipboard.string(forType: .string)?.data(using: .utf8) : clipboard.data(forType: nativeType(mime))
        guard let expected = Data(base64Encoded: value), let actual = actual else {
            fatalError("Native AppKit clipboard mismatch: \(mime)")
        }
        if mime.hasPrefix("application/x-ntilde-qa") {
            guard binaryPayload(actual) == binaryPayload(expected) else { fatalError("Native binary clipboard mismatch") }
        } else if expected != actual { fatalError("Native clipboard mismatch: \(mime)") }
    }
} else if operation == "write" {
    let item = NSPasteboardItem()
    for (mime, value) in encoded {
        guard let data = Data(base64Encoded: value) else { fatalError("Invalid fixture") }
        if mime == "text/plain" {
            guard let text = String(data: data, encoding: .utf8), item.setString(text, forType: .string) else { fatalError("Invalid Unicode fixture") }
        } else if !item.setData(data, forType: nativeType(mime)) { fatalError("Invalid fixture") }
    }
    clipboard.clearContents()
    guard clipboard.writeObjects([item]) else { fatalError("Native clipboard write refused") }
    try Data("ok".utf8).write(to: root.appendingPathComponent("peer-write.ready"))
    // Stay alive until the parent terminates this known peer process.
    RunLoop.main.run()
} else { exit(2) }
