# Mouse Swipe Visualizer

Visualiserar din **fysiska musrörelse** som korta, bleknande swipes och levererar bilden som en
**riktig Windows-kamera**: **"Mouse Swipe Visualizer Camera"**. Medal (eller vilken kamera-app som helst)
kan välja den direkt – ingen OBS behövs.

```
Windows Raw Input (WM_INPUT, RIDEV_INPUTSINK – även när spelet har fokus)
        ↓
RawMouseInput ─→ MouseDeltaBuffer
        ↓
SwipeEngine (egen tråd, egen timing)
   SwipeTracker → SwipeModelBuilder → SwipeRenderModel → SoftwareRasterizer (BGRA, off-screen)
        ↓                                                   ↘
CameraFrameLink (delat minne Global\…, seqlock)              PreviewFrameStore → valfri preview-fönster
        ↓
MouseSwipeVisualizer.VirtualCamera.dll  (C++ Media Foundation media source, körs i Windows Camera Frame Server)
   IMFMediaSourceEx / IMFMediaStream2 → BGRA→NV12/RGB32 → IMFSample
        ↓
"Mouse Swipe Visualizer Camera" (MFCreateVirtualCamera, SoftwareCameraSource)
        ↓
Medal → Settings → Video Overlays → Virtual Camera Capture
```

**Videon är oberoende av fönster.** Renderingen sker i en egen tråd till en minnesbuffert – inget
WPF-fönster, ingen skärmdump, ingen `PrintWindow`. Kameran fungerar lika bra när Settings/preview är
täckt, minimerat, dolt, stängt, eller när appen bara kör i tray (`--headless`).

