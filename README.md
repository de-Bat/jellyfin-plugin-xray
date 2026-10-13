# X-Ray for Jellyfin

Prime Video–style X-Ray for Jellyfin. **Phase 1: see which actors are in the current scene.**

Pause the video, bring up the player controls, or press **X**, and a panel slides in from the left showing
the cast members on screen right now, with their character names. An **All Cast** tab lists everyone and
when they next appear.

## How it works

1. **Analysis (server)**: ffmpeg samples a frame every 2 s. Each frame goes through SCRFD face detection
   and ArcFace embeddings (ONNX Runtime, via [FaceAiSharp](https://github.com/georg-jung/FaceAiSharp)).
   Every face is compared with the cast members' head shots from your Jellyfin metadata (TMDb).
2. **Self-training**: the most confident frame matches are added as extra reference faces and every face
   is matched again, which picks up actors whose look in the film differs from their head shot.
3. **Timeline**: sightings are merged into appearances (gaps under 20 s are bridged, so shot/reverse-shot
   counts as one scene) and stored as JSON in the plugin data folder.
4. **Client**: a small script injected into jellyfin-web loads the timeline once per video and shows the
   people present at the current playback position.

Analysis runs in the **Build X-Ray scene data** scheduled task (daily at 3 AM by default, movies and
episodes without data), or on demand: admins get an **Analyze now** button in the panel. An 8-minute
1080p clip takes about 100 s on a desktop CPU.

The first analysis downloads the ONNX Runtime native library (~15–23 MB for your platform) and the face
models (~69 MB) from NuGet into the plugin data folder. It reads them with HTTP range requests, so the
full 130 MB runtime package is not downloaded. Supported: Windows x64/arm64, Linux x64/arm64, macOS arm64.

## Install

### From the plugin repository (recommended)

1. Dashboard → Plugins → Repositories → **+**
2. Name: `X-Ray`, URL:
   `https://raw.githubusercontent.com/de-Bat/jellyfin-plugin-xray/main/manifest.json`
3. Catalog → **X-Ray** → Install, then restart Jellyfin.

### Manual

Download `xray_<version>.zip` from [Releases](https://github.com/de-Bat/jellyfin-plugin-xray/releases),
or build it:

```sh
dotnet publish src/Jellyfin.Plugin.XRay -c Release -o publish
```

Copy the `*.dll` files from `publish/` into `<jellyfin data>/plugins/XRay_0.2.0.0/` and restart Jellyfin.
This build targets Jellyfin **12.2** (net10.0).

### Web client

The plugin adds `<script data-xray-plugin src="../XRay/ClientScript">` to jellyfin-web's `index.html` at
startup, and removes it when you turn off the option. If Jellyfin cannot write that file (for example in
read-only Docker images), load `/XRay/ClientScript` with a script-injector plugin instead.

### Android TV

The official Android TV app is native, so plugins can't add to it. A fork with an X-Ray panel built into
both of its video players lives at
[de-Bat/jellyfin-androidtv](https://github.com/de-Bat/jellyfin-androidtv/tree/xray) (branch `xray`).
Sideload the APK from its [releases](https://github.com/de-Bat/jellyfin-androidtv/releases). Pause, or
press any button to bring up the controls, and the cast on screen appears top-left.

## API

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/XRay/Items/{itemId}` | user | Cast, appearances, and analysis status |
| GET | `/XRay/Items/{itemId}/Scene?positionTicks=` | user | Person ids on screen at a position |
| POST | `/XRay/Items/{itemId}/Analyze` | admin | Queue face analysis |
| POST | `/XRay/Folders/Analyze?path=&overwrite=` | admin | Queue every movie/episode in a folder (and subfolders); `overwrite=true` redoes analyzed ones |
| POST | `/XRay/Items/{itemId}/Timeline` | admin | Replace with manual data: `[{PersonId, StartTicks, EndTicks}]` |
| DELETE | `/XRay/Items/{itemId}/Timeline` | admin | Remove data |
| GET | `/XRay/ClientScript` | none | Web client script |

## Settings

Dashboard → Plugins → X-Ray: sample interval, frame width, minimum face size, match threshold (default
0.40) and margin, merge gap, cast size, which item types the task covers, and web injection.

**Specific folders**: put one path per line in *Only these folders* to limit the scheduled task to those
folders and their subfolders (paths as the server sees them). To analyze one folder right away, call
`POST /XRay/Folders/Analyze?path=/media/movies/Marvel` with an admin API key.

**Log file**: X-Ray writes its own `xray_yyyyMMdd.log` (including debug messages) to Jellyfin's log folder,
so it also appears under Dashboard → Logs. The last 7 days are kept; turn it off under *Logging*.

## Notes

- Only plain video files are analyzed (not ISO/BluRay folders). Cast members without a head shot can't be
  recognized.
- The ArcFace and SCRFD weights come from InsightFace and are licensed for **non-commercial research
  use only**.
- FaceAiSharp pins SixLabors.ImageSharp 3.1.12, which has a moderate advisory (GHSA-wmxv-xphr-5c9g). The
  plugin only decodes ffmpeg output and Jellyfin's own metadata images with it.

## Development

```sh
dotnet test
```

Unit tests cover appearance merging, scene queries, face matching, BMP frame splitting and
`index.html` injection.

## Releasing

Bump `changelog` (and `targetAbi` if needed) in `build.yaml`, then push a tag:

```sh
git tag v0.2.0.0 && git push origin v0.2.0.0
```

The Release workflow tests, builds `xray_<version>.zip`, publishes a GitHub release, and adds the version
(with its MD5 checksum) to `manifest.json` on `main`.
