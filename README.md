# DSH Dock for Visual Studio

**DeepSeek Harness's conversation in a Visual Studio tool window.**

DSH Dock puts DSH's own conversation UI next to the code it is working on, served by
QuickDock and shown in a WebView2 tool window: tool cards, approvals, model and permission
controls, the same page the VS Code and JetBrains docks embed. A file path clicked in the
conversation opens in Visual Studio. It is a client of **dsh-quickdock**, the plugin that runs
inside DSH.

This first version is the dock window itself. The editor-tools half — a reverse MCP bridge
that lets the agent read and change your solution — is the next milestone.

## Features

- **The real conversation, docked.** **View → Other Windows → DSH Dock**: DSH's own UI in a
  tool window next to Solution Explorer, not a re-implementation.
- **The full QuickDock shell.** New session, the sidebar, the session title, handoff and the
  rest of the bar; the dock follows the session DSH bound to it.
- **Files open in Visual Studio.** A path in a tool card opens in the IDE, resolved against
  the open solution when the dock reported a relative path.
- **Never a blank panel.** When DSH or QuickDock is not reachable, the window says which
  address it tried and why, and offers Retry and Open in browser.
- **Finds the listener.** The port comes from `DSH_QUICKDOCK_PORT`, then QuickDock's
  `config.json`, then `47910` — the same discovery the other docks use.
- **WebView2**, which ships with Windows and Visual Studio; no extra browser runtime to
  install.

## How the docks fit together

DSH Dock for Visual Studio is one of three editor docks for the same page. **It requires DSH
with the [dsh-quickdock](https://github.com/CoplenSasbian/dsh-quickdock) plugin installed** —
QuickDock serves the conversation and the loopback listener this window reads.

| Project | Runs in | What it is |
|---|---|---|
| [dsh-quickdock](https://github.com/CoplenSasbian/dsh-quickdock) | DSH | The host half: serves the conversation as a loopback page and the MCP bridge the editor docks register with. **Required by every editor dock.** |
| [DSH Dock for VS Code](https://github.com/CoplenSasbian/dsh-vsdock) | VS Code | The same page in the secondary side bar, plus VS Code editor tools. |
| [DSH Dock for JetBrains IDEs](https://github.com/CoplenSasbian/dsh-intellijdock) | IntelliJ IDEA, CLion, PyCharm, WebStorm, GoLand, … | The same page in a tool window, plus JetBrains editor tools. |
| [DSH Dock for Visual Studio](https://github.com/CoplenSasbian/dsh-visualstudio) | Visual Studio 2026 | The same page in a tool window (the editor-tools half is next). |

The editor docks can be connected to the same folder at the same time: their tool namespaces
(`vscode_*`, `mcp__<ide>__*`) do not collide.

## Requirements

- Visual Studio 2026 (18.x) on Windows, with the WebView2 runtime (installed with Windows 11
  and with Visual Studio).
- DSH (desktop or `dsh web`) with **dsh-quickdock 0.6.0+** — it serves the `visualstudio` view.
- QuickDock's listener reachable on `http://127.0.0.1:47910` (DSH → **Settings → QuickDock →
  Connection**). A port of `0` means the listener is off.

## Install

Double-click the VSIX from [Releases](https://github.com/CoplenSasbian/dsh-visualstudio/releases)
(or run `VSIXInstaller.exe` on it), restart Visual Studio, then open **View → Other Windows →
DSH Dock**.

## License

MIT — see [LICENSE](LICENSE).