**Tangentbord + ram.** Bilden delas 40/60: till vänster ett tangentbordsblock (Esc–5, Tab–T, Caps–G,
Shift–B, Ctrl/Alt/Space) där nedtryckta tangenter lyser upp, till höger swipen i en egen ram (musytan). Allt ligger på en
stylad panel ("frame") med bakgrund, kantfärg, kantbredd och rundade hörn. Allt är inställbart
(se [Settings](#settings)); tangentbord och ram kan stängas av för ren swipe över hela bilden.

Programmet **observerar** bara input – det injicerar, ändrar eller blockerar ingenting och rör inte spelprocessen.
Kameran är en **user-mode** Media Foundation media source (ingen kernel-drivrutin).

---

## Requirements

| | |
|---|---|
| OS | **Windows 11 build 22000 eller senare** för den virtuella kameran (utvecklat/testat på build 26200). Appen startar även på Windows 10, men utan kamera (Settings visar då *"Native Windows Virtual Camera is not supported on this Windows version. Requires Windows 11 build 22000 or later."*). |
| Köra (MSI) | Inget extra – self-contained .NET, statiskt länkad C++-runtime (ingen VC++ redistributable behövs). |
| Bygga | .NET SDK 9.0 + **Visual Studio 2022** (Community räcker) med workloads **.NET desktop development** och **Desktop development with C++** (toolset v143) + **Windows 11 SDK** (≥ 10.0.22000; projektet använder installerade **10.0.26100.0**). |
| Installer | WiX v5 hämtas automatiskt via NuGet (`WixToolset.Sdk`). |

## Build

```bash
dotnet restore
```

```bash
dotnet build -c Release
```

`dotnet build` bygger även C++-projektet: `MouseSwipeVisualizer.csproj` hittar Visual Studios MSBuild via
`vswhere` och bygger `MouseSwipeVisualizer.VirtualCamera.vcxproj`; DLL:en kopieras bredvid exe:n.
(Utan C++-verktyg: `dotnet build -c Release -p:SkipNativeBuild=true` – då utan kamera.)

MSI (publish + WiX):

```bash
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

→ `installer/bin/x64/Release/MouseSwipeVisualizer-3.0.7.0-x64.msi`

**VS Code:** öppna mappen, installera rekommenderade tillägg (C# Dev Kit). F5 = kör, *Run Task* har
`build`, `build installer (MSI)`, `camera: status`, `camera: install / repair (UAC)`, `camera: test`, `self-test`.
Launch-konfigurationen *headless / tray only* kör utan fönster.

---

## Installation och registrering av kameran

### Rekommenderat: MSI

Dubbelklicka på MSI:n (UAC-fråga). Den:

1. installerar `MouseSwipeVisualizer.exe` + `MouseSwipeVisualizer.VirtualCamera.dll` i `C:\Program Files\MouseSwipeVisualizer\`,
2. COM-registrerar media source-DLL:en i **HKLM** (`CLSID {0728D89A-2065-4F45-85F7-B128DA227817}`, `ThreadingModel=Both`),
3. skapar kameran för den installerande användaren (körs *utan* admin efter installationen).

Avinstallation (Inställningar → Appar, eller `msiexec /x`) kör **före** filborttagningen en upphöjd åtgärd som tar
bort **alla** kamera-enheter skapade från vår CLSID (alla användare), COM-registreringen och eventuella
utvecklingskopior – inga föräldralösa kameror blir kvar (verifierat, se *Tester*). Uppgradering behåller kameran.

### Utvecklingsflöde: kommandon

| Kommando | Vad | Admin? |
|---|---|---|
| `MouseSwipeVisualizer.exe --camera-status` | API-stöd, registrering, uppräkning, räknare | nej |
| `MouseSwipeVisualizer.exe --camera-install` | kopierar DLL:en till Program Files + HKLM-COM (UAC), skapar sedan kameran | UAC för steg 1 |
| `MouseSwipeVisualizer.exe --camera-register` | skapar/öppnar kameran för denna användare (idempotent) | nej |
| `MouseSwipeVisualizer.exe --camera-unregister` | `IMFVirtualCamera::Remove` för denna användare | nej |
| `MouseSwipeVisualizer.exe --camera-remove` | full borttagning: kamera(or), device nodes, COM, filer | UAC |
| `MouseSwipeVisualizer.exe --camera-diagnose` | status + alla kameror + kort läs-test | nej |
| `MouseSwipeVisualizer.exe --camera-test [--frames N] [--format 1280x720@30] [--rgb32] [--save-frame fil.png]` | öppnar kameran som Media Foundation-konsument och läser bilder | nej |

Exe:n är en GUI-app; i PowerShell: `.\MouseSwipeVisualizer.exe --camera-status | Out-Host`.
Samma funktioner finns som knappar i **Settings → Output**.

### Varför CurrentUser + System lifetime – och varför ändå admin?

Två **olika** saker behöver sättas upp:

* **Media source-DLL:en (COM)** laddas av tjänsterna *Windows Camera Frame Server* (LOCAL SERVICE) och
  *Frame Server Monitor* (LOCAL SYSTEM) i session 0. De ser bara **HKLM**-registreringar och kan bara läsa filer
  på en plats som t.ex. Program Files. Det kräver admin **en gång, vid installation**.
* **Själva kameran** skapas med `MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
  MFVirtualCameraLifetime_System, MFVirtualCameraAccess_CurrentUser, …)`:
  * **`CurrentUser`**: kameran syns bara för ditt Windows-konto och kräver **ingen** admin (`AllUsers` gör det).
  * **`System` lifetime**: kameran finns kvar mellan appstarter och omstarter, så Medal ser den även när appen
    inte körs (då visas en ren chroma-bakgrund). Appen återskapar den dessutom idempotent vid varje start.
    Avinstallationen tar alltid bort den (`IMFVirtualCamera::Remove` + borttagning av device nodes).

Windows lägger själv till *"Windows Virtual Camera"* i namnet – på svensk Windows heter kameran
**"Mouse Swipe Visualizer Camera (Windows Virtuell Kamera)"**.

---

## Köra

```bash
MouseSwipeVisualizer.exe             # Settings + preview (preview kan stängas; kameran påverkas inte)
MouseSwipeVisualizer.exe --headless  # bara tray-ikon, inga fönster – full kamerafunktion
```

Start-menyn har båda varianterna. Tray-ikonen: *Show preview*, *Settings…*, *Clear trail*, *Exit*.

### Rendera bara när någon tittar

Media source skapar det delade minnet när en konsument (Medal) startar strömmen och uppdaterar en heartbeat per
bild. Appen känner av det (polling 4 Hz utan konsument) och renderar **bara då** i konsumentens upplösning och
FPS. Utan konsument: ingen 60 FPS-rendering (uppmätt **0,00 %** CPU). Oförändrad bild rasteriseras inte om –
kameran upprepar senaste bilden med nya timestamps.

---

## Video formats

Kameran exponerar (första = standard):

| Upplösning | FPS | Format |
|---|---|---|
| **1280 × 720** | **30** (standard) | NV12, RGB32 |
| 1280 × 720 | 60 | NV12, RGB32 |
| 800 × 800 | 60 | NV12, RGB32 |
| 800 × 800 | 30 | NV12, RGB32 |
| 640 × 480 | 30 | NV12, RGB32 |

NV12 är vad kamerapipelinen och de flesta appar föredrar; RGB32 finns för appar som vill ha det. Bilden
renderas direkt i den begärda upplösningen (swipe-ytan centreras i 16:9-bilden). Sensorprofilen *Legacy* är
satt att visa alla format (även 60 FPS) för appar som inte är profil-medvetna.

---

## IPC: app → kamera

* Namngivet delat minne **`Global\MouseSwipeVisualizerCamera.Frames.v1`** (24 MiB: 4 KiB header + 3 bildslottar à 8 MiB).
  Media source (LOCAL SERVICE, session 0) skapar det med en DACL som ger *interaktiva användare* läs/skriv –
  en vanlig användarprocess får inte skapa `Global\`-objekt. Layouten finns i
  `src/MouseSwipeVisualizer.Shared/SharedFrameProtocol.h` (C++) och `.cs` (C#), byte-identiska.
* **Ingen tearing:** trippelbuffring + **seqlock** per slot. Producenten skriver bara till en icke-publicerad slot
  (seq udda → skriv → seq jämn → publicera slot-index). Konsumenten kopierar och godkänner bara om seq var jämn
  och oförändrad före och efter kopian. Ingen mutex.
* **Otillförlitlig data:** media source räknar själv ut offset/storlek och kräver exakt matchning
  (`width/height` = begärd, `stride = width*4`, `offset = header + slot*kapacitet`, `size ≤ kapacitet`) – korrupt
  metadata kan aldrig ge läsning utanför mappningen. Appen validerar på samma sätt det kameran skriver.
* **App kör inte / har kraschat:** producer-heartbeat äldre än 1 s → kameran levererar en solid bakgrundsbild
  (chroma-grön) i rätt takt. Appen återansluter automatiskt vid omstart.
* **Konsumenten slutar:** consumer-heartbeat står still > 1 s → appen slutar producera.

---

## Medal

### Testplan (manuell – kräver Medal)

1. Installera MSI:n (eller `--camera-install`) och starta MouseSwipeVisualizer.
2. Settings → **Output** ska visa *"Virtual camera: Ready – …"*; `--camera-status` ska visa
   `Virtual Camera: Registered   Status: Ready`.
3. Starta Medal.
4. Öppna **Settings → Video Overlays**.
5. Slå på **Video Overlay**.
6. Slå på **Virtual Camera Capture**.
7. Leta efter **"Mouse Swipe Visualizer Camera (Windows Virtuell Kamera)"** (namnet kan få Windows-suffixet på annat språk).
8. Välj kameran.
9. Rör musen.
10. Kontrollera Medals förhandsvisning – swipen ska synas (Settings → Diagnostics visar *Camera active consumer: YES*, begärt format och bildräknare).
11. Starta ett spel.
12. Skapa ett Medal-clip.
13. Kontrollera att swipen finns i det färdiga clipet.

**Status:** Windows-sidan är verifierad (uppräkning, öppning, bildflöde genom Frame Server, se *Tester*).
**Medal är inte verifierat** – det kräver att du går igenom stegen ovan. Resultatet dokumenteras här när testet är gjort.

### Viktig begränsning: transparens

En kamerabild har ingen användbar alfakanal. Kameran levererar därför swipen på en **chroma-grön bakgrund**
(`#00FF00`) med *chroma-safe edges* (inga halvtransparenta gröna kantpixlar, även efter NV12:s 2×2-chroma-
subsampling). Medals dokumentation beskriver **ingen chroma key** för Video Overlay – anta därför att overlayn
visas som en **ogenomskinlig ruta** med grön bakgrund i Medal. Det är en produktbegränsning i Medal, inte något
vi kan lösa i kameran. Alternativ:

