# Soundboard

A Windows desktop soundboard (WPF, .NET 10, NAudio). Every sound plays on two outputs at once:

- **You hear it on**: your headphones or speakers.
- **Virtual mic output**: a virtual audio cable that Discord, Zoom or Teams uses as its microphone.

Your real microphone can be mixed into the virtual mic as well, so people hear your voice and the sounds through one input.

```
sound files ─┬─► Monitor bus ─────────────────────► your headphones
             └─► Virtual-mic bus ─► CABLE Input ──► (CABLE Output) ─► Discord's input device
your mic ───────────────┘ (passthrough)
```

## Setup for Discord / Zoom

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (donationware). If it isn't installed, the app shows an
   **Install VB-Cable…** button that:
   - downloads `VBCABLE_Driver_Pack46.zip` from vb-audio.com into a new temp folder (the installer isn't bundled);
   - checks that the setup program has a valid Authenticode signature from VB-Audio's author (Vincent Burel), and asks
     before running it if the signer is different;
   - runs it as administrator. Accept the Windows prompt, click **Install Driver**, then close the VB-Cable window;
   - selects `CABLE Input` once the device appears, and deletes the temp folder.

   If the CABLE devices don't appear afterwards, restart Windows and click **Refresh devices**. If the download or
   check fails, the app offers to open the VB-Cable page so you can install it manually.
2. In Soundboard:
   - **Virtual mic output** → `CABLE Input (VB-Audio Virtual Cable)` (picked automatically if it's installed on first run)
   - **Your mic (passthrough)** → your real microphone
3. In Discord, open **Settings → Voice & Video → Input Device** and choose `CABLE Output (VB-Audio Virtual Cable)`.
   In Zoom, open **Settings → Audio → Microphone** and choose the same device.
4. Turn off Discord's noise suppression (Krisp) and set Input Sensitivity to manual, or it may cut out sound effects.

The two volume sliders are separate: one sets what you hear, the other what others hear. Mic passthrough is not affected by the slider.

## Using it

- Add sounds with **Add sounds…** or by dragging files onto the window (mp3, wav, aiff, wma, m4a).
- Click a pad to play it. Sounds can overlap. **Stop all** stops every sound but not your mic.
- Right-click a pad to:
  - **Rename…** it;
  - **Trim…** it: drag across the waveform to keep only part of it, preview it, and optionally rename it. Trimming
    never changes your own files: the trimmed version is saved as a new MP3 in `%APPDATA%\Soundboard\Clips` and the
    pad switches to it. For clips the app made itself, the old version is replaced. Files up to 10 minutes long can be
    trimmed;
  - **Remove** it.
- If you plug in a device while the app is open, click **Refresh devices**.
- The meter next to your mic shows its level. The dot is green while the mic is live and red while it's reconnecting. If
  the mic drops out (sleep, a USB disconnect), the app reopens it automatically and says so in the status bar.

Settings and the sound list are saved to `%APPDATA%\Soundboard\settings.json`. Sounds are referenced by path, not copied.

## Clip recorder (make sound clips from Discord or any app)

1. In **Record from**, pick the window whose app you want to record. Discord is picked by default, even when it's only
   in the tray. The list refreshes when you open it, or click **⟳**. Your choice is remembered.
2. In **Keep last**, choose how much audio to keep: 30 s (the default), 1 minute or 2 minutes. Longer buffers use more
   memory, about 23 MB a minute, and each option shows its size. The choice is remembered.
3. Turn on **Clip recorder**. While it's on, the chosen amount of that app's audio is kept in memory. Other apps
   and your own mic are not captured. A red dot and "● REC" in the title bar show it's running. To switch apps, turn
   the recorder off first. The same goes for changing the length.

   Windows captures audio per app, not per window: picking one browser window records every tab and window of that
   browser.
4. When something clip-worthy happens, click **✂ Clip last …** (the button shows the length).
5. Drag across the waveform to select the moment, and click **▶ Play selection** to check it. The preview plays only
   on your own output, so the call doesn't hear it.
6. Name it, then either:
   - **Add to soundboard**: saves an MP3 to `%APPDATA%\Soundboard\Clips` and adds it as a pad; or
   - **Save MP3…**: save it anywhere, for example to submit to the vote bot with `/sound submit`.

The editor tells you whether the selection fits the vote bot (4 s by default) and Discord's soundboard (5.2 s).

**Consent:** clips contain other people's voices. Tell people in the call when the recorder is on, and only clip
people who agree. Recording people without consent breaks Discord's rules and is illegal in many places.

Requirements: Windows 10 version 2004 or later. To record only Discord, use the Discord **desktop** app: a Discord
tab in a browser is recorded together with everything else that browser plays. Turning the recorder off throws away
the buffered audio.

## Portable download

The release zip (`Soundboard-<version>-win-x64.zip`) has one self-contained `Soundboard.exe` that runs without .NET
installed. Unzip it anywhere and run it. Settings and clips still live in `%APPDATA%\Soundboard`, so moving or
replacing the exe keeps them.

To build the zip:

```
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

It reads the version from `<Version>` in `src/Soundboard/Soundboard.csproj` and writes the zip to `artifacts\`.

## Install with a desktop shortcut

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

This builds a Release version into `%LOCALAPPDATA%\Programs\Soundboard` and creates a **Soundboard** shortcut on your
desktop. Run it again after code changes to update the install (close the app first). The icon is generated by
`tools/make-icon.ps1`.

## Build and run (development)

```
dotnet run --project src/Soundboard
```

## Code layout

| File | Role |
|---|---|
| `Audio/AudioEngine.cs` | Opens devices; plays each sound on the monitor and virtual-mic buses |
| `Audio/OutputBus.cs` | One always-running WASAPI output: a sounds sub-mixer (volume, Stop All) plus persistent inputs |
| `Audio/MicPassthrough.cs` | Captures the real mic into the virtual-mic bus; a watchdog reconnects it after drop-outs and caps drift delay |
| `Audio/SampleFormat.cs` | Converts any file or mic stream to the bus's sample rate and channel count |
| `Audio/ProcessLoopbackCapture.cs` | Captures one app's audio tree via Windows process loopback, with silence filled in |
| `Audio/AudioSource.cs` | Lists apps with open windows and finds each one's root process (Discord even from the tray) |
| `Audio/ClipRecorder.cs` | Captures the chosen app and keeps 30 s to 2 min in a `ReplayBuffer` |
| `Audio/ClipExport.cs` | Cuts a selection and encodes it to MP3 (Windows Media Foundation) |
| `ClipEditorWindow.xaml(.cs)`, `WaveformView.cs` | Waveform, drag to select, preview, save |
| `Setup/VbCableInstaller.cs` | Downloads, extracts and runs the VB-Cable installer |
| `Setup/Authenticode.cs` | Verifies the installer's signature with WinVerifyTrust |
| `Settings.cs` | JSON settings and sound list |
| `Log.cs` | Diagnostic log at `%APPDATA%\Soundboard\soundboard.log` |
| `MainWindow.xaml(.cs)` | UI |

## Known limits

- Passthrough adds roughly 50–100 ms of delay to your voice. That's fine for calls but noticeable if you monitor yourself.
- Choosing the same device for both outputs plays every sound twice on it.
- Clips are one mixed track of everything the chosen app plays. Speakers aren't separated.
- Not included yet: global hotkeys, per-sound volume, looping, ogg/flac files.
