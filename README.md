# Mouse Swipe Visualizer

Shows your **physical mouse movement** as short, fading swipes, together with a keyboard block that lights up
the keys you hold, and delivers the picture as a **real Windows camera**: **"Mouse Swipe Visualizer Camera"**.
Medal (or any other camera app) can select it directly; no OBS needed.

**Download:** see [Releases](https://github.com/Flurry2005/MouseAndKeyBoardVisualizer/releases). There is an
MSI installer, which registers the camera, and a portable zip.

```
Windows Raw Input (WM_INPUT, RIDEV_INPUTSINK, also while the game has focus)
        ↓
RawMouseInput ─→ MouseDeltaBuffer                (+ KeyboardState for the keyboard panel)
        ↓
SwipeEngine (own thread, own timing)
   SwipeTracker → SwipeModelBuilder → SwipeRenderModel → SoftwareRasterizer (BGRA, off-screen)
        ↓                                                   ↘
CameraFrameLink (shared memory Global\…, seqlock)            PreviewFrameStore → optional preview window
        ↓
MouseSwipeVisualizer.VirtualCamera.dll  (C++ Media Foundation media source, runs in the Windows Camera Frame Server)
   IMFMediaSourceEx / IMFMediaStream2 → BGRA→NV12/RGB32 → IMFSample
        ↓
"Mouse Swipe Visualizer Camera" (MFCreateVirtualCamera, SoftwareCameraSource)
        ↓
Medal → Settings → Video Overlays → Virtual Camera Capture
```

**The video does not depend on any window.** Rendering happens on its own thread into a memory buffer: no WPF
window, no screenshot, no `PrintWindow`. The camera works just the same when Settings or the preview is covered,
minimized, hidden or closed, or when the app runs in the tray only (`--headless`).

**Keyboard and frames.** By default the picture is split 40/60. On the left is a keyboard block (Esc–5, Tab–T,
Caps–G, Shift–B, Ctrl/Alt/Space) where held keys light up. On the right is the swipe in its own box (the mouse
area). Everything sits on a styled panel (the "frame") with a background, border colour, border width and rounded
corners. A background picture, or the cover art of what's playing on Spotify, can fill the frame, with an
optional frosted-glass look. Everything is configurable
(see [Settings](#settings)). The keyboard and frames can be turned off for a plain full-picture swipe.

The program only **observes** input. It does not inject, change or block anything and never touches the game
process. The camera is a **user-mode** Media Foundation media source (no kernel driver).

---

## Requirements

| | |
|---|---|
| OS | **Windows 11 build 22000 or later** for the virtual camera (developed and tested on build 26200). The app also starts on Windows 10, but without the camera. Settings then shows *"Native Windows Virtual Camera is not supported on this Windows version. Requires Windows 11 build 22000 or later."* |
| Running (MSI) | Nothing extra: self-contained .NET and a statically linked C++ runtime (no VC++ redistributable needed). |
| Building | .NET SDK 9.0 + **Visual Studio 2022** (Community is fine) with the workloads **.NET desktop development** and **Desktop development with C++** (toolset v143) + **Windows 11 SDK** (≥ 10.0.22000; the project uses the installed **10.0.26100.0**). |
| Installer | WiX v5 is fetched automatically through NuGet (`WixToolset.Sdk`). |

## Build

```bash
dotnet restore
```

```bash
dotnet build -c Release
```

`dotnet build` also builds the C++ project: `MouseSwipeVisualizer.csproj` finds Visual Studio's MSBuild through
`vswhere` and builds `MouseSwipeVisualizer.VirtualCamera.vcxproj`, and the DLL is copied next to the exe.
Without the C++ tools, use `dotnet build -c Release -p:SkipNativeBuild=true`; you then get no camera.

MSI (publish + WiX):

```bash
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

→ `installer/bin/x64/Release/MouseSwipeVisualizer-3.0.12.0-x64.msi`

Release binaries are deterministic and contain no local build paths.

**VS Code:** open the folder and install the recommended extensions (C# Dev Kit). F5 runs the app. *Run Task*
offers `build`, `build installer (MSI)`, `camera: status`, `camera: install / repair (UAC)`, `camera: test` and
`self-test`. The *headless / tray only* launch configuration runs without windows.

---

## Installing and registering the camera

### Recommended: MSI

Double-click the MSI (UAC prompt). It:

1. installs `MouseSwipeVisualizer.exe` + `MouseSwipeVisualizer.VirtualCamera.dll` into `C:\Program Files\MouseSwipeVisualizer\`,
2. COM-registers the media source DLL in **HKLM** (`CLSID {0728D89A-2065-4F45-85F7-B128DA227817}`, `ThreadingModel=Both`),
3. creates the camera for the installing user (this step runs *without* admin rights after the install).

Uninstalling (Settings → Apps, or `msiexec /x`) runs an elevated action **before** the files are removed. It
deletes **all** camera devices created from our CLSID (for every user), the COM registration and any development
copies, so no orphaned cameras are left behind (verified, see *Tests*). Upgrading keeps the camera.

### Development workflow: commands

| Command | What it does | Admin? |
|---|---|---|
| `MouseSwipeVisualizer.exe --camera-status` | API support, registration, enumeration, counters | no |
| `MouseSwipeVisualizer.exe --camera-install` | copies the DLL to Program Files + HKLM COM (UAC), then creates the camera | UAC for step 1 |
| `MouseSwipeVisualizer.exe --camera-register` | creates/opens the camera for this user (idempotent) | no |
| `MouseSwipeVisualizer.exe --camera-unregister` | `IMFVirtualCamera::Remove` for this user | no |
| `MouseSwipeVisualizer.exe --camera-remove` | full removal: camera(s), device nodes, COM, files | UAC |
| `MouseSwipeVisualizer.exe --camera-diagnose` | status + all cameras + a short read test | no |
| `MouseSwipeVisualizer.exe --camera-test [--frames N] [--format 1280x720@30] [--rgb32] [--save-frame file.png]` | opens the camera as a Media Foundation consumer and reads frames | no |

The exe is a GUI app. In PowerShell, use `.\MouseSwipeVisualizer.exe --camera-status | Out-Host`.
The same functions are available as buttons in **Settings → Output**.

### Why CurrentUser + System lifetime, and why admin anyway?

Two **different** things have to be set up:

* **The media source DLL (COM)** is loaded by the *Windows Camera Frame Server* (LOCAL SERVICE) and
  *Frame Server Monitor* (LOCAL SYSTEM) services in session 0. They only see **HKLM** registrations and can only read
  files from a location such as Program Files. That needs admin **once, at install time**.
* **The camera itself** is created with `MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
  MFVirtualCameraLifetime_System, MFVirtualCameraAccess_CurrentUser, …)`:
  * **`CurrentUser`**: the camera is only visible to your Windows account and needs **no** admin (`AllUsers` would).
  * **`System` lifetime**: the camera survives app restarts and reboots, so Medal sees it even when the app isn't
    running (it then shows a plain chroma background). The app also re-creates it idempotently on every start.
    Uninstalling always removes it (`IMFVirtualCamera::Remove` + removal of the device nodes).

Windows adds *"Windows Virtual Camera"* to the name itself, in your Windows language. On English Windows the
camera is called **"Mouse Swipe Visualizer Camera (Windows Virtual Camera)"**.

---

## Running

```bash
MouseSwipeVisualizer.exe             # Settings + preview (the preview can be closed; the camera is unaffected)
MouseSwipeVisualizer.exe --headless  # tray icon only, no windows; the camera works fully
```

The Start menu has both variants. The tray icon offers *Show preview*, *Settings…*, *Clear trail* and *Exit*.

### Rendering only while someone is watching

The media source creates the shared memory when a consumer (Medal) starts the stream, and it updates a heartbeat
for every frame. The app detects this (polling at 4 Hz while there is no consumer) and **only then** renders, at
the consumer's resolution and frame rate. Without a consumer there is no 60 FPS rendering (measured **0.00 %** CPU).
An unchanged picture is not rasterized again; the camera repeats the last frame with new timestamps.

---

## Video formats

The camera exposes these formats (the first is the default):

| Resolution | FPS | Format |
|---|---|---|
| **1280 × 720** | **30** (default) | NV12, RGB32 |
| 1280 × 720 | 60 | NV12, RGB32 |
| 800 × 800 | 60 | NV12, RGB32 |
| 800 × 800 | 30 | NV12, RGB32 |
| 640 × 480 | 30 | NV12, RGB32 |

NV12 is what the camera pipeline and most apps prefer; RGB32 is there for apps that want it. The picture is
rendered directly at the requested resolution. The sensor profile *Legacy* is set to show all formats (including
60 FPS) for apps that aren't profile-aware.

---

## IPC: app → camera

* Named shared memory **`Global\MouseSwipeVisualizerCamera.Frames.v1`** (24 MiB: a 4 KiB header + 3 frame slots of 8 MiB).
  The media source (LOCAL SERVICE, session 0) creates it with a DACL that gives *interactive users* read/write
  access, because a normal user process isn't allowed to create `Global\` objects. The layout is defined in
  `src/MouseSwipeVisualizer.Shared/SharedFrameProtocol.h` (C++) and `.cs` (C#), byte for byte identical.
* **No tearing:** triple buffering plus a **seqlock** per slot. The producer only writes to an unpublished slot
  (seq odd → write → seq even → publish the slot index). The consumer copies and only accepts the copy if seq was
  even and unchanged before and after the copy. No mutex.
* **Untrusted data:** the media source computes offsets and sizes itself and requires an exact match
  (`width/height` = requested, `stride = width*4`, `offset = header + slot*capacity`, `size ≤ capacity`), so corrupt
  metadata can never cause a read outside the mapping. The app validates what the camera writes in the same way.
* **App not running or crashed:** if the producer heartbeat is older than 1 s, or the app has exited cleanly, the
  camera delivers a solid background frame (chroma green) at the right rate. The app reconnects automatically
  when it restarts.
* **Consumer stops:** if the consumer heartbeat stands still for more than 1 s, the app stops producing.

---

## Medal

### Test plan (manual, requires Medal)

1. Install the MSI (or run `--camera-install`) and start MouseSwipeVisualizer.
2. Settings → **Output** should show *"Virtual camera: Ready – …"*; `--camera-status` should show
   `Virtual Camera: Registered   Status: Ready`.
3. Start Medal.
4. Open **Settings → Video Overlays**.
5. Turn on **Video Overlay**.
6. Turn on **Virtual Camera Capture**.
7. Look for **"Mouse Swipe Visualizer Camera (Windows Virtual Camera)"** (the suffix follows your Windows language).
8. Select the camera.
9. Move the mouse.
10. Check Medal's preview; the swipe should be visible. Settings → Diagnostics shows *Camera active consumer: YES*,
    the requested format and frame counters.
11. Start a game.
12. Create a Medal clip.
13. Check that the swipe is in the finished clip.

**Status:** the Windows side is verified (enumeration, opening, frame flow through the Frame Server; see *Tests*).
**Medal itself is not verified.** That requires going through the steps above.

### Important limitation: transparency

A camera picture has no usable alpha channel. The camera therefore delivers the overlay on a **chroma-green
background** (`#00FF00`) with *chroma-safe edges*: no half-transparent green edge pixels, even after NV12's 2×2
chroma subsampling. Medal's documentation describes **no chroma key** for Video Overlay, so assume the overlay shows
up in Medal as an **opaque box** with a green background. That is a limitation of Medal, not something the camera
can solve. Alternatives:

* Turn off *Chroma-key background* in Settings. The background becomes **black**, a more discreet box.
* Use a background picture in the frame (see Settings), so the box looks intentional.
* If you need a see-through swipe over the game, use **OBS mode** (Output mode = *OBS Capture Window*) with a
  Chroma Key or the *Screen* blending mode in OBS; see below.

---

## Settings

Stored in `%LocalAppData%\MouseSwipeVisualizer\settings.json` (schema 4; older files are migrated).

| Setting | Default | Description |
|---|---|---|
| `OutputMode` | `NativeVirtualCamera` | `NativeVirtualCamera` or `ObsCaptureWindow` (fallback). |
| `ShowPreview` | true | Show the preview window at start. The camera works without it. |
| `CaptureWidth`/`CaptureHeight` | 800 × 800 | Size of the preview / OBS window. The camera uses the resolution the consumer requests. |
| `CaptureBackgroundEnabled` | true | Chroma background (`ChromaKeyColor`), otherwise black. Applies to camera and preview. |
| `ChromaKeyColor` | `#00FF00` | Background colour (also the camera's fallback picture). |
| `ChromaSafeEdges` | true | Opaque colour-ramp fade + hard, 2×2-aligned edges wherever something touches the key colour, so there is no green halo after keying, even in NV12. |
| `SensitivityScale` | 1.0 | 800 counts = centre → edge at 1.0. |
| `SwipeBreakMs` | 120 | Pause that ends a swipe (the next one starts in the centre). |
| `LiftDetectionEnabled` / `LiftGapMs` | true / 40 | Lift-and-re-centre detection. |
| `SmoothingStrength` | 0.35 | Visual smoothing. |
| `TrailLifetimeMs` | 500 | How long the trail stays visible. |
| `TrailThickness` | 4 | Line width in picture pixels. |
| `TrailColor` | `#FFFFFF` | Line colour. |
| `OutlineEnabled` / `OutlineColor` | true / `#141418` | Outline around the line, arrow and dot. |
| `HeadStyle` | `Arrow` | Head of the swipe: `Arrow`, `Dot` or `None`. |
| `DotColor` / `DotSize` | `#FFFFFF` / 12 | Dot colour and diameter (px, 2–48) when `HeadStyle` = `Dot`. |
| `KeyboardEnabled` | true | Show the keyboard block. |
| `KeyboardPosition` | `Left` | `Left`, `Right`, `Top` or `Bottom` of the swipe. |
| `KeyboardSplitPercent` | 40 | The keyboard's share of the area (20–70); the swipe gets the rest (40/60). |
| `KeyFillColor` / `KeyBorderColor` / `KeyLabelColor` | `#000000` / `#E6E6E6` / `#FFFFFF` | Key at rest. |
| `KeyPressedFillColor` / `KeyPressedLabelColor` | `#FFFFFF` / `#000000` | Pressed key. |
| `SwipeBoxEnabled` | true | Frame around the mouse area (the swipe), styled like the keys. |
| `SwipeBoxFillColor` / `SwipeBoxBorderColor` | `#000000` / `#E6E6E6` | Mouse area background and border. |
| `SwipeBoxBorderWidth` / `SwipeBoxCornerRadius` / `SwipeBoxPadding` | 2 / 12 / 10 | Border width, corner radius and space between border and swipe (px). |
| `SwipeBoxWidthPercent` / `SwipeBoxHeightPercent` | 100 / 100 | Mouse area frame width/height in % of its space (20–100, centred). The swipe keeps its size and only shrinks once the frame edge reaches it. |
| `BackgroundImagePath` | empty | Picture inside the frame. It replaces the frame's and the mouse area's background colours and is scaled to cover the frame; outside the frame it's still the chroma colour. Without a frame it covers the whole picture. |
| `BackgroundImageBlur` / `BackgroundImageDim` | 24 / 20 | Blur (px) and darkening (%) of the background picture. |
| `GlassEnabled` | false | Glass look: the frame, mouse area and keys become frosted, see-through glass over an extra-blurred copy of the background picture. Pressed keys stay solid. |
| `GlassTintColor` / `GlassOpacity` / `GlassBlur` | `#FFFFFF` / 12 / 16 | Glass tint, tint strength (%) and extra frost blur (px). |
| `SpotifyCoverEnabled` | false | Use the cover of what's playing on Spotify as the frame picture (see [Spotify cover art](#spotify-cover-art)). |
| `SpotifyClientId` | empty | Client ID of your own Spotify app. The client secret is **not** stored in settings.json. |
| `SpotifyPollSeconds` | 3 | How often to check what's playing (1–60 s); the cover changes within this time after a song change. |
| `BackgroundFadeMs` | 600 | Crossfade (ms, 0–5000) when the cover or background picture changes; 0 = switch instantly. |
| `CoverColorsEnabled` | true | Take colours from the cover / background picture. Spotify's API sends no colours, so the accent (the most prominent saturated colour) is extracted from the image. Grey covers keep the normal colours. |
| `CoverAccentTrail` / `CoverAccentKeyBorders` | true / true | Accent colour for the mouse strokes (line, arrow, dot) and the key outlines. |
| `CoverAccentPressedKeys` / `CoverAccentFrameBorders` | false / false | Accent for pressed keys (label turns black/white for contrast) and for the frame and mouse area borders. |
| `CoverAutoContrast` | true | The mouse strokes (line, arrow, dot) keep at least 4.5:1 contrast against what is under the mouse area: darkened on light covers, lightened on dark ones (hue kept), with the outline flipped when needed. Key outlines keep at least 3:1 against the picture around the keyboard, and glass key labels turn dark on light covers. |
| `SpotifyRedirectPort` | 8888 | Port of the local sign-in redirect `http://127.0.0.1:PORT/callback`. |
| `BordersEnabled` | true | Borders on the frame, mouse area and keys (off = borderless). |
| `FrameEnabled` | true | Draw the panel (the "frame") behind the keyboard and swipe. |
| `FrameBackgroundColor` / `FrameBorderColor` | `#000000` / `#E6E6E6` | Panel background and border. |
| `FrameBorderWidth` / `FrameCornerRadius` | 2 / 16 | Border width and corner radius (px). |
| `FrameWidthPercent` / `FrameHeightPercent` | 100 / 100 | Panel width/height in % of the picture (20–100, centred). The keyboard and swipe keep their size and only shrink once the panel edge reaches them. |
| `FrameMargin` / `FramePadding` | 6 / 14 | Distance picture edge → panel, and panel border → content (px). |
| `RenderFps` | 60 | Preview frame rate when no camera consumer sets the pace. |
| `IncludeDebugInCapture` | false | Debug text in the OBS window (development). |
| `ShowSettingsOnStartup` | true | Open Settings at start. |

### Spotify cover art

Shows the album (or podcast) cover of what's playing on your Spotify account as the frame background. It uses
the same blur, dim and glass settings as a background picture, and falls back to your normal background when
nothing is playing.

Setup (once):

1. Open the [Spotify developer dashboard](https://developer.spotify.com/dashboard) and click **Create app**. Any name
   and description will do. Under **Redirect URIs** add exactly `http://127.0.0.1:8888/callback` (Settings shows the
   URI; it changes if you change the callback port). Under APIs, tick **Web API**.
2. In the app's settings, copy the **Client ID** and click **View client secret** to copy the secret.
3. In Mouse Swipe Visualizer, go to **Settings → Spotify cover art**. Paste both, click **Connect Spotify…** and
   approve access in the browser that opens.
4. Tick **Use the cover of what's playing on Spotify** and choose how often to check (**Check every … s**, default 3)
   and how long the crossfade to a new cover takes (**Cover fade (ms)**, default 600; 0 = instant).
5. Optional: **Use colours from the cover** tints the mouse strokes and key outlines (and, if ticked, pressed keys
   and frame borders) with the cover's accent colour. The colours crossfade together with the cover.

Apps in Spotify's development mode only work for their owner and for users you add under *User Management* in the
dashboard.

Security and privacy:

* Access is read-only: the scopes are `user-read-currently-playing` and `user-read-playback-state`.
* The client secret and the refresh token are stored encrypted with Windows DPAPI for your Windows account, in
  `%LocalAppData%\MouseSwipeVisualizer\spotify.dat`. They are never written to settings.json, never logged and
  never shown again in the UI. **Disconnect** deletes them.
* The sign-in redirect is received by a one-shot listener bound to 127.0.0.1 only. It checks a random `state`
  value and closes after the sign-in (or after 3 minutes).
* Covers are only downloaded over HTTPS from Spotify's image servers (max 5 MB), into
  `%LocalAppData%\MouseSwipeVisualizer\spotify-covers`, which keeps only the last few covers.
* It polls at most once per second, whatever the settings, and honours Spotify's rate-limit `Retry-After`.

**Keyboard and privacy:** Raw Input for the keyboard is only registered while `KeyboardEnabled` is on. Only the
up/down state of the 27 keys in the block is kept in memory, per physical key (scan code), so the layout is the same
for every keyboard language. No other keys are stored, nothing is logged and nothing is written to disk. Just like
with the mouse, the program only observes; it doesn't inject or block anything.

**Anti-aliasing:** everything drawn on a panel, in the mouse area or on a background picture is anti-aliased
(swipe, outline, arrow, dot, keys, labels, frames). Hard, 2×2-aligned edges are only used where something directly
touches the chroma colour (the panel's outer edge, or the swipe when neither frame nor mouse area is on);
anything else would leave a green fringe after keying. The preview window scales smoothly, so the picture looks
clean even when the window is a different size from the video or Windows display scaling is above 100 %.

The frame and keyboard are drawn in a cached static layer. The background, picture and frames are rebuilt only
when the style or size changes, and the keyboard only when a key goes up or down.

**Diagnostics** (bottom of Settings) shows, among other things:

```
Virtual Camera API supported: YES
MediaSource registered: YES (C:\Program Files\MouseSwipeVisualizer\MouseSwipeVisualizer.VirtualCamera.dll)
Virtual camera registered: YES
Virtual camera enumerable: YES
Camera friendly name: Mouse Swipe Visualizer Camera (Windows Virtual Camera)
Camera active consumer: YES/NO
Requested format: 1280x720 NV12 @ 30 FPS
Frames produced / Frames consumed / Dropped/repeated frames
Camera side: conversion … ms, sample creation total … ms
Last consumer start / Last error
```

It also shows engine statistics (input events/s, dx/dy, frame times per stage, allocation per frame).

---

## OBS mode (fallback)

With Output mode = **OBS Capture Window**, the preview window *"Mouse Swipe Visualizer"* is the OBS source
(Window: [MouseSwipeVisualizer.exe]: Mouse Swipe Visualizer): *Sources → + → Window Capture*, Capture Method
*Windows 10 (1903 and up)*, *Client Area* on, *Filters → Chroma Key → Green*. The window may sit behind the game
but must not be minimized.

---

## Tests

```bash
MouseSwipeVisualizer.exe --selftest [--selftest-filter text] [--selftest-skip-camera] [--selftest-input-seconds 10]
```

The report goes to `%LocalAppData%\MouseSwipeVisualizer\selftest.txt`, together with PNG frames for visual
inspection. It covers:

* Raw Input, tracker, lift detection, settings, window placement and window properties,
* **off-screen rendering**: no window, chroma-safe, **0 halo pixels even after BGRA→NV12→BGRA**,
* **keyboard panel and frames**: the 40/60 split and all positions, key presses only change their own key,
  left/right modifiers, the static layer cache, frame and mouse area sizes (content only shrinks when an edge reaches
  it), anti-aliasing, background picture, glass and borderless modes,
* **Spotify**: encrypted secret storage (nothing in plain text, nothing in settings.json), parsing of tracks,
  episodes and "nothing playing", the cover URL allowlist, the local sign-in listener, and the cover replacing the
  frame picture and going away again (no real Spotify account needed),
* **headless**: the camera output keeps getting new frames while the preview is visible, covered, minimized, hidden
  or closed,
* **camera in-process** and **camera through the Windows Frame Server** (same suite):
  enumeration through `MFEnumDeviceSources`, opening with `IMFSourceReader`, fallback frames without the app, the
  requested format reaching the app, and **right flick / curve / left flick / lift + re-centre** read back through
  the camera and checked pixel by pixel (direction, 0 halo). It runs at 30 and 60 FPS (NV12 + RGB32, 800×800 and
  1280×720) and checks timestamps, durations, the discontinuity flag and cadence. It also covers: consumer stops →
  the app stops producing, app crash → fallback, app restart → reconnect, and two consumers.

The camera tests need the camera to themselves: close other copies of the app and any app that has the camera open
before running them.

Lifecycle (elevated, `installer/lifecycle-test.ps1`): remove the development registration → install the MSI → read
test → uninstall the MSI → **0 camera device nodes, no COM key, no files** → reinstall.

**Two apps at the same time** (measured): the first consumer keeps streaming undisturbed; a second *process* gets
`MF_E_HW_MFT_FAILED_START_STREAMING (0xC00D3704)`, the same behaviour as a physical webcam that is already in use.
Two readers in the *same* process share the stream.

---

## Performance (measured, Release)

| | |
|---|---|
| Engine per frame (1280×720, keyboard + frames) | input + model ~0.02 ms, rasterization ~1.3 ms, publishing ~0.7 ms, **0 B allocated per frame** |
| Camera side (Frame Server) | BGRA→NV12 **0.34 ms** for a new frame (fast path for uniform 2×2 blocks); a repeated frame is a row copy from the cache; a whole sample ~0.7 ms |
| CPU, app | **5.7 %** of one core at 800×800@60 while swiping; 4.8 % at 1280×720@30; **0.00 %** without a consumer |
| Cadence | 59.95–60.00 FPS / 30.00 FPS through the Frame Server, max interval ~18 ms at 60 FPS on an idle machine |

---

## Troubleshooting

**The camera doesn't show up.** Run `--camera-status`. *MediaSource registered: NO* → install it (MSI or
`--camera-install`). *Virtual camera registered: NO* → run `--camera-register` or start the app. Windows 10 is not
supported.

**The camera is green without a swipe.** The app isn't running (fallback picture), or Output mode = OBS. Start the
app (`--headless` is enough). In Diagnostics, *Camera active consumer* should be *YES* and *Frames produced* should
go up when you move the mouse.

**"Camera already in use" / 0xC00D3704.** Another app has the camera open. Close it; like a normal webcam, the
camera can only be streamed by one app at a time.

**The swipe doesn't move in games.** Is the game running as administrator? Then the visualizer must also run as
administrator, because Windows doesn't deliver input from an elevated process to a non-elevated one.

**The preview looks blocky.** Update to 3.0.8 or later; older versions scaled the preview with nearest-neighbour
sampling.

**Debugging the media source.** It runs inside the *FrameServer* service (`svchost`). Trace output goes through
`OutputDebugString` (use DebugView). Errors also show up in Diagnostics (*Last error*).

**Spotify: "Spotify refused the sign-in" or the browser shows "INVALID_CLIENT: Invalid redirect URI".** The
redirect URI in your Spotify app must match the one in Settings exactly, including `127.0.0.1` (Spotify no longer
accepts `localhost`) and the port. **"Port … is already in use"**: pick another callback port and update the
redirect URI in the dashboard to match.

**Logs:** `%LocalAppData%\MouseSwipeVisualizer\logs\app.log`.

---

## Project structure

| Path | Contents |
|---|---|
| `src/MouseSwipeVisualizer/` | C# WPF app: Raw Input, swipe engine, rendering, UI, camera link, commands, self-test |
| `src/MouseSwipeVisualizer/Engine/` | `SwipeEngine` (own thread/timing), `PreviewFrameStore`, `EngineStats` |
| `src/MouseSwipeVisualizer/Input/` | `RawMouseInput` (mouse + keyboard Raw Input), `KeyboardLayout` / `KeyboardState`, `MouseDeltaBuffer` |
| `src/MouseSwipeVisualizer/Rendering/` | `SwipeRenderModel`, `SwipeModelBuilder`, `SoftwareRasterizer`, `OverlayLayout` (frame/keyboard/swipe layout, key glyphs), `BackgroundImage` (picture loading and blur) |
| `src/MouseSwipeVisualizer/Spotify/` | `SpotifyService` (sign-in, polling, cover cache), `SpotifyClient` (Web API), `LoopbackCallback` (sign-in redirect), `SpotifySecrets` (DPAPI) |
| `src/MouseSwipeVisualizer/Camera/` | `CameraFrameLink` (shared memory), `VirtualCameraService`, `CameraCommands`, `CameraConsumer`, P/Invoke |
| `src/MouseSwipeVisualizer.VirtualCamera/` | C++ media source: `SwipeMediaSource`, `SwipeMediaStream`, `SwipeMediaSourceActivate`, `FrameLink`, `PixelConvert`, `CameraControl` (exports), `dllmain` (COM) |
| `src/MouseSwipeVisualizer.Shared/` | `SharedFrameProtocol.h` / `.cs` |
| `installer/` | WiX MSI (`Package.wxs`), `build-installer.ps1`, `upgrade-install.ps1`, `lifecycle-test.ps1` |

### Based on Microsoft's sample

The media source architecture follows Microsoft's **Windows-Camera / Samples/VirtualCamera** (MIT License,
© Microsoft Corporation):

* `SimpleMediaSource`/`SimpleMediaStream`: event queues, `Start`/`Stop`/`Shutdown`, `IMFMediaStream2` state, stream
  attributes, sensor profiles, `IMFSampleAllocatorControl`,
* `VirtualCameraMediaSourceActivate`: the IMFActivate pattern,
* `VCamUtils`: registration and removal, device-node cleanup through `CustomCaptureSourceClsid`.

The code is rewritten with WRL (no NuGet dependencies) and extended with pacing, a fallback allocator, IPC,
conversion and caching. Files based on the sample carry an MIT attribution in their header.
