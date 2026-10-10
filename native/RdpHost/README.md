# RdpHost — desktop-clone RDP host helper

A small .NET WinForms helper that hosts the Windows RDP client ActiveX control
(`MsRdpClient10`, CLSID `A0C63C30-F08D-4AB4-907C-34905D770C7D`) connected to the
local **child session**. It exists because Qt6/PySide6 has no ActiveX container, so
the ActiveX control cannot be embedded directly in ok-ww's Qt UI.

Port of BetterGenshinImpact's `View/Controls/ChildSession/RdpActiveXHost.cs`.

ok-ww (Python) launches this helper, then reparents the helper's top-level window
into a Qt container (`QWindow.fromWinId` + `createWindowContainer`). Everything else
(child-session enabling, launching a second ok-ww into the session, IPC, raw-mouse
forwarding) lives in `src/child_session/` on the Python side.

## Build

Requires the .NET 8 SDK on Windows (or `dotnet` elsewhere for a compile check only —
the ActiveX control is Windows-only at runtime).

```powershell
cd native/RdpHost
dotnet publish -c Release
# output: bin/Release/net8.0-windows/win-x64/publish/RdpHost.exe
```

Then copy the published output into the app so `get_path_relative_to_exe` finds it:

```powershell
Copy-Item bin/Release/net8.0-windows/win-x64/publish/* ../..//assets/native/RdpHost/ -Recurse
```

`assets/native/RdpHost/` is committed so end users do not need the .NET SDK. The
build intermediate folders (`bin/`, `obj/`) are gitignored.

## Command line

```
RdpHost.exe --pipe <name> [--host-width N] [--host-height N]
```

The RDP port is read from the registry
(`SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp:PortNumber`),
default 3389 — it is not passed on the command line.

## Wire protocol

Named pipe (helper = server, ok-ww = client). Frame layout (same as the ok-ww IPC):

```
uint32 payloadLength (little-endian, max 1 MiB)
byte   payloadType   (1 = UTF-8 JSON, 2 = relative-mouse batch)
byte[] payload
```

JSON request:  `{"v":1,"id":"<guid>","op":"<name>","args":{...}}`
JSON response: `{"v":1,"id":"<same>","op":"response","ok":bool,"error":null|str,"data":{}}`
JSON event:    `{"v":1,"op":"event","event":"<name>","data":{...}}`

### Operations

| op | args | data |
|----|------|------|
| `hello` | – | `{helperPid, sessionId, hostHwnd, rdpPort, rdpWrapper, version}` |
| `connect` | `{width,height,smartSizing,audioMuted,sendSystemShortcuts}` | `{}` |
| `disconnect` | – | `{}` |
| `reconnect` / `resize` | `{width,height}` | `{}` |
| `set_smart_sizing` | `{enabled}` | `{}` |
| `set_audio_muted` | `{muted}` | `{}` (applied on reconnect) |
| `set_send_system_shortcuts` | `{enabled}` | `{}` (applied on reconnect) |
| `send_shortcut` | `{name:"win_d"\|"win_tab"}` | `{}` |
| `send_keys` | `{keys:[{scan,up,extended}]}` | `{}` |
| `focus_input` | – | `{focused}` |
| `query_state` | – | `{connected, sessionId, inputFocused}` |
| `shutdown` | `{logoff}` | `{}` (then the helper exits) |

### Events

`login_complete`, `connection_failed {message,errorCode,extendedErrorCode}`,
`remote_desktop_size_change {width,height}`, `input_window_focus {focused}`.

`input_window_focus` is polled every 200 ms on the helper's UI thread by diffing
`GetFocus()` against the RDP "Input Capture Window" — it is the gate for raw-mouse
forwarding (raw input is only forwarded while the child view has keyboard focus).

## Phase 0 verification (the hard gate)

Everything under `src/child_session/` and this helper depends on four Windows
primitives that **cannot be exercised on Linux/WSL**. Validate them on the target
Windows machine before building any UI on top. Do not proceed to later phases
until step 4 passes.

### Preconditions

- Windows 10/11 **Pro, Enterprise, or Education** (Home lacks child sessions).
- Run every step **as administrator**.
- Python env for ok-ww is installed (`pip install -r requirements.txt`), i.e.
  `PySide6`, `pywin32` are present.

Confirm the edition and admin state first:

```powershell
# Edition must not say "Home"
(Get-ComputerInfo).WindowsProductName
# Admin check — must print True
([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

### Build the helper

```powershell
cd native/RdpHost
dotnet publish -c Release
Copy-Item bin/Release/net8.0-windows/win-x64/publish/* ../../assets/native/RdpHost/ -Recurse
```

`assets/native/RdpHost/RdpHost.exe` must exist afterwards — `rdp_host.py`
locates it relative to the app via `get_path_relative_to_exe`.

### Run the spike

From the repo root, as administrator:

```powershell
python -m src.child_session.spike                 # enable + embed + auto-connect
python -m src.child_session.spike --launch notepad.exe
python -m src.child_session.spike --launch-okww    # also start a 2nd ok-ww
```

### The four gates

| # | Action | Pass condition |
|---|--------|----------------|
| 1 | Click **启用 Child Session** | status shows a child-session id and an RDP port (default 3389) |
| 2 | Click **启动测试程序** | `notepad.exe` appears **inside the RDP view only** — never on the main desktop |
| 3 | Confirm the embedded view | the helper's hwnd is reparented into the Qt container and *shows the child desktop* (not a black/blank pane) |
| 4 | Click **运行桌面 / Win+D** | the child desktop's windows minimize inside the RDP view; **Win+Tab** likewise toggles task view |

Cross-check gate 1/2 from a separate shell:

```powershell
qwinsta                 # a 2nd session (console + child) should be listed
```

### If a gate fails

- **Gate 1** — edition is Home, or not running elevated. `wts_api` guards the
  undocumented WTS entry points with `getattr`; an absent API reads as
  "unsupported".
- **Gate 2** — Task Scheduler COM path (`session_launcher.py`) needs the
  current user's `NameSamCompatible` and interactive-token logon; check the
  raised `ChildSessionError` message.
- **Gate 3 (highest risk, R3/R4)** — the ActiveX control failed to instantiate,
  or reparenting broke the "Input Capture Window" focus. Try the standalone
  fallback: run `RdpHost.exe --pipe test` in a console and confirm a window
  appears; if it does but embedding is blank, fall back to `SetParent` +
  manual `MoveWindow` instead of `createWindowContainer`.
- **Gate 4** — keyboard focus never reaches the RDP input window; verify
  `input_window_focus` events toggle when clicking inside the embedded view.
