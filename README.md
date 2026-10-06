# GamerSense

GamerSense is a Windows game-audio processing project designed to route game audio through a low-latency processing engine before sending it to the user's real headset or speakers.

## v0.1 development goal

`Game -> Virtual Audio Device -> GamerSense -> Headset / Speakers`

The first milestone focuses on stable low-latency audio routing. Later milestones will add DSP and real-time sound classification for categories such as footsteps, gunfire, reloads, vehicles, explosions, music, and environmental ambience.

## Requirements

- Windows 10/11 x64
- .NET 9 SDK for development
- VB-CABLE during the prototype stage

## Build

Clone the repository and run `BUILD-DEBUG.bat`, or run:

```powershell
dotnet restore src/GamerSense/GamerSense.csproj
dotnet build src/GamerSense/GamerSense.csproj -c Debug
```

The debug executable will be under `src/GamerSense/bin/Debug/net9.0-windows/`.

## Roadmap

- v0.1: virtual-device-to-physical-device audio passthrough
- v0.2: DSP, EQ, limiter, meters, profiles
- v0.3: real-time game-sound classification
- v0.4: category-aware enhancement and suppression
- v0.5: experimental AI source separation
- Production: dedicated GamerSense virtual audio driver

## v0.2.0 — non-destructive live analysis

Playback retains the original captured bytes, WASAPI shared-mode output, 30 ms
output latency, 200 ms buffer, and 40 ms prebuffer. Saved input/output IDs still
use the existing LocalAppData/GamerSense/settings.json file.

The new analyzer displays a 64-column logarithmic spectrum from 20 Hz to
20 kHz (or Nyquist), peak/RMS dBFS, and the strongest FFT-bin frequency.
It uses a 2048-sample Hann-window FFT, approximately 23.4 Hz resolution at
48 kHz, and refreshes at 20 Hz. Analysis uses a bounded sample ring;
FFT work runs on a background timer. The capture tap never waits for the
analyzer lock and may skip analysis samples when busy. Playback is unchanged.
Supported analysis formats: float32 (including extensible float), PCM16,
PCM24, and PCM32. Other formats continue playback with analysis unavailable.
The display clears after 300 ms without new captured audio.

This is a visualization foundation, not a sound classifier or filter. No EQ,
gain, suppression, or enhancement is applied. For multichannel visualization,
the greatest-magnitude channel sample is retained per frame to avoid phase
cancellation. This is an activity view, not a calibrated per-channel spectrum;
the dominant readout has FFT-bin resolution and omits DC.

## Validation and testing

Validated with .NET SDK 9.0.318 on Windows: Debug build, 0 warnings / 0 errors.
Automated checks cover 1 kHz tone frequency, expected peak and RMS levels,
opposite-phase stereo, unchanged capture bytes, stale-signal clearing, silence,
and unsupported formats across the supported sample representations.
Run the included checks with:

```powershell
dotnet run --project tests/AnalyzerChecks/AnalyzerChecks.csproj
```

On your system:
1. Extract the ZIP and run BUILD-DEBUG.bat (.NET 9 SDK required).
2. Run src/GamerSense/bin/Debug/net9.0-windows/GamerSense.exe.
3. Select the working virtual game device and your physical output; start audio.
4. Confirm clean playback while the spectrum and level readouts update.
5. Pause the source and confirm the display clears; stop/start several times.
6. Close and reopen the app and confirm both dropdown selections are remembered.

Physical WASAPI routing, listening quality, and persisted device selections
require testing with your devices; these were not exercised in this workspace.
The ZIP contains source only; old bin/obj build artifacts are excluded.
