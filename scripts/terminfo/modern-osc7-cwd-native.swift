import AppKit
import CoreGraphics
import Foundation
import Vision

struct Point: Codable, Equatable {
    let x: Double
    let y: Double

    init(_ point: CGPoint) {
        x = point.x
        y = point.y
    }

    var cgPoint: CGPoint { CGPoint(x: x, y: y) }
}

struct Window: Codable, Equatable {
    let id: Int
    let pid: Int
    let layer: Int
    let x: Double
    let y: Double
    let width: Double
    let height: Double

    init?(_ row: [String: Any], owner: Int) {
        guard let pid = row[kCGWindowOwnerPID as String] as? Int, pid == owner,
              let id = row[kCGWindowNumber as String] as? Int,
              let layer = row[kCGWindowLayer as String] as? Int,
              let bounds = row[kCGWindowBounds as String] as? [String: Double],
              let x = bounds["X"], let y = bounds["Y"],
              let width = bounds["Width"], let height = bounds["Height"] else { return nil }
        self.id = id
        self.pid = pid
        self.layer = layer
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }
}

struct State: Codable {
    let frontPid: Int
    let uptime: Double
    let pointer: Point
    let windows: [Window]
}

struct Line: Codable {
    let text: String
    let confidence: Float
    let bounds: [Double]
}

struct Reading: Codable {
    let original: Point
    let before: State
    let hover: State
    let captured: State
    let restored: State
    let window: Window
}

enum CaptureError: Error {
    case noState, notFrontmost, noWindow, captureFailed, warpFailed
    case windowChanged(String, [Window])
    case pointerChanged(String, Point, Point)
}

func state(_ pid: Int) throws -> State {
    guard let pointer = CGEvent(source: nil)?.location,
          let rows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
          let active = NSWorkspace.shared.frontmostApplication else { throw CaptureError.noState }
    guard Int(active.processIdentifier) == pid else { throw CaptureError.notFrontmost }
    return State(frontPid: pid, uptime: ProcessInfo.processInfo.systemUptime, pointer: Point(pointer), windows: rows.compactMap { Window($0, owner: pid) })
}

func move(_ point: CGPoint) throws {
    guard let event = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left)
    else { throw CaptureError.noState }
    guard CGWarpMouseCursorPosition(point) == .success else { throw CaptureError.warpFailed }
    event.post(tap: .cghidEventTap)
}

func recognize(_ handler: VNImageRequestHandler) throws -> [Line] {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.revision = VNRecognizeTextRequestRevision3
    request.usesLanguageCorrection = false
    request.recognitionLanguages = ["en-US"]
    try handler.perform([request])
    return (request.results ?? []).compactMap { result in
        guard let candidate = result.topCandidates(1).first else { return nil }
        let box = result.boundingBox
        return Line(text: candidate.string, confidence: candidate.confidence, bounds: [box.minX, box.minY, box.width, box.height])
    }
}

func capture(_ image: URL, in window: Window) throws {
    let screen = Process()
    screen.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    screen.arguments = ["-l", String(window.id), "-o", "-x", "-t", "png", image.path]
    try screen.run()
    screen.waitUntilExit()
    guard screen.terminationStatus == 0 else { throw CaptureError.captureFailed }
}

func near(_ lhs: Point, _ rhs: Point) -> Bool {
    abs(lhs.x - rhs.x) <= 1 && abs(lhs.y - rhs.y) <= 1
}

func checkPointer(_ stage: String, _ actual: Point, _ expected: Point) throws {
    guard near(actual, expected) else { throw CaptureError.pointerChanged(stage, actual, expected) }
}

