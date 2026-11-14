# WaveFix: a Wave Link replacement that doesn't leak your mic

## The problem

With Elgato Wave Link 3 open, anyone watching your **Discord Go Live** stream hears your
microphone in the stream audio, on top of your normal voice chat.

Cause: Wave Link adds its mic effects inside its own app, then plays the processed mic into a
virtual playback device (`Elgato Wave:3 (Elgato Virtual Audio)`). Discord's stream audio uses
Windows *process loopback*, which records **every app's playback except Discord's own**. Wave Link
is "an app playing audio", so your mic ends up in the stream.

`tools/leak-test.ps1` reproduces this by recording an app's audio the same way Discord does.

Any replacement that processes the mic inside a normal app and plays it to a virtual cable
(Voicemeeter and similar) has the same leak.

## The fix

WaveFix never plays your mic itself. Its effects run **inside the Windows audio engine**
(`audiodg.exe`), as an Audio Processing Object (APO) on the physical mic: **WaveFix's own engine**
(`native/wavefix-engine`, Rust). Discord's process loopback can't record the audio engine, so
there's nothing to leak. Discord, OBS and others use the physical mic (`Mic In (Elgato Wave:3)`)
directly, with effects already applied. The WaveFix app is the control panel: it writes the
engine's config and talks to the Windows audio APIs.

### How the engine works

