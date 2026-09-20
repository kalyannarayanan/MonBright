# MonBright

A brightness slider for your external monitors, living in the menu bar (Mac) or system tray (Windows). One slider per connected display. No admin rights, no permissions, no drivers — it talks to the monitor over the same DDC/CI channel its own buttons use.

| Platform | Folder | Requirements |
|---|---|---|
| **macOS** | [`macos/`](macos/) | Apple Silicon, macOS 13+ |
| **Windows** | [`windows/`](windows/) | Windows 10/11, no install step |

Each folder has its own README with install steps, usage, and the "is this safe?" walkthrough.

The macOS build also dims **below** a monitor's hardware minimum by scaling the display's gamma ramp — useful at night when the panel's own "0" is still bright. Details in the [macOS README](macos/README.md#darker-than-your-monitors-minimum). The Windows build doesn't do this yet.

## License

[MIT](LICENSE) — same license for both platforms.
