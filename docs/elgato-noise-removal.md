# What's inside Elgato Noise Removal

Notes from taking apart `ElgatoNoiseRemoval_v1.1.2.119.vst3` (Wave Link's noise removal plugin),
to see how it compares with what WaveFix uses. Done 2026-11-14 with static analysis only (Ghidra
through REA). The plugin was never run.

## Short answer

Elgato Noise Removal is **RNNoise** (Xiph.org's open-source neural noise remover) inside a JUCE
plugin, with a voice gate and an Elgato-styled window on top. It is the same engine WaveFix already
loads: `rnnoise_mono.dll` from werman's
[noise-suppression-for-voice](https://github.com/werman/noise-suppression-for-voice). There is no
larger proprietary AI model inside.

## The file

| | |
|---|---|
| Format | PE32+ DLL for **ARM64** (Windows on ARM). This copy doesn't load on x64 PCs. |
| Version | 1.1.2.119 |
| SHA-256 | `cdcd4ef4a9c26abdeefe120440d7cee5d94432803d53e6c3a5de33d037262a7a` |
| Size | 5,236,376 bytes |
| Signed by | Corsair Memory, Inc. (DigiCert EV code signing). Certificate read, signature not verified. |
| Exports | `GetPluginFactory`, `InitDll`, `ExitDll` (standard VST3) |

## What code it contains

| Layer | What it is | Evidence |
|---|---|---|
| Plugin framework | **JUCE**, with OpenGL drawing and JUCE's VST3 wrapper | Hundreds of `juce::` RTTI class names; 342 `OPENGL32.dll` imports |
| Host interface | **Steinberg VST3 SDK** | RTTI for `Steinberg::Vst::EditController`, `IAudioProcessor`, `IPluginFactory3`, … |
| Noise removal | **RNNoise** (GRU neural network, 48 kHz, 480-sample frames) | RNNoise lookup tables and functions, below |
| Wrapper around RNNoise | `RnNoiseCommonPlugin` and `DenoiseState` classes, the same names as in werman's noise-suppression-for-voice (inferred from names, not compared code-for-code) | RTTI strings |
| Elgato's own code | `Elgato_rnn_denoiserAudioProcessor` / `…AudioEditor`, `RNNDenoiserLookAndFeel`, `AdvancedSettingsPopup`, `BlurryError`, `RLottieComponent`, SVG buttons | RTTI strings |
| Bundled assets | Elgato logo and icon SVGs, Lottie animations, the Univers font | Embedded files |

Other imports are the usual Windows UI, GDI, OLE drag-and-drop, IME and MSVC runtime DLLs. No
network or telemetry APIs (WinHTTP, WinINet, sockets) are imported.

## Controls

| Parameter | Shown as | What it does |
|---|---|---|
| `bypass` | Bypass | Turns the effect off |
| `VAD Threshold` | NOISE GATE → Threshold | RNNoise gives each frame a voice probability; audio is muted when it falls below this. 100% treats everything as noise. |
| `VAD Release` | NOISE GATE → Release | How long to wait after the probability drops before muting, so the ends of words aren't cut off |

The noise removal itself has no strength setting. These match werman's RNNoise plugin controls
(VAD threshold and grace period), which WaveFix already exposes.

It writes a log, `ElgatoRNNDenoiser.log`, under an `Elgato/VSTs` folder ("Initialized/Closing Elgato
RNNDenoiser audio processor").

## Decompiled proof

- **Band table at `0x180455a00`.** RNNoise's `eband5ms` table `{0,1,2,3,4,5,6,7,8,10,12,14,16,20,24,28,34,40,48,60,78,100}`
  stored as 16-bit integers. RNNoise's `tansig_table` follows directly at `0x180455a40`, in the same
  layout as the upstream source.
- **`FUN_180187308` = `tansig_approx`.** It clamps to ±8, looks up `x*25 + 0.5` in that table and
  applies the same correction, `y + x*dy*(1 - y*x)` with `dy = 1 - y²`.
- **`FUN_1801854e0` = `pitch_filter`.** It works on 481 frequency bins (`FREQ_SIZE` for 480-sample
  frames), uses the same per-band pitch-gain formula (`+0.001`, `+1e-08`), interpolates over the band
  table and scales the spectrum by the result.

## Not established

- **Model weights.** Not checked whether they are RNNoise's stock model or retrained, or which
  RNNoise version (2017 v0.1 or 2024 v0.2) they come from.
- **Wrapper code.** That it is werman's wrapper is inferred from the class names only.
- **Wrapper licence.** RNNoise is BSD-3-Clause, but the wrapper's licence wasn't checked.
- **x64 build.** Only the ARM64 build was examined. The x64 build is probably the same code but
  wasn't checked.

## What this means for WaveFix

WaveFix already runs the same denoiser (werman's RNNoise v1.10, `rnnoise_mono.dll`) with the same VAD
threshold and release controls. Matching Wave Link's noise removal takes nothing from Elgato's
binary. The differences are the look of the window and, possibly, the model weights.
