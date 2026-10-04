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
