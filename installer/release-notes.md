## Download

Download **`WaveFix-Setup-….exe`** under **Assets** below and run it. No admin needed; it installs just for you.

Already have WaveFix? It updates itself (you'll see "WaveFix updated" in the corner). Your mic keeps working while it updates.

## What it is

A Wave Link replacement whose mic effects run **inside Windows' own audio engine**, so your mic can't leak into Discord Go Live streams the way it does with Wave Link.

- **Microphone:** input level, mic boost, mute, live meter
- **Sound cleanup:** AI noise removal, rumble filter, voice tones
- **App mixer:** volume, mute and output device per app
- **Plugins:** add or scan for your own 64-bit VST2 effects, and open each one's own settings window (changes apply live)
- **Shortcuts:** global hotkeys: mute, push to talk, boost, and more

## First-time setup is automatic

Open WaveFix and click **Set it up for me**. After one Windows permission prompt it installs **[Equalizer APO](https://sourceforge.net/projects/equalizerapo/)**, adds the **[RNNoise](https://github.com/werman/noise-suppression-for-voice)** noise removal plugin, connects the effects to your mic (in the slot Discord can't skip), and restarts Windows audio. Every download is checked against a pinned fingerprint. Steps already done are skipped.

## Verify the download

Each installer has a matching `.sha256` file. WaveFix's updater refuses any download that doesn't match it.
