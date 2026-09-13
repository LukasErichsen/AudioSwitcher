# AudioSwitcher

A small Windows utility that switches between two fixed playback + microphone pairs. Both normal audio and calls follow the selected pair.

**[Download the latest release](../../releases/latest)** — no source, build tools, or .NET Framework compiler required.

- **AudioSwitcher.Config.exe** — configure profiles, apply a pair, and preview sounds.
- **AudioSwitcher.Toggle.exe** — switch once, optionally play confirmation, and exit without a window or focus changes.

## Setup

Open `AudioSwitcher.Config.exe`, name your two profiles, and choose a playback device and microphone for each. Save profiles, then launch the toggle executable from your preferred shortcut or launcher.

The configuration window uses a Windows 11 inspired light appearance, native window controls, keyboard-accessible fields, and a scrollable layout for smaller displays.

- **Refresh devices** discovers connected hardware without losing selections or unsaved edits.
- Disconnected saved devices remain selected and are labelled **Disconnected**.
- **Apply now** changes Windows defaults immediately but does not save profile edits. Cancel only discards unsaved edits; it does not undo an applied pair.
- **Play sound cues on switch** controls confirmation and error sounds. Preview buttons work even when automatic cues are off and play through the current Windows output.
- Identical pairs are flagged before saving.
- Save errors remain visible with your edits intact. If another setup window has saved changes, reopen setup before saving.

## Switching and repair

The toggle identifies the current playback and microphone console defaults together. Profile 1 switches to profile 2; profile 2 switches to profile 1. If neither pair matches, it selects profile 1. Each successful switch sets and verifies console, multimedia, and communications roles for both devices.

Device resolution uses:

1. The exact saved Windows endpoint ID.
2. An exact friendly-name match, ignoring case.
3. A cleaned name, removing only numbered Windows prefixes such as `2- Speakers` or `Speakers (2- USB Audio)`.

A name-based replacement must be unique. Ambiguous matches are not guessed: choose a specific device in setup. Model numbers and distinguishing words are preserved. Repairs update both current-profile detection and target devices, and are saved automatically. Automatic repairs merge with unchanged device selections in an open setup window.

Both target devices must be active before any default is changed. After a partial failure, the app attempts to restore every previous default role and verifies the restoration. Success is reported only after the full pair is confirmed. A failed switch returns exit code 1 and plays the error cue when enabled; overlapping or duplicate triggers are ignored and return 0.

Operations cannot overlap. A 550 ms guard also ignores accidental repeated triggers, and notification sounds cannot overlap one another. Devices can still disappear mid-operation; if restoration is impossible, the log and configuration interface explain that the defaults need attention.

## Sounds

AudioSwitcher uses its own short, soft struck-note sounds. They have a rounded glass/wood character inspired by familiar notification sounds, but do not reuse Windows, Messenger, or third-party recordings.

- **Profile 1:** one soft ding.
- **Profile 2:** two closely spaced dings: the second starts 180 ms after the first and is two semitones higher.
- **Error:** a lower, muted note in the same sound family.

Leading and trailing near-silence is removed before playback, so the source file does not add a pause. The two profile-2 notes overlap naturally as the first fades. Tiny fades soften the joins without inserting silence. The clips are attenuated and capped at 16% of full scale per note (20% for the overlapping pair); the error cue is capped louder, at 32%, so it stands out. The app never changes your Windows output volume.

Success cues address the selected output by its exact endpoint ID, with a short readiness retry, plus a short silence lead-in so a device that is still waking up drops silence rather than the start of the note. **Play on both the previous and new device** (on by default) also plays the success cue on the device you're switching away from, in case the new device hasn't finished waking up in time; turn it off in setup to only play on the new device. Error cues and previews use the current default output. Sound failures never undo a successful switch. If there is no usable output, the sound failure is logged.

The original WAV clips are embedded in both executables. There are no external sound files to install. `assets/make-sounds.py` reproduces them using Python's standard library and is not required for an ordinary build.

## Configuration and logs

Files are stored in `%LOCALAPPDATA%\AudioSwitcher`:

- `audio-switcher.config` — profiles, device IDs/names, sound preference, and save revision.
- `audio-switcher.config.bak` — the previous configuration, retained after an atomic replacement.
- `audio-switcher.log` — requested profiles, repair decisions, failures, verification and restoration results.
- `audio-switcher.log.retention` — last cleanup date.
- `last-trigger` — duplicate-trigger timestamp.

Existing configurations remain compatible. Sound cues default to enabled when the old configuration has no sound preference. Configuration saves use a flushed temporary file and atomic replacement, with a backup of the previous version.

Log maintenance runs at most once per UTC day. It retains the past 365 days at cleanup time and deletes complete entries, including multiline error details. Existing dot-separated timestamps and new millisecond timestamps are supported. Unknown timestamp formats are retained rather than discarded. Logs do not independently prove what a particular game or communication app is playing; applications with their own explicit device selection may keep using that device.

## Build and verification

Requires 64-bit Windows with .NET Framework 4.8 or newer and its framework compiler. Intended and visually styled for Windows 11. No NuGet packages or SDK downloads are required.

Every push to `main` builds and tests automatically; if it passes, a release is published with `AudioSwitcher.zip` attached. Failing tests block the release.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Package
```

The toggle has no WPF dependencies. The configuration app embeds its XAML interface. Tests use isolated configurations and a simulated audio backend, exercise matching, all default roles, rollback, save conflicts, retention, trigger exclusion, sound assets, and UI actions, and render normal/compact/disconnected layouts to `artifacts`.

A separate read-only native check enumerates real Windows audio devices and checks sound endpoint mapping:

```powershell
.\artifacts\AudioSwitcher.Tests.exe --native
```

Tests do not change your Windows audio defaults. Hardware unplugging, Bluetooth wake-up timing, and subjective sound quality should also be checked with the devices you use.




