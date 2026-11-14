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

## Features (planned)

- Mic: gain, mute, live level meter
- Effects: AI noise removal and noise gate
  ([RNNoise VST](https://github.com/werman/noise-suppression-for-voice)), EQ presets
- Per-app mixer: volume, mute and output device for each app
- Tray icon, starts with Windows

## Setup (current state)

1. Install Equalizer APO 1.4.2. In Device Selector, tick only your physical mic on the
   **Capture devices** tab.
2. Put `rnnoise_mono.dll` from the RNNoise release in
   `C:\Program Files\EqualizerAPO\VSTPlugins\rnnoise\`.
3. Restart Windows Audio (or reboot).

## Tools

- `tools/leak-test.ps1 [-ProcessName X] [-Seconds N] [-Wav out.wav] [-Exclude]`: record one
  app's audio the way Discord Go Live does and print the levels.
