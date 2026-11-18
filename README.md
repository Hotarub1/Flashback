<img src="assets/icon.svg" width="64" height="64" alt="">

# Flashback

A small Windows app that keeps the last moments of your main monitor in a rolling buffer and saves them as an MP4 when you press a hotkey. It records desktop sound and your microphone.

Created in [T3 Code](https://t3.codes).

## Using it

1. Run `dist\Flashback.exe`. It starts recording straight away and keeps running in the tray when you close the window.
2. Press **Ctrl+Shift+F9** (or the **Save clip** button) to save the last 30 seconds to `Videos\Clips`.
3. The **Clips** tab shows your clips. Click one to play it; right-click to rename it, delete it, or show it in its folder.

To exit, right-click the tray icon and choose **Exit**.

## Watching and editing a clip

Clips carry each app's sound and your microphone as separate tracks (plus a mix of everything that other players use). Every app that played sound during the clip gets its own track, so Discord can be turned down while the game stays as it was. The player lets you work with them:

- **Volume.** The slider next to the speaker button sets how loud the whole clip plays, and is remembered for the next clip. It only changes what you hear, not what a saved trim contains. Mute with the speaker button or **M**; moving the volume unmutes.
- **Volume per track.** The sliders under the video set how loud each app and the microphone are while you watch. They go up to 200% so a quiet microphone can be lifted. A clip where only one app made sound, or where the app tracks did not account for all the PC's sound (see below), has one **Desktop audio** track instead. Older clips show the tracks they were saved with.
- **Trim.** Drag the handles at the ends of the video bar to choose the part to keep. Playback stops at the end of the kept part. **Save trim** writes a new clip (`<name> (trimmed).mp4`) with that part and the volumes you set, and keeps the original. The new clip has the same tracks, so it can be trimmed again. Saving is near-instant when the trim starts at the beginning; otherwise the video is re-encoded on the GPU, which takes a few seconds per minute of clip.
- **Full screen.** The ⛶ button, double-clicking the video or **F**. The controls hide when the mouse rests. **Esc** goes back.

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | Back / forward 5 seconds |
| ↑ / ↓ | Volume up / down 5% (the mouse wheel over the volume slider does the same) |
| , / . | One frame back / forward |
| Home / End | Start / end of the clip |
| I / O | Set the trim start / end at the playhead |
| F | Full screen |
| M | Mute |
| Esc | Leave full screen, or close the player |

## Settings

| Setting | What it does | Default |
|---|---|---|
| Save folder | Where clips are written | `Videos\Clips` |
| Clip hotkey | The key combination that saves a clip. It works while a game has focus | Ctrl+Shift+F9 |
| Clip length | How far back a clip reaches: 15 seconds to 5 minutes | 30 seconds |
| Quality | Low, Medium, High or Ultra. Higher means larger files | High |
| Clip resolution | Native, or a smaller size converted while saving | Native |
| Frame rate | 60 or 30 fps | 60 fps |
| Mouse cursor | Show the cursor in clips | On |
| Desktop audio | Record game and PC sound | On |
| Separate app tracks | Capture each app on its own as well, so clips get one track per app. Shows which apps are playing sound right now | On |
| Microphone | Record your microphone, choose which one, and set its volume | On, Windows default device |
| Start with Windows | Start in the background when you sign in | Off |
| Hotkey sound | Plays the moment you press the hotkey. **Choose sound…** takes any audio file (its first 5 seconds); clip audio ends at the press, so the sound is not in the clip | On, built-in chime |
| On-screen notice | Shows that a clip is being saved, then whether it worked | On |

## How it stays reliable

- **The recorder is supervised.** If screen capture stops or stalls (display change, sleep, driver reset), it is restarted automatically, usually within two seconds.
- **Recording never pauses to save a clip.** A clip is assembled from finished one-second pieces of the buffer.
- **Disk use is bounded.** The buffer holds only the clip length plus 15 seconds, is erased on exit, and recording pauses while the drive is almost full (under about 1.5 GB free).
- **Nothing is left running.** If the app is closed or crashes, Windows ends the recorder with it.
- **Audio follows your devices.** Switching headset, speakers or microphone is picked up within a couple of seconds.
- **A clip is never half-written.** It is written under a temporary name and renamed when complete. The same goes for trimmed copies.
- **The separate audio tracks cannot lose a clip.** If writing them fails, the clip is saved with one mixed track, as before.
- **App tracks never drop a sound.** The mix in every clip is the full desktop capture, not the sum of the app tracks. When a clip is saved, the app tracks are checked against the desktop capture; if they do not account for it (an app Windows would not let Flashback capture, for example), the clip gets one desktop track instead and the log says why.
- **Hotkey conflicts are visible.** If another program holds the hotkey, Settings says so, and the app keeps trying to claim it.

The status bar at the bottom of the window shows whether recording and both audio sources are working. Details go to the log (Settings, **Open log folder**).

## Limits

- Only the main monitor is recorded.
- Hardware encoding needs an NVIDIA GPU. Without one the app falls back to CPU encoding at 30 fps.
- When Windows takes screen capture away (a display mode change, an exclusive-fullscreen switch, the lock screen or an administrator prompt), about two to three seconds around that moment are missing from the buffer.
- HDR is not handled; an HDR desktop is recorded as SDR.

## Where things are stored

| What | Where |
|---|---|
| Clips | `Videos\Clips` (changeable) |
| Settings | `%APPDATA%\Flashback\settings.json` |
| Your hotkey sound (converted to WAV) | `%APPDATA%\Flashback\hotkey-sound.wav` |
| Buffer, thumbnail cache, logs | `%LOCALAPPDATA%\Flashback` |

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This needs the .NET 10 SDK. It publishes a self-contained app to `dist` and puts FFmpeg next to it, downloading FFmpeg into `tools\ffmpeg` first if it is missing. Always build with this script: running `dotnet publish` twice into the same folder deletes the native DLLs the app needs.

The app is C# and WPF. FFmpeg captures the screen (`ddagrab`) and encodes it on the GPU (`h264_nvenc`) into one-second segments. Audio is captured in the app through WASAPI and laid out against the video by wall-clock time when a clip is saved. A clip's audio is AAC tracks: the mix (the default track, what other players and Discord use: desktop audio plus the microphone), then one track per app (or one desktop track), then the microphone.

Each app is captured with Windows' process loopback (Windows 10 2004 or newer), for every app with an audio session on the default output device, as listed in the volume mixer. Windows captures a process together with the processes it started, so an app that started another app playing sound (Steam starting a game, a game launcher) gets no track of its own instead of a copy of the game; its sound stays in the mix. Processes of one app (Discord, Chrome) become one track. Apps that play nothing are not kept in memory: their silence is only counted, so the sound after it still lands sample-exactly in place. Per-app capture reaches the app later and at a different level than the desktop capture (about 20–40 ms later, differing by a few milliseconds from app to app, and 4.5 dB louder on the development PC, where the speaker driver's effects only touch the desktop path), so when a clip is saved each app's delay is measured by cross-correlation and the level by a least-squares fit against the desktop capture, which is the track that is in sync with the video. The app tracks are then shifted and scaled to match, so at 100% they add up to the mix. Flashback's own sound (a clip playing in its player, the hotkey sound) is captured too, only so that check can account for it; it never gets a track. Windows' per-app capture can get stuck after the output device goes away (seen with unplugged speakers: each stuck capture kept a CPU core busy until restarted), so app captures pause while there is no output device and start afresh when one returns or the default device changes, and any app capture that uses more than 15% of a core is reopened (the log says so).

In the player the video goes through WPF's MediaElement with its sound muted, while the separate audio tracks are decoded by FFmpeg into memory and played through WASAPI with the chosen volumes, kept within a few milliseconds of the video's clock. Trimmed copies are made by FFmpeg from the clip file itself (the GPU encoder first, then the CPU encoder if that fails).

### Command-line switches

| Switch | Purpose |
|---|---|
| `--minimized` | Start in the tray without showing the window |
| `--quit` | Ask the copy that is already running to exit |
| `--data-dir <folder>` | Keep settings, buffer and logs in `<folder>` and skip the single-instance check (for testing) |
| `--selftest <seconds>` | Record for that long, save one clip, save a trimmed copy of it, write `selftest.json` to the data folder, exit |
| `--render-ui <folder>` | Save PNG images of the pages (including the player, muted) to `<folder>`, log the player's audio/video sync, a simulated drag of the playhead and a check of the volume controls, then exit. The window opens off screen and does not take focus |
