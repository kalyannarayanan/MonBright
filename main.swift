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

// MARK: - Monitor model (one per connected external monitor)

final class Monitor: ObservableObject, Identifiable {
    let id: String
    let name: String
    @Published var brightness: Double
    @Published var available: Bool = true

    private let ddcQueue: DispatchQueue
    private var pendingWrite: DispatchWorkItem?
    private var cancellables = Set<AnyCancellable>()

    init(id: String, name: String) {
        self.id = id
        self.name = name
        self.ddcQueue = DispatchQueue(label: "monbright.ddc.\(id)")
        let key = "brightness.\(id)"
        self.brightness = UserDefaults.standard.object(forKey: key) as? Double ?? 50

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

    private func scheduleWrite(value: Double) {
        pendingWrite?.cancel()
        let id = self.id
        let item = DispatchWorkItem { [weak self] in
            let ok = DDC.setBrightness(monitorID: id, value: Int(value.rounded()))
            UserDefaults.standard.set(value, forKey: "brightness.\(id)")
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
}

// MARK: - Entry point

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
