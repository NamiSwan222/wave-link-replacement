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

- **Important fix:** WaveFix's audio engine didn't actually run on your mic, so effects, boost and the soundboard had no effect, and on some mics recording stopped working altogether. Windows creates audio effects in a way the engine didn't support; it does now. Update, and your effects reach the mic straight away (no need to run setup again).
- The Setup page now always stays in the sidebar, with **Copy details for help** and **Take the engine off this mic**.
- **Copy details for help** (Setup page): copies what WaveFix and Windows report about your mic and the audio engine, including the engine's own log, to paste when something doesn't work.
- Setup now waits up to 90 seconds for Windows audio to come back instead of failing after 20.
- **Fix:** if Equalizer APO was on your mic but a different mic was selected when WaveFix started, setup could add WaveFix's engine without removing Equalizer APO and stop with "Windows didn't accept the mic setting". Setup now always takes Equalizer APO off the mic it's setting up, and cleans up a mic left half-switched by 0.8.2.
- **Fix:** switching from Equalizer APO (or connecting the engine) could stop with "Windows didn't accept the mic setting" on some PCs, because setup gave up before the step that changes the mic had run. It now waits for that step to report back, and the setup log says exactly what went wrong if anything does.
- **No more Equalizer APO.** WaveFix runs everything on its own audio engine now. Set up with Equalizer APO before? After this update the Setup page offers **one click** that moves your mic, settings and plugins to WaveFix's engine, puts your mic's original effects back, and uninstalls Equalizer APO (unless another device still uses it). Until you click it, nothing changes.
- **Take the engine off this mic** on the Setup page puts your mic's original Windows settings back.

## First-time setup is automatic

Open WaveFix and click **Set it up for me**. After one Windows permission prompt it installs **WaveFix's audio engine**, adds the **[RNNoise](https://github.com/werman/noise-suppression-for-voice)** noise removal plugin, connects the effects to your mic (in the slot Discord can't skip), and restarts Windows audio. Every download is checked against a pinned fingerprint. Steps already done are skipped.

## Verify the download

Each installer has a matching `.sha256` file. WaveFix's updater refuses any download that doesn't match it.
