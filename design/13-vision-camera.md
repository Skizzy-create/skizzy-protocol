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
| **[R]** `ksp.camera.save {name?}` | read | same capture, written to `Skizzy/screenshots/<date>/` instead of (or as well as) being returned; for the in-game button and the CLI `skizzy shot` |

Screenshots and camera control ship in **M0** (pass 1.0 left them unscheduled). Front ends: MCP image content, Python/CLI (`skizzy shot --target DDG-1 --mode chase -o shot.jpg`), map button, in-game camera window and hotkey.

### 1.1 Capture pipeline [R]

```
end of frame (WaitForEndOfFrame coroutine, main thread)
  ScreenCapture.CaptureScreenshotIntoRenderTexture(rtFull)     pooled RenderTexture, screen size (UI included unless hideUi)
  Graphics.Blit(rtFull, rtSmall, scale, offset)                 downscale to the requested width; vertical flip where the graphics API needs it
  AsyncGPUReadback.Request(rtSmall, 0, TextureFormat.RGBA32, cb) no CPU stall; result 1–3 frames later
callback (main thread): request.GetData<byte>().CopyTo(pooledBytes)   one copy into a pooled byte[]
writer thread: ImageConversion.EncodeArrayToJPG(pooledBytes, R8G8B8A8_UNorm, w, h, 0, quality)  (or EncodeArrayToPNG)
  → bin payload to the session / file
```

- All four Unity APIs exist in KSP's Unity 2019.4 (`UnityEngine.ScreenCaptureModule`, `CoreModule`, `ImageConversionModule` checked). Whether `EncodeArrayToJPG` is safe off the main thread is an **M0 spike**; if not, encoding moves to the main thread (≤ 8 ms at 1024 px, still under the cap) and the budget row is updated.
- **Fallback:** when `SystemInfo.supportsAsyncGPUReadback` is false (e.g. OpenGL), `ReadPixels` into a pooled `Texture2D` (≤ 30 ms stall at 1024 px), as PLAN v0.2 §8.3.
- `hideUi:true` fires `GameEvents.onHideUI`, captures, then `onShowUI` the next frame (one frame without UI; default `false`).
- RenderTextures and byte buffers are pooled per size and released after 60 s unused; the self-check counts `RenderTexture`/`Texture2D` objects before and after 100 captures.

### 1.2 Camera modes [R]

| IDEAS mode | Implementation |
|---|---|
| follow | `FlightCamera.SetTargetVessel(v)` + mode `AUTO` (KSP picks orbital/chase by situation) |
| orbit | mode `ORBITAL` + `camHdg`/`camPitch`/`SetDistance` from `{heading, pitch, distance}` |
| chase | mode `CHASE` (behind the velocity vector) |
| free | mode `FREE`; camera stays where it is in world space |
| look-at-target | target the `from` vessel, compute heading/pitch so the `to` object lies on the far side of the `from` vessel at the requested framing, mode `FREE` hold; updated every frame while active |
| locked | mode `LOCKED` (kept for completeness) |

The active vessel never changes because of a camera call: targeting another vessel uses `SetTargetVessel`/`SetTargetTransform`, not `FlightGlobals.SetActiveVessel`, so leases and the human-override rule are unaffected.

## 2. Caps and costs

Max 1 capture per 2 s per session, 4 per 2 s host-wide; ≤ 2 MB per image; **[R]** ≤ 3 ms main-thread time at 1024 px with `AsyncGPUReadback` (≤ 30 ms with the `ReadPixels` fallback); `hideUi` causes a one-frame UI flicker (default off; decided, `18-risks-and-decisions.md` §3.2 D-10). Captures are recorded in `host.stats` and, when a recording is active, optionally stored at ≤ 1 per 10 s (`screenshots/`).

## 3. Agent usage patterns

- Builder agent: after `ksp.editor.apply`, one screenshot of the SPH to confirm the layout; after `craft.trial`, one chase shot at speed.
- Commander: rarely; a map render is cheaper and more informative than a 3D frame.
- Human: `ksp.camera.look_at {from:"DDG-1", to:"t:117"}` bound to a map button ("watch this engagement").

## 4. Acceptance

1. 1024 px JPEG ≤ 2 MB in ≤ 3 ms main-thread time with `AsyncGPUReadback` (≤ 30 ms on the fallback); no frame-time spike above 2 ms beyond the baseline p99 while capturing every 2 s for 5 minutes; 100 captures leave `Texture2D`/`RenderTexture` counts unchanged; rate limit enforced; image displayed in Claude Code.
2. `look_at` frames both objects in the camera view (manual check) and works on tracks under god view.
3. **[R]** Each of follow/orbit/chase/free/look-at is reachable from MCP, `skizzy shot`, the map and the in-game camera window; a camera call never changes the active vessel.
