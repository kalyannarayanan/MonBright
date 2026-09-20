//
//  setter.swift
//  MonBright — DDC/CI + DisplayServices CLI helper
//
//  Author: Kalyan
//
// Usage:
//   setter list                       -> prints "<id>\t<name>" per display
//   setter set <id> <0-100>           -> set brightness for given display
//   setter <0-100>                    -> legacy: set first external monitor
//
// IDs:
//   "internal"          -> the Mac's built-in display (only one per machine)
//   "<MANU>-<PC>-<SN>"  -> external monitor, identified from its EDID

import Foundation
import IOKit
import CoreGraphics
import Darwin

// External brightness — via IOAVService (DDC/CI over the cable).
@_silgen_name("IOAVServiceCreateWithService")
func IOAVServiceCreateWithService(
    _ allocator: CFAllocator?,
    _ service: io_service_t
) -> Unmanaged<AnyObject>?

@_silgen_name("IOAVServiceWriteI2C")
func IOAVServiceWriteI2C(
    _ service: AnyObject,
    _ chipAddress: UInt32,
    _ offset: UInt32,
    _ inputBuffer: UnsafeRawPointer,
    _ inputBufferSize: UInt32
) -> kern_return_t

@_silgen_name("IOAVServiceReadI2C")
func IOAVServiceReadI2C(
    _ service: AnyObject,
    _ chipAddress: UInt32,
    _ offset: UInt32,
    _ outputBuffer: UnsafeMutableRawPointer,
    _ outputBufferSize: UInt32
) -> kern_return_t

@_silgen_name("IOAVServiceCopyEDID")
func IOAVServiceCopyEDID(
    _ service: AnyObject,
    _ edid: UnsafeMutablePointer<Unmanaged<CFData>?>
) -> kern_return_t

// Internal brightness — via DisplayServices private framework. The system
// uses these same calls when you press F1/F2.
@_silgen_name("DisplayServicesSetBrightness")
func DisplayServicesSetBrightness(_ display: CGDirectDisplayID, _ brightness: Float) -> Int32

@_silgen_name("DisplayServicesGetBrightness")
func DisplayServicesGetBrightness(_ display: CGDirectDisplayID, _ brightness: UnsafeMutablePointer<Float>) -> Int32

struct ExternalMonitor {
    let av: AnyObject
    let id: String
    let name: String
}

func parseEDID(_ data: Data) -> (id: String, name: String)? {
    guard data.count >= 128 else { return nil }

    let manuRaw = (UInt16(data[8]) << 8) | UInt16(data[9])
    let c1 = UInt8(((manuRaw >> 10) & 0x1F) + 0x40)
    let c2 = UInt8(((manuRaw >> 5) & 0x1F) + 0x40)
    let c3 = UInt8((manuRaw & 0x1F) + 0x40)
    let manu = String(bytes: [c1, c2, c3], encoding: .ascii) ?? "???"

    let pc = (UInt16(data[11]) << 8) | UInt16(data[10])
    let sn = (UInt32(data[15]) << 24) | (UInt32(data[14]) << 16) | (UInt32(data[13]) << 8) | UInt32(data[12])

    var name: String? = nil
    for offset in stride(from: 54, to: 109, by: 18) {
        if data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 0 && data[offset + 3] == 0xFC {
            let bytes = data[(offset + 5)..<(offset + 18)]
            let raw = String(bytes: bytes, encoding: .ascii) ?? ""
            let firstLine = raw.components(separatedBy: "\n").first ?? raw
            let trimmed = firstLine.trimmingCharacters(in: .whitespaces)
            if !trimmed.isEmpty {
                name = trimmed
                break
            }
        }
    }

    let id = "\(manu)-\(String(format: "%04X", pc))-\(String(format: "%08X", sn))"
    return (id, name ?? "External Display")
}

func findExternalMonitors() -> [ExternalMonitor] {
    let matching = IOServiceMatching("DCPAVServiceProxy")
    var iter: io_iterator_t = 0
    guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iter) == KERN_SUCCESS else {
        return []
    }
    defer { IOObjectRelease(iter) }

    var result: [ExternalMonitor] = []
    var fallbackIndex = 0
    var service = IOIteratorNext(iter)
    while service != 0 {
        let loc = IORegistryEntryCreateCFProperty(
            service, "Location" as CFString, kCFAllocatorDefault, 0
        )?.takeRetainedValue() as? String

        if loc == "External",
           let av = IOAVServiceCreateWithService(kCFAllocatorDefault, service)?.takeRetainedValue() {

            var edidRef: Unmanaged<CFData>? = nil
            let kr = IOAVServiceCopyEDID(av, &edidRef)
            if kr == KERN_SUCCESS,
               let edidData = edidRef?.takeRetainedValue() as Data?,
               let parsed = parseEDID(edidData) {
                result.append(ExternalMonitor(av: av, id: parsed.id, name: parsed.name))
            } else {
                result.append(ExternalMonitor(av: av, id: "unknown-\(fallbackIndex)", name: "External Display"))
                fallbackIndex += 1
            }
        }
        IOObjectRelease(service)
        service = IOIteratorNext(iter)
    }
    return result
}

