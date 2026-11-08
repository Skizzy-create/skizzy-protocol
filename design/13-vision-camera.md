# 13 Vision and camera

Screenshots and camera control are for **on-demand** looks; the data streams carry the real-time load. Area `ksp.camera` is on by default (D11).

## 1. Capabilities

| Capability | Level | Notes |
|---|---|---|
| `ksp.camera.screenshot {width 256–1920, format jpeg|png, quality, hideUi, target?, mode?, orbit?}` | read | end-of-frame capture, downscale, encode, destroy textures (PLAN v0.2 §8.3); optional one-call target+mode+orbit so an agent gets "chase view of DDG-1 from astern" in one tool call |
| `ksp.camera.set_mode {auto|free|orbital|chase|locked}` | control | `FlightCamera.SetMode` |
| `ksp.camera.target {vessel|track|part}` | control | `SetTargetVessel`; tracks resolve to vessels under god view or when identity is known |
| `ksp.camera.orbit {heading, pitch, distance}` | control | clamped |
| `ksp.camera.look_at {from: vessel, to: vessel|track}` | control | positions the camera on the line between two objects (watching an engagement) |
| `ksp.camera.burst {count ≤ 4, intervalS}` (P2) | read | a short sequence for motion judgement, returned as several images |
| `ksp.camera.multi {targets:[…]}` (P2) | read | captures N targets in consecutive frames, returns N images |
| `ksp.camera.annotate` (P2) | read | burns track ids/BRAA labels into the image (SkiaSharp in the bridge from the simultaneous tactical snapshot) |
| `tac_map_image` (bridge) | read | headless map render (`11-tactical-map.md` §9) |

## 2. Caps and costs

Max 1 capture per 2 s per session, 4 per 2 s host-wide; ≤ 2 MB per image; ≤ 30 ms main-thread time at 1024 px (`ReadPixels` stall dominates); `hideUi` causes a one-frame UI flicker (default off per PLAN; decision for the user). Captures are recorded in `host.stats` and, when a recording is active, optionally stored at ≤ 1 per 10 s (`screenshots/`).

## 3. Agent usage patterns

- Builder agent: after `ksp.editor.apply`, one screenshot of the SPH to confirm the layout; after `craft.trial`, one chase shot at speed.
- Commander: rarely; a map render is cheaper and more informative than a 3D frame.
- Human: `ksp.camera.look_at {from:"DDG-1", to:"t:117"}` bound to a map button ("watch this engagement").

## 4. Acceptance

1. 1024 px JPEG ≤ 2 MB in ≤ 30 ms main-thread time; 100 captures leave `Texture2D`/`RenderTexture` counts unchanged; rate limit enforced; image displayed in Claude Code.
2. `look_at` frames both objects in the camera view (manual check) and works on tracks under god view.
