# Port BetterGI "桌面分身" (ChildSession) into ok-ww

## Context

BetterGenshinImpact (BGI) has a "桌面分身" feature that lets one app **run and view a second game
client in a separate Windows Terminal-Services session**, embedded as an RDP view inside the main
window, with raw-mouse forwarding so the second client is playable, plus a second automation
process running inside that session. We want the same capability in ok-ww (Wuthering Waves
automation; Python + PySide6 + ok-script) so a user can multi-box two accounts and automate the
second one, controlling/viewing it from a single ok-ww window.

BGI's mechanism (reference: `/home/stiffme/shengyiwu/better-genshin-impact/`):
1. Enable an undocumented Windows "child session" (`WTSEnableChildSessions` et al. in wtsapi32).
2. Launch a second app copy **into that session** via a temporary Task Scheduler task
   (`RunEx(sessionId)`), launched as `--instance childSession`.
3. Host an **RDP ActiveX control** (`MsRdpClient10`, CLSID `A0C63C30-F08D-4AB4-907C-34905D770C7D`)
   that connects to `localhost` with the ActiveX-only property `ConnectToChildSession=true`, showing
   the child desktop; `SendKeys` (scan codes) drive system shortcuts; an "Input Capture Window"
   child hwnd provides keyboard focus for input forwarding.
4. A **named-pipe IPC** tracks the child instance and forwards **Raw Input relative-mouse batches**
   from the RDP view into the child session (RDP loses raw relative movement the game needs).

**Chosen approach (user decision):** because Qt6/PySide6 has no ActiveX container, the RDP control is
hosted by a **new standalone .NET WinForms helper exe** (near-verbatim port of BGI's C#
`RdpActiveXHost.cs`). ok-ww launches it and embeds its top-level window into Qt via
`QWindow.fromWinId` + `createWindowContainer`. Everything else is ported to Python.

> **Environments:** the target is Windows-only (child sessions + RDP + Task Scheduler). Dev here is
> WSL/Linux, so **none of the win32 paths can be run/tested locally** — verification happens on the
> user's Windows machine. Requires **admin** and a Windows edition that supports child sessions
> (10/11 Pro/Enterprise/Education; Home does not).

---

## Repo layout to add

```
native/RdpHost/                     # C# source, committed for build (not shipped from here)
  RdpHost.csproj                    # net8.0-windows, UseWindowsForms, self-contained x64
  Program.cs                        # CLI + WinForms msg loop + named-pipe server bootstrap
  RdpActiveXHost.cs                 # port of BGI View/Controls/ChildSession/RdpActiveXHost.cs
  ChildSessionNativeMethods.cs      # port: wtsapi32 + EnumChildWindows "Input Capture Window"
  RawInputMonitor.cs                # port of BGI Core/Monitor/RawInputMonitor.cs
  Protocol.cs / PipeServer.cs       # JSON envelope dispatch + BGI frame codec
  README.md                         # build + publish instructions
assets/native/RdpHost/RdpHost.exe   # PUBLISHED output, committed (dev is on Linux; user builds once)
src/child_session/                  # Python package (new)
  wts_api.py  session_launcher.py  pipe.py  rdp_host.py
  relative_mouse.py  ipc.py  service.py  window.py  tab.py  child_instance.py
```
Add `native/**/bin/`, `native/**/obj/` to `.gitignore`; commit only the published exe.
Locate the helper with the existing `get_path_relative_to_exe('assets','native','RdpHost','RdpHost.exe')`
(`ok/util/file.py:38`, used the same way in `src/globals.py:20`).

---

## Helper ↔ ok-ww protocol

**Transport:** a named pipe per host process (`--pipe okww.rdphost.<pid>.<guid>`; helper = server).
A `SUBSYSTEM:WINDOWS` app has no console, and a duplex pipe supports reconnects + binary frames,
reusing BGI's exact codec. Frame: `uint32 payloadLength (LE, ≤1 MiB)` + `byte payloadType`
(1 = UTF-8 JSON, 2 = relative-mouse batch) + payload.

**Control ops (request `{v,id,op,args}` / response `{v,id,op:"response",ok,error,data}`):**
`hello`, `connect`, `disconnect`, `resize`, `set_smart_sizing`, `set_audio_muted`,
`set_send_system_shortcuts`, `send_shortcut {win_d|win_tab}`, `send_keys`, `focus_input`,
`query_state`, `raw_input {enabled}`, `shutdown {logoff}`.

