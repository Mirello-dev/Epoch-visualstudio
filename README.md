<p align="center">
  <img src="images/epoch-logo.png" alt="epoch logo" width="120" />
</p>

<h1 align="center">epoch</h1>

<p align="center">
  <strong>Automatic coding time tracking for Visual Studio</strong><br/>
  Seamlessly records your focus time and syncs it to your epoch instance.
</p>

<p align="center">
  <a href="https://github.com/mirello-dev/epoch-visualstudio/blob/main/LICENSE">
    <img src="https://img.shields.io/badge/license-GPL--3.0-green" alt="License" />
  </a>
</p>

---

## What it does

epoch runs silently in the background and tracks how much time you actively spend coding. It detects your current project via git, sends periodic heartbeats to your epoch instance, and shows a live timer in the status bar. Everything works offline — heartbeats are queued locally and synced automatically when connectivity is restored.

This is the **Visual Studio** edition. It is a feature-for-feature port of the [VS Code extension](https://github.com/mirello-dev/epoch-vscode) and shares the same on-disk configuration, so if you run both editors you only configure your API key once.

## Features

- **Live status bar** — shows today's total coding time at a glance (`epoch: 1 hrs 23 mins`)
- **Automatic project detection** — resolves the project name from your git `origin` remote
- **Offline queue** — heartbeats are stored locally and flushed when back online
- **Zero configuration needed** — connects to `https://epoch.mirello.cloud` by default
- **Self-hosted support** — point it at any epoch instance with a custom URL
- **Shared config with VS Code** — uses the same `~/.config/epoch/config.json`

## Requirements

- Visual Studio 2022 (17.0+), Community / Professional / Enterprise

## Getting started

1. Install the `.vsix` (double-click it, or via **Extensions → Manage Extensions**)
2. Open **Tools** and run **`epoch: Set API Key`**
3. Optionally run **`epoch: Set Instance`** if you're using a self-hosted server
4. Start coding — the timer appears in the bottom-left status bar

## Commands

All commands live under the **Tools** menu.

| Command | Description |
|---|---|
| `epoch: Set API Key` | Store your epoch API key |
| `epoch: Set Instance` | Set the URL of your epoch instance |
| `epoch: Validate API Key` | Check that your key authenticates successfully |
| `epoch: Open Dashboard` | Open your epoch dashboard in the browser |
| `epoch: Show Output` | Open the epoch output pane for diagnostics |

## Configuration

Configuration is stored at `~/.config/epoch/config.json` (respects `$XDG_CONFIG_HOME`) — the same file the VS Code extension uses.

| Key | Default | Description |
|---|---|---|
| `apiKey` | — | API key for authentication |
| `baseUrl` | `https://epoch.mirello.cloud` | URL of your epoch instance |

## Status bar indicators

| Display | Meaning |
|---|---|
| `epoch: 2 hrs 14 mins` | Tracking — connected and syncing |
| `epoch: 1 hrs 05 mins (offline)` | Offline — data queued for sync |
| `epoch: ✕ Unconfigured (set API key)` | API key missing or invalid |

## Building from source

Open `EpochVisualStudio.sln` in Visual Studio 2022 with the **Visual Studio extension development** workload installed, then build. The `.vsix` is produced under `bin\Debug` / `bin\Release`. Pressing **F5** launches an experimental VS instance with the extension loaded.

## How it maps to the VS Code extension

| VS Code concept | Visual Studio equivalent |
|---|---|
| `onDidChangeActiveTextEditor` / `onDidChangeTextDocument` / `onDidSaveTextDocument` | `WindowEvents.WindowActivated` / `TextEditorEvents.LineChanged` / `DocumentEvents.DocumentSaved` |
| `window.state.focused` | Win32 foreground-window check against the IDE main window |
| Status bar item | `IVsStatusbar` |
| `vscode.commands.registerCommand` | `.vsct` command table + `OleMenuCommandService` |
| `vscode.git` extension API | parsing `.git/config` for the `origin` remote |
| `https`/`http` | `HttpClient` |
| output channel | `IVsOutputWindowPane` |

## License

GPL-3.0 — see [LICENSE](LICENSE) for details.

---

<p align="center">
  Built by <a href="https://mirello.ch">Mirello</a>
</p>