func readTooltip(pid: Int, image: URL) throws -> Reading {
    let initial = try state(pid)
    let main = initial.windows.filter { $0.layer == 0 && $0.width >= 400 && $0.height >= 200 }
    guard main.count == 1, let tabWindow = main.first else { throw CaptureError.noWindow }
    // The owned profile uses the horizontal strip. Its first tab sits just to
    // the right of the macOS traffic-light reserve in the 36-point title band.
    let target = CGPoint(x: tabWindow.x + 112, y: tabWindow.y + 18)
    let outside = CGPoint(x: tabWindow.x + 112, y: tabWindow.y + 95)
    var lastPosted = outside
    defer {
        if let pointer = CGEvent(source: nil)?.location, near(Point(pointer), Point(lastPosted)) {
            try? move(initial.pointer.cgPoint)
        }
    }
    try move(outside)
    Thread.sleep(forTimeInterval: 0.15)
    let before = try state(pid)
    try checkPointer("outside", before.pointer, Point(outside))
    lastPosted = target
    try move(target)
    Thread.sleep(forTimeInterval: 0.8)
    let hover = try state(pid)
    try checkPointer("hover", hover.pointer, Point(target))
    let popups = hover.windows.filter { window in
        !before.windows.contains { $0.id == window.id }
            && abs(window.x - target.x) < 80 && abs(window.y - target.y) < 80
            && window.width > 20 && window.height > 10 && window.height < 120
    }
    guard popups.count == 1, let window = popups.first else { throw CaptureError.noWindow }
    try capture(image, in: window)
    let captured = try state(pid)
    try checkPointer("capture", captured.pointer, hover.pointer)
    for (stage, snapshot) in [("before", before), ("hover", hover), ("capture", captured)] {
        guard snapshot.windows.contains(tabWindow) else { throw CaptureError.windowChanged(stage, snapshot.windows) }
    }
    guard captured.windows.contains(window) else { throw CaptureError.windowChanged("tooltip", captured.windows) }
    try move(initial.pointer.cgPoint)
    Thread.sleep(forTimeInterval: 0.10)
    let restored = try state(pid)
    try checkPointer("restore", restored.pointer, initial.pointer)
    return Reading(original: initial.pointer, before: before, hover: hover, captured: captured,
                   restored: restored, window: window)
}

func warmReader() throws {
    let size = NSSize(width: 320, height: 64)
    let sample = NSImage(size: size, flipped: false) { bounds in
        NSColor.white.setFill()
        NSBezierPath(rect: bounds).fill()
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 22), .foregroundColor: NSColor.black]
        ("Text recognition warmup" as NSString).draw(at: NSPoint(x: 12, y: 20), withAttributes: attributes)
        return true
    }
    var bounds = NSRect(origin: .zero, size: size)
    guard let pixels = sample.cgImage(forProposedRect: &bounds, context: nil, hints: nil),
          !(try recognize(VNImageRequestHandler(cgImage: pixels))).isEmpty else { throw CaptureError.captureFailed }
}

func send<T: Encodable>(_ value: T, encoder: JSONEncoder) throws {
    var data = try encoder.encode(value)
    data.append(0x0a)
    FileHandle.standardOutput.write(data)
}

do {
    let args = CommandLine.arguments
    let encoder = JSONEncoder()
    if args.count == 2, args[1] == "--ocr-server" {
        try warmReader()
        try send("ready", encoder: encoder)
        while let input = readLine() {
            let path = try JSONDecoder().decode(String.self, from: Data(input.utf8))
            let handler = VNImageRequestHandler(url: URL(fileURLWithPath: path))
            try send(recognize(handler), encoder: encoder)
        }
    } else if args.count == 3, args[1] == "--ocr" {
        let handler = VNImageRequestHandler(url: URL(fileURLWithPath: args[2]))
        try send(recognize(handler), encoder: encoder)
    } else {
        guard args.count == 3, let pid = Int(args[1]), pid > 0 else { throw CaptureError.noState }
        let reading = try readTooltip(pid: pid, image: URL(fileURLWithPath: args[2]))
        try send(reading, encoder: encoder)
    }
} catch {
    FileHandle.standardError.write(Data(("Native tab tooltip capture: \(error)\n").utf8))
    exit(1)
}
