// screen-probe — record one app window's frames with exact timestamps (#1544).
//
// The reader speed bench needs to know WHEN each app's window changed after an
// input event, the same way for excise, Preview and Acrobat, without touching
// any of them. ScreenCaptureKit delivers a frame only when the window's
// content changed, stamped with the mach_absolute_time it was displayed, which
// is exactly the signal: the driver stamps each input event with
// mach_absolute_time too, and the analysis lines the two up offline.
//
// It records, and nothing else. Each frame is stored as a small greyscale image
// so every metric (first change, fully drawn, speed index, frame gaps) is
// computed afterwards and thresholds can be tuned without re-recording.
//
//   screen-probe --pid 1234 --out frames.bin [--scale 0.125] [--fps 60]
//   screen-probe --pid 1234 --out frames.bin --region X,Y,W,H
//
// --region (#1551-#1554) records a fixed screen rectangle (global points,
// top-left origin) showing only the app's windows, instead of one window.
// A window switch changes which window is in FRONT, not any window's content,
// so a single-window capture cannot see it; the multi-document speed runs
// stack every document window on one frame and record that frame.
//
// Output: a little-endian stream of records
//   UInt64 displayTime (mach ticks) | UInt32 width | UInt32 height | width*height bytes (luma)
// Stdout: one JSON line per event ("ready" after the first frame, "stopped").
// It stops on SIGTERM or SIGINT, or when stdin reaches EOF.

import AppKit
import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo

struct Options {
    var pid: pid_t = 0
    var out = ""
    var scale = 0.125
    var fps = 60
    var region: CGRect?
}

func parse() -> Options {
    var o = Options()
    var it = CommandLine.arguments.dropFirst().makeIterator()
    while let a = it.next() {
        switch a {
        case "--pid": o.pid = pid_t(it.next() ?? "") ?? 0
        case "--out": o.out = it.next() ?? ""
        case "--scale": o.scale = Double(it.next() ?? "") ?? o.scale
        case "--fps": o.fps = Int(it.next() ?? "") ?? o.fps
        case "--region":
            let parts = (it.next() ?? "").split(separator: ",").compactMap { Double($0) }
            guard parts.count == 4, parts[2] > 0, parts[3] > 0 else {
                FileHandle.standardError.write("--region needs X,Y,W,H\n".data(using: .utf8)!)
                exit(2)
            }
            o.region = CGRect(x: parts[0], y: parts[1], width: parts[2], height: parts[3])
        default:
            FileHandle.standardError.write("unknown argument \(a)\n".data(using: .utf8)!)
            exit(2)
        }
    }
    if o.pid == 0 || o.out.isEmpty {
        FileHandle.standardError.write("usage: screen-probe --pid N --out FILE [--scale S] [--fps N] [--region X,Y,W,H]\n".data(using: .utf8)!)
        exit(2)
    }
    return o
}

func emit(_ dict: [String: Any]) {
    if let data = try? JSONSerialization.data(withJSONObject: dict),
       let s = String(data: data, encoding: .utf8) {
        print(s)
        fflush(stdout)
    }
}

final class Recorder: NSObject, SCStreamOutput, SCStreamDelegate {
    let handle: FileHandle
    var frames = 0
    var skipped = 0
    var announced = false
    var previous = Data()
    let lock = NSLock()

