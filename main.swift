//
//  main.swift
//  MonBright — menu-bar app
//
//  Author: Kalyan
//

import Cocoa
import SwiftUI
import Combine
import Carbon.HIToolbox

// MARK: - Debug log
// Writes to /tmp/monbright.log so we can diagnose without stdout.
private let logURL = URL(fileURLWithPath: "/tmp/monbright.log")
private let logQueue = DispatchQueue(label: "monbright.log")
private let logStart: Date = {
    try? "".write(to: logURL, atomically: true, encoding: .utf8)
    return Date()
}()
func dlog(_ message: String) {
    let ts = String(format: "%.3f", Date().timeIntervalSince(logStart))
    let line = "[\(ts)] \(message)\n"
    logQueue.async {
        if let handle = try? FileHandle(forWritingTo: logURL) {
            handle.seekToEndOfFile()
            handle.write(line.data(using: .utf8)!)
            try? handle.close()
        }
    }
}

// MARK: - DDC bridge
//
// Brightness writes (and the monitor list) happen via the `setter` CLI
// binary that lives next to this executable. AppKit-linked Swift binaries
// see DCPAVServiceProxy property tables as empty on this machine — a
// plain CLI without AppKit sees the full IORegistry view. So we shell
// out for the privileged I/O.
enum DDC {
    private static var setterURL: URL {
        let exec = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let dir = (exec as NSString).deletingLastPathComponent
        return URL(fileURLWithPath: (dir as NSString).appendingPathComponent("setter"))
    }

    /// Returns one entry per connected display:
    ///   - id `"internal"` for the built-in display, with name refined to
    ///     `NSScreen.localizedName` (e.g. "Built-in Liquid Retina XDR Display")
    ///   - EDID-derived id and name for each external monitor
    static func list() -> [(id: String, name: String)] {
        let proc = Process()
        proc.executableURL = setterURL
        proc.arguments = ["list"]
        let pipe = Pipe()
        proc.standardOutput = pipe
        proc.standardError = FileHandle.nullDevice
        var entries: [(id: String, name: String)] = []
        do {
            try proc.run()
            proc.waitUntilExit()
            guard proc.terminationStatus == 0 else { return [] }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            guard let str = String(data: data, encoding: .utf8) else { return [] }
            entries = str.split(separator: "\n").compactMap { line in
                let parts = line.split(separator: "\t", maxSplits: 1)
                guard parts.count == 2 else { return nil }
                return (id: String(parts[0]), name: String(parts[1]))
            }
        } catch {
            dlog("DDC.list spawn failed: \(error)")
            return []
        }
        // Refine the built-in display's name using NSScreen.localizedName.
        if let idx = entries.firstIndex(where: { $0.id == "internal" }) {
            for screen in NSScreen.screens {
                if let num = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
                   CGDisplayIsBuiltin(num.uint32Value) != 0 {
                    entries[idx] = (id: "internal", name: screen.localizedName)
                    break
                }
            }
        }
        return entries
    }

    static func setBrightness(monitorID: String, value: Int) -> Bool {
        let proc = Process()
        proc.executableURL = setterURL
        proc.arguments = ["set", monitorID, String(max(0, min(100, value)))]
        proc.standardOutput = FileHandle.nullDevice
        proc.standardError = FileHandle.nullDevice
        do {
            try proc.run()
            proc.waitUntilExit()
            let code = proc.terminationStatus
            if code != 0 {
                dlog("setBrightness(\(monitorID), \(value)): setter exit=\(code)")
            }
            return code == 0
        } catch {
            dlog("setBrightness spawn failed: \(error)")
            return false
        }
    }
}

// MARK: - Brightness curve
//
// One slider, two mechanisms. DDC bottoms out at the panel's minimum
// backlight — VCP 0x10 = 0 means "dimmest the hardware supports", not
// "off", which on a typical IPS panel is still 40-60 nits. Below the
// crossover we pin the hardware at 0 and scale the gamma ramp instead,
// which is the only way to get darker than the panel's floor.

enum Brightness {
    /// Slider position where the hardware bottoms out. Below this, DDC is
    /// pinned at 0 and gamma does the work.
    static let crossover = 30.0

    /// Darkest software scale. ponytail: one constant for everyone — make it
    /// a preference only if real users disagree about it.
    static let gammaFloor = 0.25

    /// Split a 0-100 slider position into a DDC value and a gamma scale.
    /// Continuous at `crossover`: both branches yield (ddc: 0, gamma: 1.0),
    /// so there is no visible step as the slider crosses it.
    static func split(_ slider: Double) -> (ddc: Int, gamma: Double) {
        let s = max(0, min(100, slider))
        if s >= crossover {
            return (Int((((s - crossover) / (100 - crossover)) * 100).rounded()), 1.0)
        }
        return (0, gammaFloor + (1 - gammaFloor) * (s / crossover))
    }

