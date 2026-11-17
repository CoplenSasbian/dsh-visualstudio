# DSH Dock for Visual Studio

**DeepSeek Harness's conversation in a Visual Studio tool window.**

DSH Dock puts DSH's own conversation UI next to the code it is working on, served by
QuickDock and shown in a WebView2 tool window: tool cards, approvals, model and permission
controls, the same page the VS Code and JetBrains docks embed. A file path clicked in the
conversation opens in Visual Studio. It is a client of **dsh-quickdock**, the plugin that runs
inside DSH.

This first version is the dock window with a read-only editor-tools half: the agent can see the
solution, read files, search, read diagnostics and symbols, and open a file at a line. The write
tools and review diffs are the next milestone.

## Features

- **The real conversation, docked.** **View → Other Windows → DSH Dock**: DSH's own UI in a
  tool window next to Solution Explorer, not a re-implementation.
- **The full QuickDock shell.** New session, the sidebar, the session title, handoff and the
  rest of the bar; the dock follows the session DSH bound to it.
- **Files open in Visual Studio.** A path in a tool card opens in the IDE, resolved against
  the open solution when the dock reported a relative path.
- **Editor tools for the agent.** Turn on the plug in the dock's title bar: the extension starts
  a loopback MCP server and QuickDock mounts it for the solution folder, so the agent gets
  `mcp__visualstudio__*` tools — workspace folders, the active selection, file reads, file
  listing, text search, the Error List, a file's symbols, and opening a file at a line.
- **Scoped to your solution.** Tools are mounted only for sessions working inside the solution
  directory; when the session on screen works elsewhere, the title bar shows an amber warning
  triangle, and the list button shows what was published and what is mounted.
- **Reload without restarting Visual Studio.** The tool window's own toolbar has a Reload
  button: it re-resolves QuickDock's port and reloads the shell page, so a DSH restart does not
  need an IDE restart.
- **Never a blank panel.** When DSH or QuickDock is not reachable, the card names the address
  and where the port came from, and offers Retry, Start DSH, Open in browser, Connection
  settings, the QuickDock log, and adopting QuickDock's `config.json` port.
- **Settings.** **Tools → Options → DSH Dock**: the QuickDock port (0 follows the config),
  whether to follow `config.json`, whether editor tools start with the dock, and the
  editor-tools port.
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
| [DSH Dock for Visual Studio](https://github.com/CoplenSasbian/dsh-visualstudio) | Visual Studio 2026 | The same page in a tool window, plus Visual Studio editor tools (read-only for now). |

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