* Stäng av *Chroma-key background* i Settings → bakgrunden blir **svart** (diskretare ruta).
* Behöver du genomskinlig swipe över spelet: använd **OBS-läget** (Output mode = *OBS Capture Window*) med
  Chroma Key / Blending Mode *Screen* i OBS, se nedan.

---

## Settings

Sparas i `%LocalAppData%\MouseSwipeVisualizer\settings.json` (schema 4; äldre filer läses in).

| Setting | Default | Beskrivning |
|---|---|---|
| `OutputMode` | `NativeVirtualCamera` | `NativeVirtualCamera` eller `ObsCaptureWindow` (v2-fallback). |
| `ShowPreview` | true | Visa preview-fönstret vid start. Kameran fungerar utan det. |
| `CaptureWidth`/`CaptureHeight` | 800 × 800 | Previewns / OBS-fönstrets storlek. Kameran använder konsumentens begärda upplösning. |
| `CaptureBackgroundEnabled` | true | Chroma-bakgrund (`ChromaKeyColor`), annars svart. Gäller kamera och preview. |
| `ChromaKeyColor` | `#00FF00` | Bakgrundsfärg (även kamerans fallback-bild). |
| `ChromaSafeEdges` | true | Opak färgramp-fade + hård, 2×2-blockjusterad kontur → ingen grön halo efter keying, även i NV12. |
| `SensitivityScale` | 1.0 | 800 counts = mitt → kant vid 1.0. |
| `SwipeBreakMs` | 120 | Paus som avslutar en swipe (nästa startar i mitten). |
| `LiftDetectionEnabled` / `LiftGapMs` | true / 40 | Lyft-och-re-center-detektering. |
| `SmoothingStrength` | 0.35 | Visuell utjämning. |
| `TrailLifetimeMs` | 500 | Hur länge trailen syns. |
| `TrailThickness` | 4 | Linjebredd i bildpixlar. |
| `TrailColor` | `#FFFFFF` | Linjens färg. |
| `OutlineEnabled` / `OutlineColor` | true / `#141418` | Kontur runt linje, pil och prick. |
| `HeadStyle` | `Arrow` | Huvudet på swipen: `Arrow`, `Dot` eller `None`. |
| `DotColor` / `DotSize` | `#FFFFFF` / 12 | Prickens färg och diameter (px, 2–48) när `HeadStyle` = `Dot`. |
| `KeyboardEnabled` | true | Visa tangentbordsblocket. |
| `KeyboardPosition` | `Left` | `Left`, `Right`, `Top` eller `Bottom` om swipen. |
| `KeyboardSplitPercent` | 40 | Tangentbordets andel av ytan (20–70); swipen får resten (40/60). |
| `KeyFillColor` / `KeyBorderColor` / `KeyLabelColor` | `#000000` / `#E6E6E6` / `#FFFFFF` | Tangent i vila. |
| `KeyPressedFillColor` / `KeyPressedLabelColor` | `#FFFFFF` / `#000000` | Nedtryckt tangent. |
| `SwipeBoxEnabled` | true | Ram runt musytan (swipen), i samma stil som tangenterna. |
| `SwipeBoxFillColor` / `SwipeBoxBorderColor` | `#000000` / `#E6E6E6` | Musytans bakgrund och kant. |
| `SwipeBoxBorderWidth` / `SwipeBoxCornerRadius` / `SwipeBoxPadding` | 2 / 12 / 10 | Kantbredd, hörnradie och luft mellan kant och swipe (px). |
| `SwipeBoxWidthPercent` / `SwipeBoxHeightPercent` | 100 / 100 | Musramens bredd/höjd i % av sin yta (20–100, centrerad). Swipen behåller sin storlek och krymper först när ramkanten når den. |
| `BackgroundImagePath` | tom | Bild bakom allt (ersätter chroma-färgen). Skalas så att den täcker bilden. |
| `BackgroundImageBlur` / `BackgroundImageDim` | 24 / 20 | Oskärpa (px) och mörkning (%) av bakgrundsbilden. |
| `GlassEnabled` | false | Glaslook: ram, musyta och tangenter blir frostat, genomskinligt glas över (en extra suddig kopia av) bakgrundsbilden. Nedtryckta tangenter är solida. |
| `GlassTintColor` / `GlassOpacity` / `GlassBlur` | `#FFFFFF` / 12 / 16 | Glasets ton, tonstyrka (%) och extra frost-oskärpa (px). |
| `BordersEnabled` | true | Kanter på ram, musyta och tangenter (av = kantlöst). |
| `FrameEnabled` | true | Rita panelen ("frame") bakom tangentbord och swipe. |
| `FrameBackgroundColor` / `FrameBorderColor` | `#000000` / `#E6E6E6` | Panelens bakgrund och kant. |
| `FrameBorderWidth` / `FrameCornerRadius` | 2 / 16 | Kantbredd och hörnradie (px). |
| `FrameWidthPercent` / `FrameHeightPercent` | 100 / 100 | Panelens bredd/höjd i % av bilden (20–100, centrerad). Tangentbord och swipe behåller sin storlek och krymper först när panelkanten når dem. |
| `FrameMargin` / `FramePadding` | 6 / 14 | Avstånd bildkant → panel, och panelkant → innehåll (px). |
| `RenderFps` | 60 | Preview-FPS när ingen kamera-konsument styr takten. |
| `IncludeDebugInCapture` | false | Debugtext i OBS-fönstret (utveckling). |
| `ShowSettingsOnStartup` | true | Öppna Settings vid start. |