- `Program Files\WaveFix\Engine\wavefix-engine-<hash>.dll` is registered as a Windows audio effect
  (COM class `{1F0E0A0C-41EE-4E1A-B2C4-0681D5344BDD}`, registered like a driver's APO) and added **last**
  to the mic's endpoint effects (the composite list, `{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15`), so any
  effects the mic already had keep running. Endpoint effects apply to every stream, raw mode included.
  The original values are saved under `HKLM\SOFTWARE\WaveFix\Backup`; **Take the engine off this mic**
  on the Setup page puts them back.
- The app writes the chain as JSON to `ProgramData\WaveFix\Engine\config\<mic>.json`; the engine
  reloads it within a fraction of a second and reports to `...\status\<mic>.json`.
- The engine only loads code from `Program Files\WaveFix` (admin-only): RNNoise and your own plugins
  are copied there with one Windows permission prompt. No other program can slip code into Windows
  audio through WaveFix. An engine update asks for permission once, too.
- `cargo test` in `native/wavefix-engine` loads the built DLL the way Windows' audio service does
  (class factory, Initialize, format checks, LockForProcess, APOProcess) and checks the audio.
- Written from Microsoft's documented APO interfaces; Equalizer APO (GPL) was only a reference.

### Coming from Equalizer APO

WaveFix 0.8.0 and older ran the effects through [Equalizer APO](https://sourceforge.net/projects/equalizerapo/).
After updating, the Setup page offers one run that switches over: it installs the engine, moves
RNNoise and your plugins into `Program Files\WaveFix\Plugins`, puts the mic's original effects back
from Equalizer APO's own record, adds WaveFix's engine, and uninstalls Equalizer APO (unless another
device still uses it). Your settings carry over.

## Features

- **Mic:** pick the mic, input level, mic boost (up to +24 dB, applied in the audio engine), mute, live level meter
- **Effects:** AI noise removal and gate
  ([RNNoise VST](https://github.com/werman/noise-suppression-for-voice)), 80 Hz low cut,
  voice tone presets (Flat, Warm, Clear, Broadcast, Radio) and a Custom 5-band EQ
- **Voice polish:** leveler (compressor), de-esser, background silencer (downward expander) and
  peak protection (limiter, the very last effect, after mic boost)
- **Voice changer:** 10 voices (Deep, Giant, Chipmunk, Robot, Alien, Phone call, Megaphone, Cave,
  Underwater, Ghost) or a Custom mix of pitch, robot, wobble, grit, echo and room. Pitch shifting
  adds about 40 ms of delay
- **Soundboard:** MP3/WAV/M4A/WMA/AAC/FLAC sounds (up to 60 s) mixed into the mic inside the
  audio engine (after the voice changer, before peak protection), with a key per sound, Stop all,
  a volume for others and optional "hear it myself". The app streams the mix to the plugin through
  a memory-mapped file (`C:\ProgramData\WaveFix\Engine\bridge.bin`, LOCAL SERVICE gets write
  access); it never plays the sound into the mic from an app, so nothing leaks into Discord streams
- **WaveFix Voice FX:** voice polish, the voice changer, the soundboard mixer and peak protection
  (`native/wavefix-fx`, Rust), built into the engine. It's also a VST2 plugin, so other hosts can load it
- **App mixer:** volume, mute and output device for each app playing sound
- **Plugins:** add your own 64-bit VST2 effects to the mic chain, or **scan** the usual VST
  folders (and any folder you pick) for them; turn on/off, reorder, remove. They're copied to
  `C:\Program Files\WaveFix\Plugins` (one Windows permission prompt), the only place the engine
  loads code from
- **Plugin settings:** a Settings button on every plugin (and Adjust on noise removal) opens the
  plugin's own window, or sliders for plugins without one. Changes apply live: WaveFix hosts the
  VST2 in a separate process, reads its state (`effGetChunk`, or parameters) and sends it to the
  engine (a base64 chunk, or parameter name → value). Reset
  returns a plugin to its defaults
- **Shortcuts:** system-wide hotkeys (work in games): mute toggle, push to talk, noise removal
  on/off, mic boost up/down, next voice tone, voice changer on/off, next voice, stop all sounds,
  a key per soundboard sound, open WaveFix, with
  an on-screen confirmation. F-keys and numpad keys can be used on their own
- Friendly dark UI (palette and type from the UI/UX Pro Max "podcast / music streaming"
  profile, [Plus Jakarta Sans](https://github.com/tokotype/PlusJakartaSans) under the OFL),
  keyboard focus rings, screen-reader names, per-monitor DPI
- Tray icon (left click opens, right click for mute and quit), optional start with Windows
- Warns when Wave Link is running or when the effects aren't attached to the mic correctly

## Install

Download `WaveFix-Setup-<version>.exe` from the
[latest release](https://github.com/NamiSwan222/wave-link-replacement/releases/latest) and run it.
It installs per-user (no admin) to `%LocalAppData%\Programs\WaveFix` and **updates itself**:
it checks GitHub releases at startup and every 6 hours, verifies the installer against the
release's `.sha256`, installs silently and restarts in the tray. Turn off **Auto-update** in the
sidebar to get an "Update now" button instead.

### Releasing (maintainers)

Bump nothing by hand: push a tag and GitHub Actions builds the self-contained app, the
Inno Setup installer and its checksum, and publishes the release.

```bash
git tag v0.3.1 && git push origin v0.3.1
```

## Setup

Open WaveFix and click **Set it up for me** (Setup tab, or the button on the status banner). After
one UAC prompt, an admin helper (`WaveFix.exe --setup`):

1. installs WaveFix's engine into `Program Files\WaveFix\Engine` and registers it with Windows;
2. extracts `rnnoise_mono.dll` from the RNNoise v1.10 release into `Program Files\WaveFix\Plugins`
   (the download is pinned by SHA-256 and kept in an admin-only folder until verified);
3. saves the mic's effect settings, then briefly runs `WaveFix.exe --system-attach` as SYSTEM (the
   owner of those settings) through a temporary scheduled task to add the engine to the mic;
4. restarts Windows Audio.

Done steps are skipped. To build it yourself: `dotnet run -c Release --project src\WaveFix` (needs
the .NET 8 SDK and [Rust](https://rustup.rs) for the engine). Close Wave Link.

In Discord, OBS and other apps, select the physical mic (e.g. `Mic In (Elgato Wave:3)`),
not any `Elgato Virtual Audio` device.

## Why the endpoint slot

Windows has three effect slots on a capture device. The stream slot (SFX) is skipped for apps
that open the mic in raw mode, and Discord's Standard audio subsystem does. The endpoint slot
(EFX) is applied to every stream, raw included, so that's where WaveFix's engine goes.
`tools/mic-level.ps1 -Raw` shows the difference.

## Known limits

- Per-app output switching uses the same undocumented Windows API as EarTrumpet. Some apps
  only switch after they restart their audio.

## Tools

- `tools/leak-test.ps1 [-ProcessName X] [-Seconds N] [-Wav out.wav] [-Exclude]`: record one
  app's audio the way Discord Go Live does and print the levels. WaveFix measures digital
  silence; Wave Link carries your voice.
- `tools/mic-level.ps1 [-Raw] [-Comms] [-LowLatency]`: record the mic the way different apps
  open it and print the levels, to check which modes get the effects.