    init(path: String) {
        FileManager.default.createFile(atPath: path, contents: nil)
        handle = FileHandle(forWritingAtPath: path)!
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let info = attachments.first,
              let rawStatus = info[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixels = CMSampleBufferGetImageBuffer(sb) else { return }

        let displayTime = (info[.displayTime] as? UInt64) ?? mach_absolute_time()
        CVPixelBufferLockBaseAddress(pixels, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixels, .readOnly) }
        // 420f: plane 0 is luma, which is all the analysis needs.
        let w = CVPixelBufferGetWidthOfPlane(pixels, 0)
        let h = CVPixelBufferGetHeightOfPlane(pixels, 0)
        let stride = CVPixelBufferGetBytesPerRowOfPlane(pixels, 0)
        guard let base = CVPixelBufferGetBaseAddressOfPlane(pixels, 0) else { return }

        var luma = Data(capacity: w * h)
        let src = base.assumingMemoryBound(to: UInt8.self)
        for row in 0..<h {
            luma.append(src + row * stride, count: w)
        }

        lock.lock()
        // ScreenCaptureKit delivers "complete" frames at the requested rate even
        // when nothing changed (199 frames in 4 s of an idle Preview window).
        // A frame identical to the last one carries no information, and
        // dropping it makes "a record exists" mean "the window changed".
        if announced && luma == previous {
            skipped += 1
            lock.unlock()
            return
        }
        previous = luma
        var record = Data(capacity: 16 + w * h)
        withUnsafeBytes(of: displayTime.littleEndian) { record.append(contentsOf: $0) }
        withUnsafeBytes(of: UInt32(w).littleEndian) { record.append(contentsOf: $0) }
        withUnsafeBytes(of: UInt32(h).littleEndian) { record.append(contentsOf: $0) }
        record.append(luma)
        handle.write(record)
        frames += 1
        let first = !announced
        announced = true
        lock.unlock()
        if first { emit(["event": "ready", "width": w, "height": h, "displayTime": displayTime]) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        emit(["event": "error", "message": "\(error)"])
        exit(1)
    }
}

let opts = parse()
// ScreenCaptureKit asserts (CGS_REQUIRE_INIT) in a process with no window-server
// connection. NSApplication makes one; .prohibited keeps the probe out of the
// Dock and unable to take focus from the app being measured.
_ = NSApplication.shared
NSApp.setActivationPolicy(.prohibited)
let recorder = Recorder(path: opts.out)
var activeStream: SCStream?

func stop(_ reason: String) {
    let s = activeStream
    let finish = {
        recorder.lock.lock()
        try? recorder.handle.synchronize()
        try? recorder.handle.close()
        let n = recorder.frames
        let k = recorder.skipped
        recorder.lock.unlock()
        emit(["event": "stopped", "reason": reason, "frames": n, "skippedUnchanged": k])
        exit(0)
    }
    if let s = s {
        s.stopCapture { _ in finish() }
    } else {
        finish()
    }
}

for sig in [SIGTERM, SIGINT] {
    signal(sig, SIG_IGN)
    let src = DispatchSource.makeSignalSource(signal: sig, queue: .main)
    src.setEventHandler { stop("signal \(sig)") }
    src.resume()
    // Keep the source alive for the life of the process.
    objc_setAssociatedObject(recorder, UnsafeRawPointer(bitPattern: Int(sig))!, src, .OBJC_ASSOCIATION_RETAIN)
}

// EOF on stdin is the other stop signal, so a driver that dies takes the probe with it.
DispatchQueue.global().async {
    while readLine() != nil {}
    DispatchQueue.main.async { stop("stdin closed") }
}

Task {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let config = SCStreamConfiguration()
        let filter: SCContentFilter
        var started: [String: Any] = ["event": "started"]
        if let region = opts.region {
            guard let app = content.applications.first(where: { $0.processID == opts.pid }) else {
                emit(["event": "error", "message": "no application with pid \(opts.pid)"])
                exit(1)
            }
            let centre = CGPoint(x: region.midX, y: region.midY)
            guard let display = content.displays.first(where: { $0.frame.contains(centre) }) else {
                emit(["event": "error", "message": "no display contains the region centre"])
                exit(1)
            }
            filter = SCContentFilter(display: display, including: [app], exceptingWindows: [])
            config.sourceRect = CGRect(x: region.minX - display.frame.minX, y: region.minY - display.frame.minY,
                                       width: region.width, height: region.height)
            config.width = max(16, Int(region.width * opts.scale * 2))
            config.height = max(16, Int(region.height * opts.scale * 2))
            started["displayID"] = display.displayID
            started["region"] = [region.origin.x, region.origin.y, region.width, region.height]
        } else {
            // The app's LARGEST normal window: Acrobat's hover tooltip is a window too.
            let candidates = content.windows.filter {
                $0.owningApplication?.processID == opts.pid && $0.windowLayer == 0
            }
            guard let window = candidates.max(by: { $0.frame.width * $0.frame.height < $1.frame.width * $1.frame.height }) else {
                emit(["event": "error", "message": "no on-screen window for pid \(opts.pid)"])
                exit(1)
            }
            filter = SCContentFilter(desktopIndependentWindow: window)
            config.width = max(16, Int(window.frame.width * opts.scale * 2))
            config.height = max(16, Int(window.frame.height * opts.scale * 2))
            started["windowID"] = window.windowID
            started["frame"] = [window.frame.origin.x, window.frame.origin.y, window.frame.width, window.frame.height]
        }
        config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(opts.fps))
        config.queueDepth = 5
        config.showsCursor = false
        let stream = SCStream(filter: filter, configuration: config, delegate: recorder)
        try stream.addStreamOutput(recorder, type: .screen,
                                   sampleHandlerQueue: DispatchQueue(label: "screen-probe.frames"))
        try await stream.startCapture()
        activeStream = stream
        started["captureSize"] = [config.width, config.height]
        emit(started)
    } catch {
        emit(["event": "error", "message": "\(error)"])
        exit(1)
    }
}

dispatchMain()
