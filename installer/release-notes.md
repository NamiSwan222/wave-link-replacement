## Download

Download **`WaveFix-Setup-….exe`** under **Assets** below and run it. No admin needed; it installs just for you.

Already have WaveFix? It updates itself (you'll see "WaveFix updated" in the corner). Your mic keeps working while it updates.

## What it is

A Wave Link replacement whose mic effects run **inside Windows' own audio engine**, so your mic can't leak into Discord Go Live streams the way it does with Wave Link.

- **Microphone:** input level, mic boost, mute, live meter
- **Sound cleanup:** AI noise removal, rumble filter, voice tones, and a Custom tone with five EQ sliders
- **Voice polish:** leveler (compressor), de-esser, background silencer (expander) and peak protection (limiter)
- **Voice changer:** Deep, Giant, Chipmunk, Robot, Alien, Phone call, Megaphone, Cave, Underwater, Ghost, or your own mix (pitch, robot, wobble, grit, echo, room). Like every other effect, it runs inside Windows' audio engine, so it never leaks into Discord streams
- **App mixer:** volume, mute and output device per app
- **Plugins:** add or scan for your own 64-bit VST2 effects, and open each one's own settings window (changes apply live)
- **Soundboard:** add sound files and play them into your mic with a click or a key. They're mixed in inside Windows' audio engine, so they don't leak into Discord streams either
- **Shortcuts:** global hotkeys: mute, push to talk, boost, voice changer on/off, next voice, a key for each sound, stop all sounds, and more. Numpad keys work on their own (no Ctrl needed)

## New in this version

- **WaveFix's own audio engine.** New installs no longer need Equalizer APO: WaveFix installs its own Windows audio effect, adds itself after any effects your mic already has, and only loads code from its admin-only folder. Already set up with Equalizer APO? Nothing changes for you. **Take the engine off this mic** on the Setup page puts your mic's original settings back.
- **Soundboard:** long sounds (up to 15 minutes) now work, files that can't be added say why, and you can drag files or folders onto the page.

## First-time setup is automatic

Open WaveFix and click **Set it up for me**. After one Windows permission prompt it installs **WaveFix's audio engine** (setups from before 0.8 use **[Equalizer APO](https://sourceforge.net/projects/equalizerapo/)**), adds the **[RNNoise](https://github.com/werman/noise-suppression-for-voice)** noise removal plugin, connects the effects to your mic (in the slot Discord can't skip), and restarts Windows audio. Every download is checked against a pinned fingerprint. Steps already done are skipped.

## Verify the download

Each installer has a matching `.sha256` file. WaveFix's updater refuses any download that doesn't match it.