**Events (unsolicited):** `connecting`, `connected`, `login_complete`, `disconnected{reason}`,
`fatal_error{code,desc}`, `logon_error{code,desc}`, `remote_desktop_size_change{w,h}`,
`input_window_focus{focused}`, `raw_mouse_result{handled}`. (`input_window_focus` comes from a
helper UI-thread timer diffing `GetFocus()` against the located Input Capture Window.)

**Embedding:** helper is a borderless, per-monitor-V2-DPI WinForms `Form` with the AxHost docked.
Python hosts `QWindow.fromWinId(hostHwnd)` via `QWidget.createWindowContainer` (Qt then manages
geometry/z-order/show-hide). On resize compute physical px = logical × `og.dpi_scaling`
(`ok/__init__.py:1239`). Keep-aspect and topmost are applied on the **Qt container** (topmost via
`WindowStaysOnTopHint`; WS_EX_TOPMOST is meaningless once reparented).

---

## Python modules (`src/child_session/`)

- **`wts_api.py`** — ctypes ports of `WTSEnableChildSessions` / `WTSIsChildSessionsEnabled` /
  `WTSGetChildSessionId` / `WTSLogoffSession` (guard with `getattr(windll.wtsapi32, name, None)`);
  `get_rdp_port()` reads registry `...\WinStations\RDP-Tcp:PortNumber` (default 3389) via `winreg`;
  `is_rdp_wrapper_enabled()`.
- **`session_launcher.py`** — port of `ChildSessionProcessLauncher.cs` with pywin32
  (`win32com.client.Dispatch("Schedule.Service")`): `NewTask(0)`, Principal UserId = current user,
  LogonType=3 (InteractiveToken), RunLevel=1 (Highest), `RegisterTaskDefinition(...,2,...)`,
  `RunEx(None, 0x4, sessionId, None)`, delete in `finally`. `launch_okww_in_session()` builds
  `sys.executable main.py --instance childSession` (or frozen `sys.executable --instance childSession`).
- **`pipe.py`** — frame codec + blocking `PipeClient`/`PipeServer` (thread per connection);
  `get_client_pid()` (kernel32 `GetNamedPipeClientProcessId`) + `get_client_session()`
  (`WTSQuerySessionInformation`) so the client's real session/PID can't be spoofed. Pipe ACL =
  current-user-only, deny `Network`.
- **`rdp_host.py`** — `RdpHostProcess`: spawn/stop the helper, req/resp correlation with timeouts,
  a `RdpHostSignals(QObject)` bridging helper events → Qt signals. Convenience wrappers for each op.
- **`ipc.py`** — root `InstanceIpcServer` (pipe `okww.v1.user-<SID>.root`) + child `InstanceIpcClient`
  (auto-reconnect every 1 s). Ops: `ping`, `connection.open{role}`, `input.relativeMouse.subscribe/
  unsubscribe`, `child.runTask`. Port of `InstanceIpcProtocol.cs` minus WebView.
- **`relative_mouse.py`** — root forwarder (gated on game-mouse-mode + connected + input focused,
  writes payloadType 2 to the IPC client) and child injector (`SendInput` with `MOUSEEVENTF_MOVE`).
- **`service.py`** — `ChildSessionService`, port of `ChildSessionService.cs` (connect/reconnect/retry,
  60 s timeout, show/hide, smart-sizing/audio reconnect, logoff-on-dispose, status text). Stored as
  `og.child_session` (add attr in `OkGlobals.__init__`, `ok/__init__.py:~1183`).
- **`window.py`** — `ChildSessionWindow` (qfluentwidgets `BaseWindow`): embedded helper + toolbar
  (Connect/Disconnect/Launch Child/Win+D/Win+Tab/Audio/Smart-sizing/Logoff/status). Uses `self.tr`.
- **`tab.py`** — `ChildSessionTab(CustomTab)` nav entry (enable toggle, Show, status, config link).
- **`child_instance.py`** — child-mode glue: connect to root IPC, register role `childSession`,
  `run_task_from_root(name)` → existing module-level `run_task` (`ok/__init__.py:~1144`).

## Wiring / edits to existing files

