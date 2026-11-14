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

WaveFix never plays your mic itself. Effects run **inside the Windows audio engine**
(`audiodg.exe`) as an Audio Processing Object on the physical mic endpoint, via
[Equalizer APO](https://sourceforge.net/projects/equalizerapo/). Discord's process loopback
can't record the audio engine, so there's nothing to leak. Discord, OBS and others use the
physical mic (`Mic In (Elgato Wave:3)`) directly, with effects already applied.

WaveFix itself is a control panel that writes Equalizer APO config and talks to the Windows
audio APIs.

## Features

- **Mic:** pick the mic, input level, mic boost (up to +24 dB, applied in the audio engine), mute, live level meter
- **Effects:** AI noise removal and gate
  ([RNNoise VST](https://github.com/werman/noise-suppression-for-voice)), 80 Hz low cut,
  voice tone presets (Flat, Warm, Clear, Broadcast, Radio)
- **App mixer:** volume, mute and output device for each app playing sound
- **Plugins:** add your own 64-bit VST2 effects to the mic chain, or **scan** the usual VST
  folders (and any folder you pick) for them; turn on/off, reorder, remove. They're copied to
  `C:\ProgramData\WaveFix\Plugins` so the audio engine (LOCAL SERVICE) can load them, and they
  run on their default settings
- **Shortcuts:** system-wide hotkeys (work in games): mute toggle, push to talk, noise removal
  on/off, mic boost up/down, next voice tone, open WaveFix, with an on-screen confirmation
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

**Automatic:** open WaveFix and click **Set it up for me** (Setup tab, or the button on the
status banner). After one UAC prompt, an admin helper (`WaveFix.exe --setup`) installs
Equalizer APO 1.4.2 silently, extracts `rnnoise_mono.dll` from the RNNoise v1.10 release,
saves Equalizer APO's child-APO backup for the mic, briefly runs `WaveFix.exe --system-attach`
as SYSTEM through a temporary scheduled task to put Equalizer APO in the mic's endpoint slot,
and restarts Windows Audio. Downloads are pinned by SHA-256 and kept in an admin-only folder
until verified. Done steps are skipped.

**Manual** (what the automatic setup does):

1. Install [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) 1.4.2. In Device
   Selector, tick only your physical mic on the **Capture devices** tab.
2. Put `rnnoise_mono.dll` from the RNNoise release's `vst` folder in
   `C:\Program Files\EqualizerAPO\VSTPlugins\rnnoise\`.
3. Move Equalizer APO to the mic's **endpoint** slot so raw-mode apps (Discord's default audio
   subsystem) get the effects too. From an admin PowerShell:
   `tools\run-as-system.ps1` (pass `-Undo` to revert). Edit the endpoint GUID in
   `tools\set-mic-effect-slot.ps1` if your mic isn't the one it targets.
4. Restart Windows Audio (or reboot).
5. Install WaveFix (above), or build it yourself: `dotnet run -c Release --project src\WaveFix`
   (needs the .NET 8 SDK). Close Wave Link.

In Discord, OBS and other apps, select the physical mic (e.g. `Mic In (Elgato Wave:3)`),
not any `Elgato Virtual Audio` device.

## Why the endpoint slot

Windows has three effect slots on a capture device. Equalizer APO's Device Selector uses the
stream slot (SFX), which Windows skips for apps that open the mic in raw mode, and Discord's
Standard audio subsystem does. The endpoint slot (EFX) is applied to every stream, raw
included. `tools/mic-level.ps1 -Raw` shows the difference.

## Known limits

- Noise removal runs on the plugin's default settings; Equalizer APO ignored every
  parameter syntax tried, so gate strength isn't adjustable yet.
- No compressor yet.
- Per-app output switching uses the same undocumented Windows API as EarTrumpet. Some apps
  only switch after they restart their audio.

## Tools

- `tools/leak-test.ps1 [-ProcessName X] [-Seconds N] [-Wav out.wav] [-Exclude]`: record one
  app's audio the way Discord Go Live does and print the levels. WaveFix measures digital
  silence; Wave Link carries your voice.
- `tools/mic-level.ps1 [-Raw] [-Comms] [-LowLatency]`: record the mic the way different apps
  open it and print the levels, to check which modes get the effects.
- `tools/run-as-system.ps1 [-Undo]` + `tools/set-mic-effect-slot.ps1`: move Equalizer APO to
  the mic's endpoint slot (see above).