**Tangentbordet och integritet:** Raw Input för tangentbordet registreras bara när `KeyboardEnabled` är på.
Endast upp/ner-läget för de 27 tangenterna i blocket hålls i minnet (per fysisk tangent/scan code, så
layouten är densamma oavsett språk). Inga andra tangenter lagras, inget loggas, inget skrivs till disk.
Precis som för musen: programmet observerar bara, det injicerar eller blockerar ingenting.

**Kantutjämning:** allt som ritas på en panel, i musytan eller på en bakgrundsbild är kantutjämnat
(swipe, kontur, pil, prick, tangenter, ramar). Hårda, 2×2-justerade kanter används bara där något
direkt möter chroma-färgen (panelens ytterkant, eller swipen när varken ram eller musyta är på), annars
blir det en grön kant efter keying. Med en bakgrundsbild finns ingen chroma-färg, så allt är utjämnat.

Ramen och tangentbordet ritas i ett cachat statiskt lager: bakgrund + ram byggs om bara när stil eller
storlek ändras, tangentbordet bara när en tangent går upp/ner. Kanterna är 2×2-blockjusterade i
chroma-safe-läget, så ingen grön halo uppstår efter NV12 och keying.

**Diagnostics** (Settings, nederst) visar bland annat:

```
Virtual Camera API supported: YES
MediaSource registered: YES (C:\Program Files\MouseSwipeVisualizer\MouseSwipeVisualizer.VirtualCamera.dll)
Virtual camera registered: YES
Virtual camera enumerable: YES
Camera friendly name: Mouse Swipe Visualizer Camera (Windows Virtuell Kamera)
Camera active consumer: YES/NO
Requested format: 1280x720 NV12 @ 30 FPS
Frames produced / Frames consumed / Dropped/repeated frames
Camera side: conversion … ms, sample creation total … ms
Last consumer start / Last error
```