- **`config.py`** — add `ChildSession` `ConfigOption` (auto-launch, topmost, smart sizing, keep aspect,
  send system shortcuts, game mouse mode, audio muted, child task, window positions) with
  `show_at_tab=True`; append to `global_configs` (config.py:169) and add
  `["src.child_session.tab","ChildSessionTab"]` to `custom_tabs` (config.py:170). When
  `--instance childSession`, switch `config_folder` (e.g. `configs_child`) so the two processes don't
  clobber each other's `configs/devices.json`.
- **`ok/util/process.py`** — add `--instance` / `--instance-pipe` to `parse_arguments_to_map`
  (additive; it already uses `parse_known_args`).
- **`ok/__init__.py`** — read `--instance`; when `childSession`, force `config['check_mutex']=False`
  before the mutex check (`ok/__init__.py:682`) and branch `start()` to the child loop (no GUI,
  no required task arg). Add `og.child_session`.

---

## Phased milestones

**Phase 0 — feasibility spike (Windows, throwaway).** Prove the four risky primitives: (1) enable
child sessions & read id/port; (2) launch `notepad.exe` into the session and confirm it's invisible
on the main desktop; (3) build `RdpHost.exe` and connect it standalone to show the child desktop;
(4) embed its hwnd in a Qt test window and send Win+D. **Hard gate: do not proceed until (4) works.**

**Phase 1 — helper hardening + protocol.** Full CLI, req/resp + events, focus events, reconnect,
clean shutdown/logoff; publish to `assets/native/RdpHost/`. Verify 5 connect/disconnect cycles and
that `input_window_focus` tracks clicks.

**Phase 2 — Qt window + config.** `window.py`/`tab.py`/`service.py` + config wiring. Verify
drag-resize scaling, smart-sizing/keep-aspect toggles, topmost, logoff-on-close.

**Phase 3 — launch child ok-ww.** `child_instance.py`, `ipc.py`, arg parsing, instance mode, config
folder switch. Verify child connects to root pipe and a root-dispatched task runs in the child.

**Phase 4 — relative mouse.** Helper RawInputMonitor + `relative_mouse.py` + IPC subscribe/inject.
Verify 1:1 relative movement into the child game, gating on focus and game-mouse-mode.

**Phase 5 — polish/packaging.** Audio mute, retry diagnostics surfaced in the tab, i18n entries,
self-contained signed helper (AV), document the admin requirement.

---

## Risks (most likely to break first)

- **R3 MsRdpClient10 in a .NET helper** — AxHost/COM interop; verify the OCX instantiates before
  building anything else (Phase 0.3, highest risk).
- **R4/W5 embedding + focus** — reparenting a foreign top-level AxHost into Qt may mean the RDP
  "Input Capture Window" never receives keyboard focus, killing shortcuts + raw-mouse. Test focus
  early; fall back to `SetParent` + manual `MoveWindow`.
- **R1 undocumented WTS APIs** — may be absent/no-op on some builds; `getattr` guard + feature-gate
  the whole tab; requires admin + Pro/Enterprise.
- **R2 Task Scheduler COM from Python** — verify; fall back to `comtypes.client.CreateObject`.
- **R6 DPI / coordinate spaces** (logical vs physical) on resize.

---

## Verification

Local (WSL): only import-level/lint checks — `python -c "import src.child_session.<mod>"` may fail
on pure-win32 deps; full runs must be on Windows. On Windows (user):
1. Phase 0 gate on the user's exact Windows build (edition + admin).
2. `qwinsta` shows the child session; game visible via RDP view but not on main desktop.
3. End-to-end: enable child session → Connect → embedded view shows child desktop → Launch Child
   ok-ww → root dispatches a task → child automates its game; relative mouse drives the child game;
   Close → session logged off.

## Critical files
- `config.py` (global_configs:169, custom_tabs:170, config_folder)
- `ok/__init__.py` (start:764, run_task:1144, check_mutex gate:682, OkGlobals:~1174, dpi:1239)
- `ok/util/process.py` (parse_arguments_to_map:571, check_mutex:235, is_admin:33, execute:390, kill_exe:423)
- `ok/ui/qt/MainWindow.py` (custom_tabs:125-136), `ok/ui/qt/widget/CustomTab.py`
- Reference to port: `better-genshin-impact/BetterGenshinImpact/{View/Controls/ChildSession/RdpActiveXHost.cs, Service/ChildSession/*.cs, Service/Instance/InstanceIpcProtocol.cs, Core/Monitor/RawInputMonitor.cs}`
