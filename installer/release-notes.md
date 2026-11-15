## Download

Download **`WaveFix-Setup-….exe`** under **Assets** below and run it. No admin needed; it installs just for you.

Already have WaveFix? It updates itself (you'll see "WaveFix updated" in the corner). Your mic keeps working while it updates.

## What it is

A Wave Link replacement whose mic effects run **inside Windows' own audio engine**, so your mic can't leak into Discord Go Live streams the way it does with Wave Link.

- **Microphone:** input level, mic boost, mute, live meter
- **Sound cleanup:** AI noise removal, rumble filter, voice tones, and a Custom tone with five EQ sliders
- **Voice polish:** leveler (compressor), de-esser, background silencer (expander) and peak protection (limiter)
- **Voice changer:** 13 voices (Deep, Giant, Beast, Squeaky, Bot 3000, Android, Martian, Walkie, Megaphone, Canyon, Cathedral, Phantom, Duo) or your own mix of pitch, robot, chorus, drive, radio, echo, reverb and volume. Like every other effect, it runs inside Windows' audio engine, so it never leaks into Discord streams
- **AI voices:** RVC voice models (add a `.pth` or find one online), running on your own PC
- **App mixer:** volume, mute and output device per app, with each app's own icon
- **Plugins:** add or scan for your own 64-bit VST2 effects, and open each one's own settings window (changes apply live)
- **Soundboard:** add sound files and play them into your mic with a click or a key. They're mixed in inside Windows' audio engine, so they don't leak into Discord streams either
- **Shortcuts:** global hotkeys: mute, push to talk, boost, voice changer on/off, next voice, a key for each sound, stop all sounds, and more. Numpad keys work on their own (no Ctrl needed)

## New in this version

- **New voice changer.** The voice changer now runs on the Voice Shift engine: a fast pitch shifter, robot, chorus, drive, radio, echo and a big reverb, with 13 new voices from Deep and Giant to Bot 3000, Walkie, Cathedral and Phantom. The Custom sliders control those effects. Your voice changer setting carries over; a voice that no longer exists becomes Deep. **Needs the latest audio engine:** click **Update the audio engine** on the Microphone page once (Windows asks for permission).

## Earlier

- **Set the AI voice delay yourself.** AI voices → Advanced → **Set the delay myself**: block size, context, crossfade and a safety margin. **Try the lowest delay** picks the shortest settings that held up in testing (about 0.12 s in all on a graphics card). If your PC can't keep up, WaveFix adds 10 ms at a time on its own.
- **Much shorter automatic delay.** On a graphics card the AI voice now picks about 0.2 s instead of up to 1.2 s. WaveFix was measuring the graphics card while it was still warming up, and then trying other sizes that ran several times slower until a restart; it now reloads cleanly for each size and judges by the typical conversion.
- **10 ms less delay for every AI voice:** part of the blend between blocks was reserved but never used.
- **New look:** Astryx's "Gothic" style: near-black slate backgrounds, light buttons and switches with dark text, the Fustat font, and a dark title bar.
