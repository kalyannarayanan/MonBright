# MonBright

A brightness slider for your external monitors, living in the menu bar (Mac) or system tray (Windows). One slider per connected display. No admin rights, no permissions, no drivers — it talks to the monitor over the same DDC/CI channel its own buttons use.

| Platform | Folder | Requirements |
|---|---|---|
| **macOS** | [`macos/`](macos/) | Apple Silicon, macOS 13+ |
| **Windows** | [`windows/`](windows/) | Windows 10/11, no install step |

Each folder has its own README with install steps, usage, and the "is this safe?" walkthrough.

Both builds also dim **below** a monitor's hardware minimum — useful at night when the panel's own "0" is still bright. The bottom third of the slider pins the hardware at its floor and dims the picture itself, with the same curve on both platforms. Details: [macOS](macos/README.md#darker-than-your-monitors-minimum) · [Windows](windows/README.md#darker-than-your-monitors-minimum).

## License

[MIT](LICENSE) — same license for both platforms.