plus engine-statistik (input events/s, dx/dy, frame-tider per steg, allokering per bild).

---

## OBS-läget (fallback)

Output mode = **OBS Capture Window** → preview-fönstret *"Mouse Swipe Visualizer"* är OBS-källan (Window: [MouseSwipeVisualizer.exe]: Mouse Swipe Visualizer)
(som i v2): *Sources → + → Window Capture*, Capture Method *Windows 10 (1903 and up)*, *Client Area* på,
*Filters → Chroma Key → Green*. Fönstret får ligga bakom spelet men ska inte minimeras.

---

## Tester

```bash
MouseSwipeVisualizer.exe --selftest [--selftest-filter text] [--selftest-skip-camera] [--selftest-input-seconds 10]
```

Rapport: `%LocalAppData%\MouseSwipeVisualizer\selftest.txt` (+ PNG-bilder). Senaste körning: **264/264 PASS**. Innehåller:

* alla tidigare tester (Raw Input, tracker, lift, settings, placering, fönsteregenskaper …),
* **off-screen**: rendering utan fönster, chroma-safe, **0 halo-pixlar även efter BGRA→NV12→BGRA**,
* **headless**: kamera-utgången får nya, ändrade bilder när preview är synlig/täckt/minimerad/dold/stängd,
* **kamera in-process** och **kamera genom Windows Frame Server** (samma svit):
  uppräkning via `MFEnumDeviceSources`, öppning med `IMFSourceReader`, fallback-bilder utan app, begärt format
  når appen, **höger-flick / kurva / vänster-flick / lyft+re-center** läses genom kameran och kontrolleras pixel för
  pixel (riktning, 0 halo), 30 och 60 FPS (NV12 + RGB32, 800×800 och 1280×720) med kontroll av timestamps,
  durations, discontinuity-flagga och kadens, konsument stoppar → appen slutar producera, app-krasch → fallback,
  app-omstart → återanslutning, två konsumenter.

Livscykel (upphöjt, `installer/lifecycle-test.ps1`): ta bort dev-registrering → MSI-installation → läs-test →
MSI-avinstallation → **0 kamera-device nodes, ingen COM-nyckel, inga filer** → ominstallation.

