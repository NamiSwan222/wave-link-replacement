## Download

Download **`WaveFix-Setup-….exe`** under **Assets** below and run it. No admin needed; it installs just for you.

Already have WaveFix? It updates itself (you'll see "WaveFix updated" in the corner). Your mic keeps working while it updates.

## What it is

A Wave Link replacement whose mic effects run **inside Windows' own audio engine**, so your mic can't leak into Discord Go Live streams the way it does with Wave Link.

- **Microphone:** input level, mic boost, mute, live meter
- **Sound cleanup:** AI noise removal, rumble filter, voice tones, and a Custom tone with five EQ sliders
- **Voice polish:** leveler (compressor), de-esser, background silencer (expander) and peak protection (limiter)
- **Voice changer:** 28 voices (from Giant, Monster and Little kid to Auto-tune, Synth singer, Walkie-talkie and Canyon) or your own mix of pitch, formant, harmonics, octave layer, auto-tune, vocoder, ring mod, distortion, bitcrusher, modulation, radio, echo and reverb. Like every other effect, it runs inside Windows' audio engine, so it never leaks into Discord streams
- **AI voices:** RVC voice models (add a `.pth` or find one online), running on your own PC
- **App mixer:** volume, mute and output device per app, with each app's own icon
- **Plugins:** add or scan for your own 64-bit VST2 effects, and open each one's own settings window (changes apply live)
- **Soundboard:** add sound files and play them into your mic with a click or a key. They're mixed in inside Windows' audio engine, so they don't leak into Discord streams either
- **Shortcuts:** global hotkeys: mute, push to talk, boost, voice changer on/off, next voice, a key for each sound, stop all sounds, and more. Numpad keys work on their own (no Ctrl needed)

## New in this version

After updating, click **Set it up for me** once (one Windows permission prompt): the new voice changer and noise removal live in the audio engine.

- **A much bigger voice changer.** A new voice engine changes your pitch while keeping your voice's character, so +5 sounds like a higher voice instead of a sped-up recording, and a new **Formant** control makes your voice bigger or smaller without changing the note. Also new:
  - **Gender** (pitch and formant together), **Harmonics** (darker or brighter), **Octave layer** (a second voice below or above), **Detune** (metallic, alien) and **Breath** (whispery)
  - **Auto-tune** that snaps you to the nearest note, and a **Vocoder** that plays your words as a synth chord
  - **Distortion types** (overdrive, fuzz, megaphone), **Bitcrusher**, **Modulation** (chorus, flanger, phaser), **Radio** (old phone, walkie-talkie with squelch, police radio, AM radio) and **Echo styles** (slapback, canyon, rhythm)
  - **28 voices** in groups, including Monster, Little kid, Whisper, Android, Synth singer, Walkie-talkie, Retro console, Jet and Canyon. Deep, Giant and Chipmunk now sound like bigger or smaller people instead of slowed-down or sped-up tape.
- **AI voices, much better:**
  - **No more babbling in pauses:** WaveFix learns your mic's background noise and keeps pauses silent.
  - **Shorter delay:** picked automatically for your PC (about 0.2 s on a good graphics card) and adjusted as you go.
  - **Find voices online:** search RVC voices on Hugging Face and add one with a click, including its index file.
  - **Advanced settings:** silence threshold, loudness, the voice's index file and strength, and speaker (for models with several voices).
  - **Voice pictures:** add a picture to any voice.
- **Hear myself:** hear your mic in your headphones exactly as others hear it, effects and AI voice included (Microphone page).
- **Set up my mic for me:** stay quiet for a moment, read a sentence, and WaveFix sets your input level, boost, noise removal, rumble filter, leveler and tone. Nothing changes until you click Apply.
- **Find sounds online** for the soundboard: openly licensed sounds (Freesound, Wikimedia and more), with a preview and the creator's credit kept.
- **Plugin search is remembered:** **Find plugins on this PC** shows the last search instantly; **Scan again** looks again.
- **New look:** a calmer plum-dark theme with Inter and IBM Plex Serif fonts, a reorganized sidebar with grouped pages, a quick mute control and live level in the sidebar, and **Setup & help** at the bottom (Start with Windows and Auto-update moved there).
- **App mixer** shows each app's own icon.