    /// Inverse of the hardware branch — converts a v1.0.0 stored DDC value to
    /// the slider position that now produces it.
    static func sliderForDDC(_ ddc: Double) -> Double {
        crossover + (max(0, min(100, ddc)) / 100) * (100 - crossover)
    }

    /// Runnable check: `MonBright --selftest`. Uses precondition, not assert,
    /// because build.sh compiles with -O and asserts would vanish from the
    /// shipped binary.
    static func selfTest() {
        let lo = split(0), cross = split(crossover), hi = split(100)
        precondition(lo.ddc == 0 && abs(lo.gamma - gammaFloor) < 1e-9, "bottom must be (0, gammaFloor)")
        precondition(cross.ddc == 0 && abs(cross.gamma - 1.0) < 1e-9, "crossover must be (0, 1.0)")
        precondition(hi.ddc == 100 && abs(hi.gamma - 1.0) < 1e-9, "top must be (100, 1.0)")

        // No seam: approaching the crossover from below lands on the same point.
        let below = split(crossover - 0.001)
        precondition(below.ddc == 0 && abs(below.gamma - 1.0) < 0.001, "seam at crossover")

        // Perceived brightness rises monotonically across the whole range.
        var prev = -1.0
        for i in 0...1000 {
            let s = Double(i) / 10
            let p = split(s)
            let perceived = p.gamma * (Double(p.ddc) + 1)
            precondition(perceived >= prev - 1e-9, "non-monotonic at slider \(s)")
            prev = perceived
        }

        // Migrating a v1.0.0 DDC value round-trips back to the same DDC.
        for ddc in stride(from: 0.0, through: 100.0, by: 5.0) {
            let round = Double(split(sliderForDDC(ddc)).ddc)
            precondition(abs(round - ddc) < 1.0, "migration drift at \(ddc): got \(round)")
        }

        // The whole bottom slice maps to DDC 0 — that is the point when gamma
        // is available, and exactly why monitors without it must bypass split()
        // rather than inherit a slider whose bottom third does nothing.
        precondition(split(0).ddc == 0 && split(crossover * 0.99).ddc == 0,
                     "bottom of range must be DDC 0")

        print("selftest OK — crossover \(crossover), floor \(gammaFloor)")
    }
}

// MARK: - Gamma bridge
//
// Software dimming below the hardware floor. Unlike the DDC path this stays
// in-process on purpose: CoreGraphics reverts a process's gamma ramp when that
// process exits, so a ramp set inside the short-lived `setter` would vanish
// before it was ever seen. That also means quitting MonBright always restores
// the display — there is no way to leave a machine dimmed.
//
// Gamma is applied after the framebuffer, so it is invisible to screenshots
// and screen sharing. A translucent overlay window would be fewer lines but
// would dim what the user broadcasts on a call.

enum Gamma {
    static func apply(monitorID: String, scale: Double) {
        guard let display = displayID(for: monitorID) else { return }
        let v = CGGammaValue(max(0, min(1, scale)))
        // Scaling *max* with a 1.0 exponent dims linearly; bending the
        // exponent instead would shift midtones and skew color.
        let err = CGSetDisplayTransferByFormula(display, 0, v, 1.0, 0, v, 1.0, 0, v, 1.0)
        if err != .success {
            dlog("gamma \(monitorID) scale=\(scale): CGError \(err.rawValue)")
        }
    }

    static func restoreAll() {
        CGDisplayRestoreColorSyncSettings()
    }

    /// Map a setter-style EDID id ("MANU-PPPP-SSSSSSSS") to a CGDirectDisplayID.
    static func displayID(for monitorID: String) -> CGDirectDisplayID? {
        // "unknown-N" ids come from setter.swift:113 when EDID parsing failed.
        // They are positional and change with plug order, so matching one could
        // dim a different monitor than the user is adjusting. Refuse instead.
        guard monitorID != "internal", !monitorID.hasPrefix("unknown-") else { return nil }

        var ids = [CGDirectDisplayID](repeating: 0, count: 16)
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(16, &ids, &count) == .success else { return nil }
        let external = (0..<Int(count)).map { ids[$0] }.filter { CGDisplayIsBuiltin($0) == 0 }

        if let match = external.first(where: { edidID($0) == monitorID }) { return match }
        // Fallback for machines where the CG accessors return unhelpful values:
        // only safe when there is exactly one external display to confuse.
        return external.count == 1 ? external[0] : nil
    }

