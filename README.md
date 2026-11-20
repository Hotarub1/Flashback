<img src="assets/icon.svg" width="64" height="64" alt="">

# Flashback

A small Windows app that keeps the last moments of your main monitor in a rolling buffer and saves them as an MP4 when you press a hotkey. It records desktop sound and your microphone.

Created in [T3 Code](https://t3.codes).

## Using it

1. Run `dist\Flashback.exe`. It starts recording straight away and keeps running in the tray when you close the window.
2. Press **Ctrl+Shift+F9** (or the **Save clip** button) to save the last 30 seconds to `Videos\Clips`.
3. **Clips** shows your newest clip at the top (**Latest**), uncropped and sized to the window, then the rest in a band per day with that day's number of clips. Days in the same week with only a few clips each share a band (two days, or three on a big window, when their clips fit in one row; weeks start on the day set in Windows), listed newest first, their clips shown together as in any band. On a big window Latest also shows twelve stills from across the clip; click one to play from that moment. Click a clip to play it; right-click to rename it, delete it, or show it in its folder. Clips that still have the name they were saved with are shown by time ("Today, 5:22 PM"). **Folder** in the header opens the clips folder.

To exit, right-click the tray icon and choose **Exit**.

## Watching and editing a clip

Clips carry each app's sound and your microphone as separate tracks (plus a mix of everything that other players use). Every app that played sound during the clip gets its own track, so Discord can be turned down while the game stays as it was. The player lets you work with them:

- **Volume.** The slider next to the speaker button sets how loud the whole clip plays, and is remembered for the next clip. It only changes what you hear, not what a saved trim contains. Mute with the speaker button or **M**; moving the volume unmutes.
- **Volume per track.** The sliders beside the timeline set how loud each app and the microphone are while you watch. They go up to 200% so a quiet microphone can be lifted. Sound that no app track caught (Windows' own sounds, Steam while it runs a game, the first moment of an app that just started) is on an **Other sounds** track, so the tracks together always sound like the clip. A clip where no app made sound has one **Desktop audio** track instead. Older clips show the tracks they were saved with.
- **Trim.** Drag the handles at the ends of the video bar (a strip of frames from the clip) to choose the part to keep. Playback stops at the end of the kept part. **Save trim** writes a new clip (`<name> (trimmed).mp4`) with that part and the volumes you set, and keeps the original. The new clip has the same tracks, so it can be trimmed again. Saving is near-instant when the trim starts at the beginning; otherwise the video is re-encoded on the GPU, which takes a few seconds per minute of clip.
- **Full screen.** The ⛶ button, double-clicking the video or **F**. The controls hide when the mouse rests. **Esc** goes back.
- **Back to the clips.** **Clips** in the header, or **Esc**.

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

Settings has four sections: Recording, Video, Audio and General. A big window (a maximized one on a 1080p or larger screen) shows all four at once; a smaller one shows one at a time, chosen on the left.

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

- **The recorder is supervised.** If screen capture stops (display change, sleep, driver reset), it is restarted automatically, usually within two seconds. If the main way of capturing (Windows Graphics Capture) fails to start three times in a row, the older one (Desktop Duplication) is used for 30 minutes.
- **Recording never pauses to save a clip.** A clip is assembled from one-second pieces of the buffer.
- **A still screen is still recorded.** Nothing new arrives from Windows while nothing on the screen changes, so the buffer holds the last picture for as long as it stayed up, with the sound going on as usual. A clip saved on a still screen runs right up to the press.
- **Disk use is bounded.** The buffer holds only the clip length plus 15 seconds, is erased on exit, and recording pauses while the drive is almost full (under about 1.5 GB free).
- **Nothing is left running.** If the app is closed or crashes, Windows ends the recorder with it.
- **Audio follows your devices.** Switching headset, speakers or microphone is picked up within a couple of seconds.
- **A clip is never half-written.** It is written under a temporary name and renamed when complete. The same goes for trimmed copies.
- **The separate audio tracks cannot lose a clip.** If writing them fails, the clip is saved with one mixed track, as before.
- **App tracks never drop a sound.** The mix in every clip is the full desktop capture, not the sum of the app tracks. When a clip is saved, the app tracks are checked against the desktop capture, and whatever they do not account for goes on the **Other sounds** track. The log lists the tracks of every clip and how well they matched.
- **Hotkey conflicts are visible.** If another program holds the hotkey, Settings says so, and the app keeps trying to claim it.

The bar at the bottom of the window shows whether recording is working and when the full clip length is ready; desktop audio and the microphone appear there only when something is wrong with them. The stamp at the bottom of the side rail (録, "record") is solid while recording and an outline otherwise. Details go to the log (Settings, General, **Open log folder**).

## Limits

- Only the main monitor is recorded.
- Hardware encoding needs an NVIDIA GPU. Without one the app falls back to CPU encoding at 30 fps.
- When screen capture has to restart (a display mode change, sleep, or with Desktop Duplication also an exclusive-fullscreen switch, the lock screen or an administrator prompt), a clip shows the last picture held for the one to three seconds that are missing, while the sound goes on.
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

The app is C# and WPF. Its typefaces are Chakra Petch, Zen Old Mincho (cut down to Latin and the few Japanese characters shown) and Azeret Mono, embedded in the app under the SIL Open Font License (`src\Flashback\Assets\Fonts\OFL-*.txt`). The header replaces the Windows title bar (WPF `WindowChrome`). FFmpeg captures the screen with Windows Graphics Capture (`gfxcapture`) and encodes it on the GPU (`h264_nvenc`) into one-second segments. Graphics Capture delivers every frame Windows draws (up to 250 a second) stamped with the moment it was drawn; each frame is moved to the start of its 1/60 s slot and only the first frame in each slot is kept, so the video shows exactly the picture of each moment. The older Desktop Duplication grabber (`ddagrab`, now only the fallback) times its frames with `Sleep`, which Windows rounds to 15.6 ms steps for FFmpeg: frames were taken 4 to 40 ms apart and the video juddered (about five skipped frames a second). Frames only arrive when the screen changes, so keyframes are placed by time, the encoder holds no frames back (`-delay 0`), and every frame is written to disk at once, so the piece still open on a still screen can be read when a clip is saved. Audio is captured in the app through WASAPI and laid out against the video by wall-clock time when a clip is saved. A clip's audio is AAC tracks: the mix (the default track, what other players and Discord use: desktop audio plus the microphone), then one track per app (or one desktop track), then the microphone.

Each app is captured with Windows' process loopback (Windows 10 2004 or newer), for every app with an audio session on the default output device, as listed in the volume mixer. Windows captures a process together with the processes it started, so an app that started another app playing sound (Steam starting a game, a game launcher) gets no track of its own instead of a copy of the game; its sound stays in the mix. Processes of one app (Discord, Chrome) become one track. Apps that play nothing are not kept in memory: their silence is only counted, so the sound after it still lands sample-exactly in place. Per-app capture reaches the app later and at a different level than the desktop capture (12–40 ms later and 4.5 dB louder on the development PC, where the speaker driver's effects only touch the desktop path). Windows also mixes each app capture on a thread of its own, and when that thread is held up (a busy PC, another capture opening) the capture loses some audio and runs later from then on (80 ms later after a 150 ms stall in a test). Every packet is kept with its QPC time, and a packet that does not follow straight on from the one before (lost audio, or silence left out) is placed by that time instead of right after it; within a stretch, packets are placed by QPC time relative to the first one, because the wall clock drifts against the audio clock (about 40 µs per second on the development PC). When a clip is saved, each app capture is lined up against the desktop capture (the track that is in sync with the video) piece by piece between the points where it lost audio: the delay by cross-correlation (on block differences at 2 kHz over −100 to +500 ms, then at full rate), each app's level by a least-squares fit. A piece without a clear delay is left out of its app track. The app tracks are then shifted and scaled to match, and whatever of the desktop capture they still leave out (where it is clearly louder than the faint echo that subtracting the apps always leaves) becomes the Other sounds track, so at 100% the tracks add up to the mix. An app whose sound is not in the desktop capture (one playing on another output device) gets no track. Flashback's own sound (a clip playing in its player, the hotkey sound) is captured too, only so it is accounted for; it never gets a track. Windows' per-app capture can get stuck after the output device goes away (seen with unplugged speakers: each stuck capture kept a CPU core busy until restarted), so app captures pause while there is no output device and start afresh when one returns or the default device changes, and any app capture that uses more than 15% of a core is reopened (the log says so).

In the player the video is drawn by the Media Foundation Media Engine (what the Windows Media Player app uses) straight into a child window, which times every frame to the screen; WPF's MediaElement passed frames through WPF's own renderer, which showed them at uneven moments (4 to 38 ms each instead of 16.7, some skipped). WPF cannot draw over that window, so in full screen the video leaves out the strip under the control bar while the bar shows. The separate audio tracks are decoded by FFmpeg into memory and played through WASAPI with the chosen volumes, kept within a few milliseconds of the video's clock; the video's own sound (the mix) plays only until they are ready. Trimmed copies are made by FFmpeg from the clip file itself (the GPU encoder first, then the CPU encoder if that fails).

### Command-line switches

| Switch | Purpose |
|---|---|
| `--minimized` | Start in the tray without showing the window |
| `--quit` | Ask the copy that is already running to exit |
| `--data-dir <folder>` | Keep settings, buffer and logs in `<folder>` and skip the single-instance check (for testing) |
| `--selftest <seconds>` | Record for that long, save one clip, save a trimmed copy of it, write `selftest.json` to the data folder, exit |
| `--render-ui <folder>` | Save PNG images of the pages (the clips, the player, muted and without the video picture, which is not drawn by WPF, each Settings section and the on-screen notice) to `<folder>`, check that the header has replaced the Windows title bar, check that only a full click opens a clip (not the release left over from double-clicking the title bar), log the player's audio/video sync, how well the seek bar lines up with the timeline, a simulated drag of the playhead and a check of the volume controls, then exit. The window opens off screen and does not take focus |