**Två appar samtidigt** (uppmätt): den första konsumenten strömmar ostört vidare; en andra *process* får
`MF_E_HW_MFT_FAILED_START_STREAMING (0xC00D3704)` – samma beteende som en fysisk webbkamera som redan används.
Två läsare i *samma* process delar strömmen.

---

## Prestanda (uppmätt, Release)

| | |
|---|---|
| Engine per bild (800×800) | input+modell ~0,02 ms, rasterisering ~1 ms, publicering ~0,5 ms, **0 B allokering/bild** |
| Kamerasidan (Frame Server) | BGRA→NV12 **0,34 ms** för ny bild (snabbväg för enfärgade 2×2-block), upprepad bild = radkopiering från cache; hel sample ~0,7 ms |
| CPU, app | **5,7 %** av en kärna vid 800×800@60 med aktiv swipe; 4,8 % vid 1280×720@30; **0,00 %** utan konsument |
| Kadens | 59,95–60,00 FPS / 30,00 FPS genom Frame Server, max-intervall ~18 ms vid 60 FPS |

---

## Troubleshooting

**Kameran syns inte** – kör `--camera-status`. *MediaSource registered: NO* → installera (MSI eller
`--camera-install`). *Virtual camera registered: NO* → `--camera-register` eller starta appen. Windows 10 stöds inte.

**Kameran är grön utan swipe** – appen kör inte (fallback-bild), eller Output mode = OBS. Starta appen
(`--headless` räcker). Diagnostics: *Camera active consumer: YES* och *Frames produced* ska öka när du rör musen.

**"Kameran används redan" / 0xC00D3704** – en annan app har kameran öppen. Stäng den (kameran kan bara
strömmas av en app åt gången, som en vanlig webbkamera).

**Swipen rör sig inte i spel** – kör spelet som administratör? Då måste även visualizern köras som administratör
(Windows levererar inte input från en upphöjd process till en icke-upphöjd).

**Felsöka media source** – den körs i tjänsten *FrameServer* (`svchost`). Spårutskrifter via `OutputDebugString`
(DebugView). Fel publiceras även i Diagnostics (*Last error*).

**Loggar** – `%LocalAppData%\MouseSwipeVisualizer\logs\app.log`.

---

## Projektstruktur

| Sökväg | Innehåll |
|---|---|
| `src/MouseSwipeVisualizer/` | C# WPF-app: Raw Input, swipe-motor, rasterisering, UI, kamera-länk, kommandon, självtest |
| `src/MouseSwipeVisualizer/Engine/` | `SwipeEngine` (egen tråd/timing), `PreviewFrameStore`, `EngineStats` |
| `src/MouseSwipeVisualizer/Rendering/` | `SwipeRenderModel`, `SwipeModelBuilder`, `SoftwareRasterizer` |
| `src/MouseSwipeVisualizer/Camera/` | `CameraFrameLink` (delat minne), `VirtualCameraService`, `CameraCommands`, `CameraConsumer`, P/Invoke |
| `src/MouseSwipeVisualizer.VirtualCamera/` | C++ media source: `SwipeMediaSource`, `SwipeMediaStream`, `SwipeMediaSourceActivate`, `FrameLink`, `PixelConvert`, `CameraControl` (exports), `dllmain` (COM) |
| `src/MouseSwipeVisualizer.Shared/` | `SharedFrameProtocol.h` / `.cs` |
| `installer/` | WiX-MSI (`Package.wxs`), `build-installer.ps1`, `upgrade-install.ps1`, `lifecycle-test.ps1` |

### Baserat på Microsofts sample

Media source-arkitekturen följer Microsofts **Windows-Camera / Samples/VirtualCamera** (MIT License,
© Microsoft Corporation): `SimpleMediaSource`/`SimpleMediaStream` (händelseköer, `Start`/`Stop`/`Shutdown`,
`IMFMediaStream2`-tillstånd, stream-attribut, sensorprofiler, `IMFSampleAllocatorControl`),
`VirtualCameraMediaSourceActivate` (IMFActivate-mönstret), `VCamUtils` (registrering/borttagning, device-node-
städning via `CustomCaptureSourceClsid`). Koden är omskriven med WRL (inga NuGet-beroenden) och utökad med
takthållning, fallback-allocator, IPC, konvertering och cache. Filer som bygger på samplet har MIT-attribution i huvudet.
