# KSP Render Probe — Stage 1 only

This is a non-DLSS diagnostic plugin for KSP 1.12.5 on Windows/D3D11. It deliberately does **not** redirect a camera, alter render scale, load NVIDIA libraries, or create temporal history.

## Architecture and why

`KspRenderProbe` is a single KSP `MonoBehaviour` loaded only in `Flight`. That covers external flight, map view, and IVA while avoiding menu/editor side effects. It uses no Harmony patches: Unity's public camera callbacks and `Camera.AddCommandBuffer` are enough for a first observation pass, and avoiding patches keeps the probe reversible.

The plugin does not claim to know KSP's primary camera. It logs all live cameras, but does not attach to any of them automatically. The tester cycles the selected camera and must explicitly attach the probe. On the first clean KSP 1.12.5 run, `Camera 00` at order 0 was labelled a **near-scene candidate**; UI/canvas, galaxy, scaled-space, marker, and FX cameras are blocked from attachment and jitter. This label is an observation from that run, not a general KSP assumption.

For the selected camera only, it ORs `Depth`, `DepthNormals`, and `MotionVectors` onto the existing `depthTextureMode`, retains the old value, and restores it on disable. It uses two command-buffer copies of the active color target (before image effects and after the camera's final event) to expose scene/UI ordering. It observes Unity's documented global depth, depth-normal, and motion-vector textures while that camera is rendering; a non-null texture proves exposure, not semantic correctness.

Projection jitter is opt-in and off by default. It is applied immediately before culling and restored after rendering. The probe never calls it a temporal-history test: KSP has no temporal reconstruction history in this stage. It only records the exact jitter/matrix contract and asks the tester to look for instability.

## Files

- `Source/KspRenderProbe.cs` — all Stage 1 code.
- `KspRenderProbe.csproj` — assembly references point at KSP 1.12.5 by default; override `KspDir` at build time.

## Build

The project targets .NET Framework 3.5 because that is the conservative compatibility target for KSP's Mono runtime. On a Windows development machine with the .NET Framework 3.5 developer tools installed:

```powershell
msbuild .\KspRenderProbe.csproj /p:KspDir='C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program'
```

Copy only the resulting `KspRenderProbe.dll` to `GameData/KspRenderProbe/Plugins/`. Do not deploy to a modded game first; use a copy of KSP with stock graphics for the baseline.

## Runtime controls

- `F8` — show/hide the overlay.
- `F7` — attach/detach depth/vector and color-capture instrumentation. This is permitted only for the explicit near-scene candidate.
- `F9` — enable/disable a tiny 8-sample Halton projection jitter. It is permitted only after `F7` attaches the near-scene candidate. Default: off.
- `F10` / `Shift+F10` — select next/previous observed camera.
- `F11` — force a fresh camera report.

Logs are written with the `[KspRenderProbe]` prefix to `KSP.log` and Unity's player log. The overlay gives fast visual context but is not data collection by itself.

## Required test sequence and interpretation

1. Start a **stock-graphics baseline** (no renderer/camera/post-processing mods). Enter external flight, wait five seconds, press `F11`, and save `KSP.log`.
2. Inspect candidate selection with `F10`; capture overlay screenshots for the selected external camera. Record its name/depth/order/target and the `BeforeImageEffects` versus `AfterEverything` thumbnails.
3. Repeat in map view and IVA. Do not assume the same camera applies, and do not attach/jitter a new camera until its role has been reviewed in the log.
4. With vessel and camera motion, inspect the motion-vector thumbnail. The current overlay displays raw textures; a black depth/vector thumbnail is not evidence that the texture is absent. Use the log's texture names/dimensions as existence evidence and treat semantic/vector coverage as unproven until a shader-based visualization asset is available. This does not prove every stock or mod shader writes correct object vectors.
5. Toggle `F9` for at least 30 seconds while panning and while the vessel moves. Save the log and screenshots. Success means no obvious render break, and logs show a changing jitter sample with an unchanged base projection. It does not establish temporal history stability—there is no temporal consumer yet.
6. Only after the baseline is archived, repeat with the intended visual-mod set. Any extra cameras or command buffers are evidence of a compatibility dependency, not a failure to ignore.

## Stage 1 success gate

Do **not** start Stage 2 unless the baseline shows all of the following for a manually selected external-flight camera:

1. D3D11 is active and motion vectors are supported.
2. Depth, depth-normal, and motion-vector texture objects are observed while the camera renders.
3. The observed vector field visibly responds to camera and vessel movement with correct-looking direction/coverage.
4. The pre/post color captures establish a usable scene-only composition point (or clearly document where UI/post processing enters).
5. Opt-in jitter can be enabled/disabled and restored without visual corruption through a flight/map/IVA transition.

Failure is useful data. Collect the complete `[KspRenderProbe]` log section, overlay screenshots, KSP version, graphics settings, scene, selected-camera name, and full graphics-mod list. Stop there; do not add redirect or DLSS work to compensate for an unverified input contract.
