<img src="assets/icon.svg" width="64" height="64" alt="">

# Flashback

A small Windows app that keeps the last moments of your main monitor in a rolling buffer and saves them as an MP4 when you press a hotkey. It records desktop sound and your microphone.

Created in [T3 Code](https://t3.codes).

## Using it

1. Run `dist\Flashback.exe`. It starts recording straight away and keeps running in the tray when you close the window.
2. Press **Ctrl+Shift+F9** (or the **Save clip** button) to save the last 30 seconds to `Videos\Clips`.
3. The **Clips** tab shows your clips. Click one to play it; right-click to rename it, delete it, or show it in its folder.

To exit, right-click the tray icon and choose **Exit**.

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
| Microphone | Record your microphone, choose which one, and set its volume | On, Windows default device |
| Start with Windows | Start in the background when you sign in | Off |
| Sound and on-screen notice | Confirmation when a clip is saved | On |

## How it stays reliable

- **The recorder is supervised.** If screen capture stops or stalls (display change, sleep, driver reset), it is restarted automatically, usually within two seconds.
- **Recording never pauses to save a clip.** A clip is assembled from finished one-second pieces of the buffer.
- **Disk use is bounded.** The buffer holds only the clip length plus 15 seconds, is erased on exit, and recording pauses while the drive is almost full (under about 1.5 GB free).
- **Nothing is left running.** If the app is closed or crashes, Windows ends the recorder with it.
- **Audio follows your devices.** Switching headset, speakers or microphone is picked up within a couple of seconds.
- **A clip is never half-written.** It is written under a temporary name and renamed when complete.
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
| Buffer, thumbnail cache, logs | `%LOCALAPPDATA%\Flashback` |

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This needs the .NET 10 SDK. It publishes a self-contained app to `dist` and puts FFmpeg next to it, downloading FFmpeg into `tools\ffmpeg` first if it is missing. Always build with this script: running `dotnet publish` twice into the same folder deletes the native DLLs the app needs.

The app is C# and WPF. FFmpeg captures the screen (`ddagrab`) and encodes it on the GPU (`h264_nvenc`) into one-second segments. Audio is captured in the app through WASAPI and laid out against the video by wall-clock time when a clip is saved.

### Command-line switches

| Switch | Purpose |
|---|---|
| `--minimized` | Start in the tray without showing the window |
| `--quit` | Ask the copy that is already running to exit |
| `--data-dir <folder>` | Keep settings, buffer and logs in `<folder>` and skip the single-instance check (for testing) |
| `--selftest <seconds>` | Record for that long, save one clip, write `selftest.json` to the data folder, exit |
| `--render-ui <folder>` | Save PNG images of the pages to `<folder>`, then exit |