    /// Rebuild the same id string setter.swift:83 derives from raw EDID bytes,
    /// using the identical 5-bit-letter decode from setter.swift:60-64.
    private static func edidID(_ d: CGDirectDisplayID) -> String {
        let raw = UInt16(truncatingIfNeeded: CGDisplayVendorNumber(d))
        let c1 = UInt8(((raw >> 10) & 0x1F) + 0x40)
        let c2 = UInt8(((raw >> 5) & 0x1F) + 0x40)
        let c3 = UInt8((raw & 0x1F) + 0x40)
        let manu = String(bytes: [c1, c2, c3], encoding: .ascii) ?? "???"
        return "\(manu)-\(String(format: "%04X", CGDisplayModelNumber(d)))-\(String(format: "%08X", CGDisplaySerialNumber(d)))"
    }
}

// MARK: - Monitor model (one per connected external monitor)

final class Monitor: ObservableObject, Identifiable {
    let id: String
    let name: String
    @Published var brightness: Double
    @Published var available: Bool = true

    private let ddcQueue: DispatchQueue
    private var pendingWrite: DispatchWorkItem?
    private var cancellables = Set<AnyCancellable>()

    /// Whether this monitor's slider spans hardware *plus* software range.
    /// False means the plain v1.0.0 scale, where the slider maps straight to
    /// DDC. Doubles as the meaning of the stored value: true = sub-zero scale,
    /// false = raw DDC, which keeps the two from ever disagreeing.
    private let usesSoftwareScale: Bool

    init(id: String, name: String) {
        self.id = id
        self.name = name
        self.ddcQueue = DispatchQueue(label: "monbright.ddc.\(id)")
        let key = "brightness.\(id)"
        var stored = UserDefaults.standard.object(forKey: key) as? Double ?? 50

        // Software dimming needs a CGDirectDisplayID. Monitors we can't resolve
        // — unparseable EDID, or an ambiguous match with several externals
        // attached — keep the plain DDC scale. Without this their bottom third
        // would send DDC 0 the whole way down and the slider would do nothing.
        // Built-ins resolve to nil too, so they take this path as before.
        let software = Gamma.displayID(for: id) != nil
        self.usesSoftwareScale = software

        // v1.0.0 stored a raw DDC value. The slider now spans hardware plus
        // software range, so the same number reads dimmer than it used to —
        // remap it once per monitor so upgrading doesn't darken anyone's
        // screen. Persisted immediately alongside the flag so a crash before
        // the first write can't leave the flag set with an unmigrated value.
        //
        // ponytail: a monitor that gains resolvability later (its sibling gets
        // unplugged) won't migrate until the next launch, since rebuildMenu
        // preserves Monitor instances. Self-corrects on the next drag.
        let migrationKey = "migrated.subzero.v1.\(id)"
        if software, !UserDefaults.standard.bool(forKey: migrationKey) {
            stored = Brightness.sliderForDDC(stored)
            UserDefaults.standard.set(stored, forKey: key)
            UserDefaults.standard.set(true, forKey: migrationKey)
        }
        self.brightness = stored

        // Coalesce slider drags into one write 50 ms after the latest change.
        $brightness
            .removeDuplicates { abs($0 - $1) < 0.5 }
            .dropFirst()
            .sink { [weak self] value in
                self?.scheduleWrite(value: value)
            }
            .store(in: &cancellables)

        // Apply persisted value 500 ms after init.
        DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(500)) { [weak self] in
            guard let self = self else { return }
            self.scheduleWrite(value: self.brightness)
        }
    }

    /// Re-push the gamma ramp. Display reconfiguration (sleep/wake, replug,
    /// resolution change) drops it, and the Monitor instance survives those,
    /// so nothing else would restore it.
    func reapplyGamma() {
        guard usesSoftwareScale else { return }
        Gamma.apply(monitorID: id, scale: Brightness.split(brightness).gamma)
    }

    private func scheduleWrite(value: Double) {
        pendingWrite?.cancel()
        let id = self.id
        let software = usesSoftwareScale
        let item = DispatchWorkItem { [weak self] in
            // Identity mapping when software dimming isn't available, so the
            // slider still covers the monitor's full DDC range end to end.
            var ddc = Int(value.rounded())
            if software {
                let split = Brightness.split(value)
                ddc = split.ddc
                DispatchQueue.main.async { Gamma.apply(monitorID: id, scale: split.gamma) }
            }
            let ok = DDC.setBrightness(monitorID: id, value: ddc)
            UserDefaults.standard.set(value, forKey: "brightness.\(id)")
            // Gamma failure deliberately doesn't mark the monitor unavailable —
            // hardware control still works, and the menu shouldn't grey out.
            DispatchQueue.main.async { self?.available = ok }
        }
        pendingWrite = item
        ddcQueue.asyncAfter(deadline: .now() + .milliseconds(50), execute: item)
    }
}