func findBuiltinDisplay() -> CGDirectDisplayID? {
    let maxN: UInt32 = 16
    var displays = [CGDirectDisplayID](repeating: 0, count: Int(maxN))
    var count: UInt32 = 0
    guard CGGetActiveDisplayList(maxN, &displays, &count) == .success else { return nil }
    for i in 0..<Int(count) {
        if CGDisplayIsBuiltin(displays[i]) != 0 {
            return displays[i]
        }
    }
    return nil
}

// Ask the monitor what its luminance range tops out at. Monitors are not all
// 0-100: some report 0-255, and a fixed 100 would only ever reach ~39% of
// those. DDC "Get VCP Feature" for 0x10 replies with max and current;
// we only need max. Returns nil if the monitor doesn't answer sensibly.
func readLuminanceMax(_ av: AnyObject) -> UInt16? {
    var req: [UInt8] = [0x82, 0x01, 0x10, 0]
    var chk: UInt8 = 0x6E ^ 0x51
    for i in 0..<3 { chk ^= req[i] }
    req[3] = chk

    for attempt in 0..<2 {
        let wrote = req.withUnsafeBufferPointer { buf -> kern_return_t in
            IOAVServiceWriteI2C(av, 0x37, 0x51, buf.baseAddress!, UInt32(buf.count))
        }
        guard wrote == KERN_SUCCESS else { continue }
        // DDC/CI gives the monitor 40 ms to prepare its reply.
        usleep(40_000)

        var reply = [UInt8](repeating: 0, count: 11)
        let read = reply.withUnsafeMutableBufferPointer { buf -> kern_return_t in
            IOAVServiceReadI2C(av, 0x37, 0x51, buf.baseAddress!, UInt32(buf.count))
        }
        // [0]=src [1]=0x88 len [2]=0x02 reply [3]=result [4]=vcp [5]=type [6..7]=max [8..9]=cur [10]=chk
        if read == KERN_SUCCESS, reply[1] == 0x88, reply[2] == 0x02, reply[3] == 0x00, reply[4] == 0x10 {
            let maxValue = (UInt16(reply[6]) << 8) | UInt16(reply[7])
            if maxValue > 0 { return maxValue }
        }
        if attempt == 0 { usleep(20_000) }
    }
    return nil
}

func writeExternalBrightness(_ av: AnyObject, value: Int) -> kern_return_t {
    let percent = max(0, min(100, value))
    // Scale to the monitor's own range; fall back to 0-100 if it won't tell us,
    // which is exactly what every write did before this existed.
    let top = readLuminanceMax(av) ?? 100
    let raw = UInt16((Double(percent) / 100.0 * Double(top)).rounded())
    var data: [UInt8] = [
        0x84,
        0x03,
        0x10,
        UInt8((raw >> 8) & 0xFF),
        UInt8(raw & 0xFF),
        0
    ]
    var chk: UInt8 = 0x6E ^ 0x51
    for i in 0..<5 { chk ^= data[i] }
    data[5] = chk
    return data.withUnsafeBufferPointer { buf -> kern_return_t in
        IOAVServiceWriteI2C(av, 0x37, 0x51, buf.baseAddress!, UInt32(buf.count))
    }
}

func setInternalBrightness(value: Int) -> Bool {
    guard let display = findBuiltinDisplay() else { return false }
    let brightness = Float(max(0, min(100, value))) / 100.0
    return DisplayServicesSetBrightness(display, brightness) == 0
}

// CLI dispatch
let args = CommandLine.arguments

if args.count >= 2 && args[1] == "list" {
    if findBuiltinDisplay() != nil {
        print("internal\tBuilt-in Display")
    }
    for m in findExternalMonitors() {
        print("\(m.id)\t\(m.name)")
    }
    exit(0)
}

if args.count >= 4 && args[1] == "set" {
    let id = args[2]
    guard let value = Int(args[3]) else {
        FileHandle.standardError.write(Data("Invalid value\n".utf8))
        exit(2)
    }
    if id == "internal" {
        exit(setInternalBrightness(value: value) ? 0 : 5)
    }
    let monitors = findExternalMonitors()
    guard let m = monitors.first(where: { $0.id == id }) else { exit(4) }
    exit(writeExternalBrightness(m.av, value: value) == KERN_SUCCESS ? 0 : 5)
}

// Legacy single-argument form — apply to the first external monitor.
if args.count >= 2, let value = Int(args[1]) {
    guard let first = findExternalMonitors().first else { exit(4) }
    exit(writeExternalBrightness(first.av, value: value) == KERN_SUCCESS ? 0 : 5)
}

FileHandle.standardError.write(Data("usage: setter list | setter set <id> <0-100> | setter <0-100>\n".utf8))
exit(2)

// Exit codes:
// 0  success
// 2  bad argv
// 3  IOServiceGetMatchingServices failed (unused now)
// 4  display id not found
// 5  set failed (DisplayServices non-zero or I2C non-zero)