// MARK: - SwiftUI rows hosted inside NSMenuItems

struct HeaderRow: View {
    @ObservedObject var monitor: Monitor
    var body: some View {
        HStack {
            Text(monitor.name)
                .font(.headline)
            Spacer()
            Text("\(Int(monitor.brightness.rounded()))%")
                .font(.subheadline)
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 4)
        .frame(width: 260)
    }
}

struct SliderRow: View {
    @ObservedObject var monitor: Monitor

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "sun.min.fill").foregroundStyle(.secondary)
            Slider(value: $monitor.brightness, in: 0...100)
            Image(systemName: "sun.max.fill").foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 2)
        .frame(width: 260)
    }
}


// MARK: - App delegate

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var monitors: [Monitor] = []
    private var rebuildDebouncer: DispatchWorkItem?
    private var hotKeyRefs: [EventHotKeyRef?] = []
    private var repeatTimer: Timer?
    private var activeHotKey: UInt32 = 0

    private static let hotKeySignature: OSType = 0x4D4F4E42 // 'MONB'
    private static let hotKeyIDUp: UInt32 = 1
    private static let hotKeyIDDown: UInt32 = 2
    private static let brightnessStep: Double = 5
    private static let repeatInterval: TimeInterval = 0.1

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "display", accessibilityDescription: "Monitor brightness")
            button.image?.isTemplate = true
        }

        rebuildMenu()
        installHotKeys()

        NotificationCenter.default.addObserver(
            self,
            selector: #selector(displaysChanged),
            name: NSApplication.didChangeScreenParametersNotification,
            object: nil
        )
    }

    // MARK: Global hotkeys (Carbon RegisterEventHotKey)
    //
    // F2 brightens, F1 dims the display the cursor is currently on,
    // in 5% steps. Carbon hotkeys are OS-level — they need no
    // Accessibility permission, no event-tap entitlement, nothing a
    // security review would flag.
    //
    // Note: on default Mac keyboard settings, F1/F2 alone send the
    // system's brightness media keys, which macOS catches before us.
    // To use this shortcut, either press Fn+F1 / Fn+F2 or enable
    // "Use F1, F2, etc. keys as standard function keys" in System
    // Settings → Keyboard.

    private func installHotKeys() {
        // Register both Pressed AND Released so we can drive an
        // auto-repeat Timer for "hold to keep adjusting" — Carbon
        // hotkeys don't repeat on their own.
        var specs = [
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased))
        ]
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(
            GetApplicationEventTarget(),
            { (_, event, userData) -> OSStatus in
                guard let event = event, let userData = userData else { return noErr }
                var id = EventHotKeyID()
                GetEventParameter(event,
                                  EventParamName(kEventParamDirectObject),
                                  EventParamType(typeEventHotKeyID),
                                  nil,
                                  MemoryLayout<EventHotKeyID>.size,
                                  nil,
                                  &id)
                let kind = GetEventKind(event)
                let delegate = Unmanaged<AppDelegate>.fromOpaque(userData).takeUnretainedValue()
                DispatchQueue.main.async {
                    if kind == UInt32(kEventHotKeyPressed) {
                        delegate.beginHotKeyRepeat(id: id.id)
                    } else {
                        delegate.endHotKeyRepeat(id: id.id)
                    }
                }
                return noErr
            },
            specs.count,
            &specs,
            selfPtr,
            nil
        )

        var upRef: EventHotKeyRef?
        RegisterEventHotKey(
            UInt32(kVK_F2),
            0,
            EventHotKeyID(signature: Self.hotKeySignature, id: Self.hotKeyIDUp),
            GetApplicationEventTarget(),
            0,
            &upRef
        )
        hotKeyRefs.append(upRef)

        var downRef: EventHotKeyRef?
        RegisterEventHotKey(
            UInt32(kVK_F1),
            0,
            EventHotKeyID(signature: Self.hotKeySignature, id: Self.hotKeyIDDown),
            GetApplicationEventTarget(),
            0,
            &downRef
        )
        hotKeyRefs.append(downRef)
    }

    fileprivate func beginHotKeyRepeat(id: UInt32) {
        repeatTimer?.invalidate()
        activeHotKey = id
        applyHotKey(id: id) // immediate first step
        repeatTimer = Timer.scheduledTimer(withTimeInterval: Self.repeatInterval, repeats: true) { [weak self] _ in
            guard let self = self, self.activeHotKey == id else { return }
            self.applyHotKey(id: id)
        }
    }

    fileprivate func endHotKeyRepeat(id: UInt32) {
        guard activeHotKey == id else { return }
        repeatTimer?.invalidate()
        repeatTimer = nil
        activeHotKey = 0
    }

    private func applyHotKey(id: UInt32) {
        guard !monitors.isEmpty else { return }
        let target = monitorUnderCursor() ?? monitors.first!
        let delta: Double = (id == Self.hotKeyIDUp) ? Self.brightnessStep : -Self.brightnessStep
        target.brightness = max(0, min(100, target.brightness + delta))
    }

    private func monitorUnderCursor() -> Monitor? {
        let cursor = NSEvent.mouseLocation
        guard let screen = NSScreen.screens.first(where: { $0.frame.contains(cursor) }),
              let num = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            return nil
        }
        let displayID = num.uint32Value
        if CGDisplayIsBuiltin(displayID) != 0 {
            return monitors.first(where: { $0.id == "internal" })
        }
        return monitors.first(where: { $0.name == screen.localizedName })
    }

    @objc private func displaysChanged() {
        // Debounce — the notification fires several times for one logical change.
        rebuildDebouncer?.cancel()
        let item = DispatchWorkItem { [weak self] in self?.rebuildMenu() }
        rebuildDebouncer = item
        DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(300), execute: item)
    }

    private func rebuildMenu() {
        let detected = DDC.list()

        // Preserve Monitor instances whose ids are still present so their
        // in-memory state (cancellables, in-flight writes) survives a
        // resolution change.
        let existing = Dictionary(uniqueKeysWithValues: monitors.map { ($0.id, $0) })
        monitors = detected.map { d in
            existing[d.id] ?? Monitor(id: d.id, name: d.name)
        }

        // Preserved instances kept their brightness but lost their gamma ramp
        // to the display reconfiguration that got us here. Fresh ones apply it
        // via init's deferred write; re-pushing is idempotent either way.
        monitors.forEach { $0.reapplyGamma() }

        let menu = NSMenu()
        menu.autoenablesItems = false

        if monitors.isEmpty {
            let empty = NSMenuItem(title: "No external monitor connected",
                                   action: nil, keyEquivalent: "")
            empty.isEnabled = false
            menu.addItem(empty)
        } else {
            for (i, monitor) in monitors.enumerated() {
                if i > 0 {
                    menu.addItem(NSMenuItem.separator())
                }
                let headerItem = NSMenuItem()
                let headerHosting = NSHostingView(rootView: HeaderRow(monitor: monitor))
                headerHosting.frame = NSRect(x: 0, y: 0, width: 260, height: 28)
                headerItem.view = headerHosting
                menu.addItem(headerItem)

                let sliderItem = NSMenuItem()
                let sliderHosting = NSHostingView(rootView: SliderRow(monitor: monitor))
                sliderHosting.frame = NSRect(x: 0, y: 0, width: 260, height: 28)
                sliderItem.view = sliderHosting
                menu.addItem(sliderItem)
            }

            // Shortcut hints — disabled NSMenuItems whose `keyEquivalent`
            // still renders in Apple's native ⌘Q-style mini-pill on the
            // right. They look like greyed-out informational rows rather
            // than clickable buttons. The actual key handling continues
            // to be done globally by the Carbon hotkey.
            menu.addItem(NSMenuItem.separator())
            let f1 = String(Character(UnicodeScalar(NSF1FunctionKey)!))
            let f2 = String(Character(UnicodeScalar(NSF2FunctionKey)!))

            let brightenHint = NSMenuItem(title: "Brighten (hold fn)", action: nil, keyEquivalent: f2)
            brightenHint.keyEquivalentModifierMask = []
            brightenHint.isEnabled = false
            menu.addItem(brightenHint)

            let dimHint = NSMenuItem(title: "Dim (hold fn)", action: nil, keyEquivalent: f1)
            dimHint.keyEquivalentModifierMask = []
            dimHint.isEnabled = false
            menu.addItem(dimHint)
        }

        menu.addItem(NSMenuItem.separator())

        let quitItem = NSMenuItem(title: "Quit MonBright",
                                  action: #selector(quit),
                                  keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)

        statusItem.menu = menu
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    func applicationWillTerminate(_ notification: Notification) {
        // CoreGraphics would restore these on exit anyway; doing it explicitly
        // means the display is back to normal before the process goes away.
        Gamma.restoreAll()
    }
}

// MARK: - Entry point

if CommandLine.arguments.contains("--selftest") {
    Brightness.selfTest()
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
