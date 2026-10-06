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

## v0.3.0 — experimental Wardogs sound matching

The new checkbox enables a closest-pattern readout using the reviewed Wardogs
samples. Categories: movement, gunfire, reload, explosion, ground vehicle,
air vehicle, ambience. This is an experimental baseline, not reliable sound
recognition: it may guess incorrectly, especially in mixed gameplay. No
confidence percentage is shown because it has not been calibrated.
Own and nearby movement are combined. Horns and chambering are excluded from
the model because there is only one independently labeled recording each.

The matcher needs a 48 kHz stereo capture input. If you see a format message,
you can set your virtual playback device to a 48 kHz stereo format in Windows
Sound settings. Spectrum and playback still work at other supported formats.
An absent or incompatible model disables matching without disabling playback.
Keep the Models folder beside GamerSense.exe when copying the built app.

The readout uses a half-second sample window, updated about four times per
second, on a background timer. Capture callbacks only copy/decode into a fixed
ring and never wait for its lock. Contention resets the matching window.
Very quiet audio (below -60 dBFS RMS) shows no match; that is a simple quiet
gate, not a validated unknown-sound detector. Normal-volume unknown sounds
can still receive an incorrect known-category match.

The live feature recipe (box3-spectrum-v1) was retrained and evaluated with
whole-recording holdouts. It differs from the initial offline model and is
bundled in src/GamerSense/Models/wardogs-model.json. Metrics are included in
validation/evaluation.json; macro F1 remains about 41.7% on selected weakly
labeled windows. This is not live gameplay accuracy or a calibrated confidence.

Validated Debug and Release compilation, original analyzer format checks,
Python/C# parity across all 47 features on five actual clip windows, matching
prediction parity, immutable capture bytes, disable, silence, stale display,
missing model, and unsupported rate. Real-device playback needs user testing.

Build with BUILD-DEBUG.bat. Launch the new GamerSense.exe, start your usual
routing, and compare the closest-pattern readout with what you hear. Try the
matching checkbox on/off and confirm clean audio, the spectrum, and remembered
device selections. For repeatable feedback, note the actual sound, displayed
category, and whether multiple sounds overlapped.

## v0.3.1 — ambience fallback and stricter reload matching

Weak or ambiguous matches now display "Ambience / mixed audio" rather than
forcing a known category. Reload needs a larger margin over the second-best
category and a closer distance to its learned pattern. Other categories also
use rejection gates. This is a fallback display state, not a verified ambience
classification and not a new training label. Strong matches retain their name.
Silence still shows "Quiet audio — no match". Playback is unchanged.

On the prior 1,422 held-out windows, the pilot rules reduced false reload
matches from 122 to 8, while genuine reload matches fell from 63 to 10. The
tradeoff is fewer false reports but more missed reloads. 800 windows were
rejected overall. Rules are heuristic and evaluated retrospectively on the
same development corpus, not calibrated confidence or independent live-game
validation. New unlabeled sounds can still match a known pattern incorrectly.

The new c1/c2/c3 clips are registered as provisional mixed samples, not trained:
- c1: nearby mortar impacts/explosions plus own-character running.
- c2: player building/repairing in-game items plus background explosions.
- c3: helicopter rocket fire plus ground impacts/explosions (no heli landing).
Precise event timing/review is still needed. Building/repairing has no learned
class yet and may appear as ambience/mixed audio.

Release build: 0 warnings / 0 errors. Regression checks passed, including
Python/C# feature and gated-prediction parity; explicit gate tests verify clear
reload acceptance and ambiguity/distance fallback. Real-device testing remains
necessary. Keep the updated Models folder with the app; the old ungated model
is rejected as incompatible rather than used with forced guesses.

## v0.3.2 — move analysis decoding off the playback callback

Playback still receives the captured bytes first and uses the stable 30 ms
output-latency / 40 ms prebuffer / 200 ms buffer-capacity settings. The callback
now only makes a pooled copy for observers. A bounded three-packet queue is
consumed on a lower-priority background thread; sample decoding no longer runs
on the capture callback. When the observer falls behind, only its packets are
dropped. Packets older than 100 ms are discarded, and gaps reset the analysis
windows. This does not discard, resample, filter, or change playback audio.

The new Queued audio readout reports BufferedWaveProvider backlog in ms. It is
not total capture-to-ear latency and excludes device and WASAPI output delay.
Some delay from the original playback buffers remains; this change does not
prove that user-observed lag is fixed. Real-device comparison is required.

To diagnose: compare sound matching on/off; note whether lag is constant or
increases over time, and the queued-audio reading at start and after a few
minutes. Growing backlog suggests a playback/buffering issue rather than the
half-second classification window, which only delays the visual readout.

The ambience/mixed fallback and stricter reload rules from v0.3.1 are included.
New queue tests verify immutable copies, bounded backlog, no waiting on a busy
observer, stale-packet discard, discontinuity reset, and disposal. Original
format, feature-parity, matching and rejection checks also passed. Debug and
Release compilation passed with no warnings/errors.

## v0.3.3 — selectable playback timing and route diagnostics

Stable playback remains the default and retains the original capture constructor
and buffer settings. Lower latency (test), selectable only while stopped,
requests a 20 ms polling capture buffer, 20 ms output buffer, 20 ms prebuffer,
and 80 ms playback buffer capacity. Stable uses the original 100 ms capture
request, 30 ms output request, 40 ms prebuffer, and 200 ms capacity. Requests
are not guarantees of hardware or end-to-end latency. Mode selection is saved
alongside device IDs; existing settings migrate with Stable as the default.

The lower-latency capture subclass uses the same shared-mode loopback and
conversion flags with a shorter requested buffer. No EQ/filtering is applied.
If this mode crackles, Stop and choose Stable playback before restarting.

Capture batch shows the duration of the latest delivered packet, not its age.
Queued audio shows only the playback-provider backlog. Copy audio details
copies requested timings, observed queue/batch values, device names/formats,
and matcher state to the clipboard for troubleshooting. It is only copied
when you press the button, with no automatic upload.

A reported 40–70 ms provider queue cannot alone explain 500–600 ms of perceived
delay. The virtual-device route, output-device buffering, sample-rate conversion,
or a different latency measurement may also contribute. None is yet verified.
Compare audible gunfire with the game action; the pattern readout itself uses
a half-second analysis window and is intentionally later than immediate audio.

Capture default behavior was checked against the pinned NAudio 2.2.1 source:
https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/WasapiCapture.cs
https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/WasapiLoopbackCapture.cs

Validation: stable timing preservation, shorter responsive timing, compatible
legacy saved device IDs, accurately labeled timing report, and all observer,
feature parity, classifier fallback, format and disposal regressions passed.
Debug/Release compilation passed with 0 warnings/errors. Actual WASAPI hardware
startup/latency and listening quality have not been tested in this workspace.

## v0.3.4 — keep session readings after Stop

Copy audio details now includes a retained playback-session summary. It records
capture packet count, capture-batch min/average/max, and playback-queue
min/average/max after delivery. These values survive Stop, along with the
actual session device names/formats and timing profile. Top-level Running now
and instantaneous zero values describe the current state; the retained summary
describes the last session. Starting another session replaces the history.

The metrics tap uses a nonblocking try-lock, with report formatting outside the
lock. This is a diagnostic update. Stable buffer values, passthrough bytes,
device-selection memory, and the conservative matcher are unchanged. No claim
is made that this version fixes the reported half-second delay.

Test with Stable playback for 20–30 seconds. Stop if convenient, press Copy
audio details, and paste the entire report into chat. Compare matching on/off
in separate runs if needed. The retained queue readings are after capture
batches are added, so they can be higher than UI readings between deliveries.
These metrics still do not measure complete capture-to-ear latency.

A useful independent comparison is to temporarily route Wardogs straight to
Speakers (C6), bypassing GamerSense, then restore CABLE Input and compare the
same event. If both paths have similar delay, source/output timing needs
investigation; if only the app path does, the added route needs investigation.
Do not infer the cause from a stopped zero queue or classification display.

The virtual driver also has its own buffers; its official reference describes
latency statistics. No virtual-driver setting is changed by GamerSense:
https://vb-audio.com/Cable/VBCABLE_ReferenceManual.pdf

Validation: all existing checks plus retained summary statistics, invalid/stopped
sample rejection, and fresh-session reset passed. Debug and Release builds
passed with 0 warnings/errors. Physical route latency remains unverified.

## v0.4.0 — live event monitoring and review

Playback has a new Event review tab. Its independent observer keeps a 60-second
native-format rolling recording in memory (about 23 MB at 48 kHz stereo float),
plus a peak-level timeline and up to 200 markers. It detects loud-threshold
crossings (default -18 dBFS, editable) and sudden RMS rises (10 dB above a moving
baseline), with a two-second automatic cooldown. Steady loud audio does not
continuously emit loud markers. These are activity suggestions, not sound-type
recognition, distance estimation, or identification of direct combat.

Manual marking: use Mark moment, F8 when the app is focused, or Ctrl+Alt+F8
while the app is running. The global shortcut uses Windows RegisterHotKey;
conflicts are reported and the button/local shortcut remain available. Gameplay
hotkey behavior has not been validated on the user's setup.
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey

Click a timeline marker or list item to freeze a clip (default 2 seconds before,
3 after). A very recent event may not yet have its requested post-event audio:
wait, then press Load / refresh event clip. Editing the range zooms its waveform;
the spectrum samples the end of that range. Stop live playback before replay:
review goes straight to the selected true output, so it does not feed back into
live capture. Starting playback stops any review replay.

Choose a sound label and a preference: Keep, Reduce, or Unsure. Save WAV + label
writes the selected audio range and a JSON sidecar with relative/session times,
source/session identity, trigger kind, intent, notes, and manual review status.
The label covers the whole saved range and may include overlapping sounds.
Preferences are annotations for future suppression development; they do not
change audible playback or automatically become trusted classifier training.

Memory retention: old audio rolls out after 60 seconds and its markers show
expired. A frozen selected clip remains reviewable until replaced. Stop retains
the current rolling recording; starting a new session replaces it. Save clips
before closing the app. No continuous disk recording or automatic upload occurs.
When the observer queue drops packets, gaps are marked internally and extracts
crossing a gap are rejected. Playback continues independently. Session times
follow observed capture frames, not video or wall-clock time; observer gaps are
not assigned an invented duration.

Offline: while stopped, Open audio sample can load WAV, MP3, and MP4 via the
installed Windows decoders. Only the latest 60 seconds of a longer sample stay
available. WAV is preferred; decoder failures appear in the review status.
Offline samples do not alter game audio or train the model. Show live session
switches back to the retained/current capture recording.

Stable playback, saved device IDs, conservative pattern matching, the bounded
analysis queue, and retained timing diagnostics are preserved. The reported
latency issue is still open; no latency fix is claimed in this release.

Validation: Debug/Release compile with 0 warnings/errors. Regression checks plus
spike/manual marking, sustained loud-event behavior, immutable source bytes,
context extraction, native WAV/JSON export including Reduce intent, dropped-gap
rejection, ring expiry and frozen-clip retention passed. Physical replay/live
capture, UI layout, and the in-game shortcut need user testing.

An offline vehicle demo is included in Samples/demo-vehicles.wav (your supplied
VehiclesNonGunner audio). Open audio sample starts in that folder. Automated
checks decoded the demo, detected activity markers, checked capture duration,
and extracted context successfully. This does not verify the demo's sound-type
labels; those remain manual review tasks.

## v0.4.1 — GamerSense app icon

Added a teal headset/waveform icon with amber accents, including Windows icon
sizes from 16 to 256 px. The ICO is embedded as the executable ApplicationIcon
and as the main form's icon. It travels with builds and requires no separate
icon-file lookup at runtime. Debug and Release builds passed with 0 warnings /
0 errors. Audio and event-review behavior are unchanged from v0.4.0.

## v0.4.3 — transparent black, teal, and gold app icon

Revised the icon to a transparent background with primarily black headset and
waveform surfaces, highlighted in teal, with gold listening arcs. Seven Windows
icon sizes are bundled. App/window icon integration is preserved. Debug and
Release builds passed with 0 warnings/errors. Audio behavior is unchanged.

## v0.4.4 — revised reduced-delay test

Reduced delay (test) now requests 50 ms capture instead of 100 ms Stable or
20 ms in the former test. Output remains 30 ms and startup prebuffer remains
40 ms, matching the clean Stable settings. Capacity is 150 ms (a ceiling,
not a target delay). Stable playback remains the default and is unchanged.
This is a controlled timing test, not a confirmed fix for the reported
half-second delay. Actual hardware playback latency requires user testing.

## v0.4.5 — Windows stream timing diagnostics

Copy audio details now includes the actual initialized capture/output buffer
capacities, driver-reported stream latencies, and default/minimum device periods.
These readings are retained after Stop. They are partial, overlapping indicators,
not an end-to-end measurement. Zero reported latency is not proof of zero delay.
The diagnostics borrow NAudio 2.2.1 private clients using reflection; failure
reports unavailable without changing playback. Reads happen once at startup,
not on the capture callback. Both playback profiles are unchanged from v0.4.4.
Hardware testing is still needed to locate the reported half-second delay.

## v0.4.6 — event-driven capture test

The test mode now reads loopback audio on Windows audio events instead of
polling halfway through each capture buffer. Windows 10 build 15063 or newer
supports this. Older systems use polling compatibility. Copy audio details
retains the capture scheduling method. Requested buffer capacities remain
50/30/150 ms and startup prebuffer stays 40 ms. Stable is unchanged.
No sound processing is introduced. Delay and crackling must be checked on the
user's devices; this is not a confirmed end-to-end latency fix.
Reference: https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording

## v0.4.7 — minimum-delay test and playback supply diagnostics

Three playback modes are available. Existing selections migrate to the same
Stable or Event-driven mode. Minimum delay (test) requests event-driven capture
20 ms, shared event output 10 ms, startup prebuffer 20 ms, and capacity 80 ms.
Actual Windows allocations can differ: Copy audio details retains them.
The confirmed-clean Event-driven mode retains its 50/30/150/40 ms settings;
Stable retains 100/30/200/40 ms. Device and mode selections are remembered.

Playback supply diagnostics count short reads and missing audio duration before
zero filling, with no waits or audio processing. Source pauses also count, so
these are not a definitive audible glitch count. The former zero-fill playback
contract is preserved and checked against BufferedWaveProvider.ReadFully=true
for float32, PCM16, and PCM24 audio, partial and empty reads, and nonzero offsets.

Minimum delay is an opt-in hardware test. It cannot promise zero latency or
infer total audible delay from capacities. If it crackles, return to the clean
Event-driven mode. Spectrum, matching, event review and icon are retained.
Windows buffer sizing reference:
https://learn.microsoft.com/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize

## v0.4.8 — lean shared output test

Lean output (test) adds a custom shared-mode event renderer that queues one
10 ms/default device period instead of filling the allocated endpoint capacity.
It requests 30 ms output capacity as headroom, but uses CurrentPadding to refill
only to the target. Capacity and occupied queue depth are separate quantities.
Capture retains the clean event-driven 50 ms allocation and starts playback
after 10 ms of captured audio. Source arrivals also wake the render thread.
The renderer uses above-normal thread priority and attempts Pro Audio MMCSS
registration, reverting it when stopping. It never modifies sample amplitudes.

Copy audio details retains queued-audio target, sampled pre-write output padding,
write count, MMCSS status, and missing-source readings. Padding readings cover
writes only, not every event; none are an end-to-end latency measurement.
Existing Stable, Event-driven, and Minimum delay modes keep their settings and
NAudio output implementation. Existing saved mode/device choices are retained.
Lean mode is opt-in, has not been auditioned on C6 hardware, and may underrun.
Return to Event-driven capture if it crackles or drops out.

Validation: byte-preserving playback adapter tests; exhaustive capacity/target/
padding scheduling sweep; saved-mode compatibility; analysis and event-review
checks. Windows render-device behavior still requires the user's listening test.
References:
https://learn.microsoft.com/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getcurrentpadding
https://learn.microsoft.com/windows/win32/api/avrt/nf-avrt-avsetmmthreadcharacteristicsw

## v0.4.9 — low-period shared output test

Low-period output (test) asks IAudioClient3 for its supported shared engine
period range and initializes at the shortest aligned period below the default.
This uses the native matching float32 mono/stereo output mix format: no sample
conversion or processing. Driver support is queried; the older 3 ms device
minimum is not treated as proof of low-period shared support.

If the interface, format, period, or initialization is unsupported, the app
reports that and uses the Lean renderer on a fresh client. It keeps a 10 ms
startup reserve, including fallback. Existing
Lean output retains its 10 ms reserve; every previous mode is unchanged.

The report includes the driver-supported shared period range, selected/current
period when available, actual endpoint capacity, target queue, and silence fill.
The generic 30 ms output request is for fallback allocation only in this mode;
IAudioClient3 chooses capacity from the period. It is not a total latency reading.
COM interop keeps all base slots in native order. Unsupported settings never
reinitialize an already initialized client. Native format buffers are freed.

Validation: period alignment/bounds, mismatched-format rejection, COM declaration
order, saved-mode migration, render scheduling, byte-preserving zero fill,
analysis and event-review checks. Actual C6 driver behavior needs a user test.
No claim of zero latency or clean low-period playback is made before that test.
Reference:
https://learn.microsoft.com/windows/win32/api/audioclient/nf-audioclient-iaudioclient3-initializesharedaudiostream


When low-period initialization is active, the renderer queues available captured
frames only. A partial source batch is submitted as a partial render packet;
source arrivals wake the renderer to refill before the next device event when
possible. Refill attempts waiting for capture are reported separately; ordinary
silence-fill counters do not cover those waits. Source pauses also cause waits.
This avoids committing premature silence between differently sized capture and
render batches. The original Lean mode and fallback retain their former behavior.

## v0.4.10 — direct refill test

C6 reports a fixed 10 ms shared period, so low-period initialization cannot
shorten it. Direct refill (test) uses the existing shared Lean renderer and
10 ms startup reserve, but queues available captured frames only, regardless
of IAudioClient3 support. It avoids precommitting silence on a source shortfall
that can defer newly arriving real audio. Source arrivals trigger immediate
refill where there is free target space. Samples remain in order and unmodified;
no stale-audio discard, DSP, or extra buffering is introduced.

The new report labels the fill policy and counts refill attempts waiting for
captured data. Windows may still output silence if its queue empties; these
counts are not an audible glitch measurement. Supply meter silence-fill counts
do not include these waits. All old modes, including Lean and low-period
fallback behavior, remain selectable and unchanged.

Validation simulates source gaps and smaller output packets, ensuring no future
silence commitment and byte-identical ordered samples. Previous checks cover
scheduling bounds, mode migration, analysis, and event review. C6 listening
quality and total latency still require testing. This is not a zero-delay claim.

## v0.4.11 — direct cable capture comparison

An optional seventh playback mode, Direct cable capture (test), reads from a
recording endpoint with event-driven shared-mode WASAPI. Choose CABLE Output
as the game audio device and Speakers (C6) as True output. Leave the game/test
player sending to CABLE Input, and disable Windows Listen to this device.

This replaces playback loopback capture only for this mode. Its playback uses
the v0.4.10 Direct refill policy and the same 50 ms requested capture capacity,
30 ms output capacity, 10 ms prebuffer, and 80 ms app buffer capacity. Capture
uses the selected recording device's native mix format. No samples are filtered
or gain-adjusted. All six previous modes remain available. Recording-endpoint
selection is saved separately from the existing playback-endpoint selection.

This is an experiment, not a verified latency improvement. Repeat the supplied
flash/click test, record it with the same phone position, and copy the new audio
details while playback is active or after Stop. If audio becomes noisy or stalls,
return to Direct refill with CABLE Input; the previous input selection is retained.

## v0.4.12 — persistent spike review

Live spikes, level rises and manual F8 / Ctrl+Alt+F8 marks are automatically
frozen with two seconds before and three seconds after each event, then saved
as native-format WAV plus JSON under LocalAppData/GamerSense/SavedEvents.
At Stop, pending events are saved with the available tail (shown as short tail).
Clips crossing known analysis gaps are rejected and reported, rather than
presented as continuous audio. Offline sample markers are not automatically saved.

The Event review tab opens the persistent Pending list, including clips from
earlier sessions and app restarts. Select a clip, stop live playback to replay,
adjust its review range, choose sound label and Keep/Reduce/Unsure intent,
add notes, and Save tags or Approve clip. Approval persists the review range
and tags without destroying the original clip. Approved and All saved clips
filters are available. Delete clip removes its library WAV and JSON after a
confirmation. Export WAV + label exports the current range; pending saved
clips are exported with verified=false. Approval does not alter playback or
automatically train a model.

Autosave can be disabled and the choice is remembered. Saved audio has no
automatic expiry. A 2 GB audio storage budget pauses new saves when reached;
existing clips are retained. Delete unwanted clips to make room. The folder
can be opened from the app. At 48 kHz stereo float, each five-second clip is
about 1.9 MB, so busy sessions can consume substantial space.

Completed clips are copied on the bounded analysis worker, then passed to a
separate below-normal disk worker with a bounded 32-clip queue and nonblocking
enqueue. Slow/full disks do not block the playback callback. Skipped clips,
pending writes and storage errors are displayed. Normal app close drains
queued saves; forced termination can lose in-flight clips. WAVs are committed
before atomic JSON replacement. The rolling live timeline remains 60 seconds,
but saved clips are independent of it. Previous captures cannot be recovered.

The live timeline/sample tools are collapsible. Recording-endpoint capture,
direct-refill output, buffer timings, selection memory, icon, model matching,
and spectrum remain as in v0.4.11. BUILD-DEBUG.bat is unchanged.

Validation includes byte-identical retained native WAV data, full post-event
context, ring expiry and restart, on-disk approval/notes/range persistence,
discard, stopped-session tails, gap rejection, disabled autosave, storage limits,
bounded nonblocking enqueue under simulated stalled storage, and the existing
playback/analysis checks. The review UI was rendered and tag/approval filtering
was exercised. Gameplay quality with autosaving still needs user testing.

## v0.4.13 — one-hour retention with 2 GB stop

This replaces v0.4.12's indefinite clip retention. All library clips, Pending
and Approved, expire one hour after CreatedUtc. Tagging and approval do not
extend that deadline. Export useful clips before expiry to keep a copy.
Existing library clips use their original save time and are subject to this
rule when the updated app starts.

The separate storage worker checks expiry at startup, about every 30 seconds
while idle, and before saving. If the app was closed past the deadline, clips
are removed on next startup. Both WAV and JSON are removed. Exported copies
are not deleted. Expired selected clips are cleared from the review UI.

At the 2 GB audio budget, new saves stop with a warning; unexpired clips are
not evicted to fit new ones. Saving resumes when expiry or manual deletion
frees enough space. Capture/playback timing and device memory are unchanged.

Clock-controlled tests verify no deletion at 59:59, deletion at 60:00 including
approved clips, both files removed, cached reads rejected, restart cleanup,
and resuming saves after expiry releases space without exceeding the budget.
The existing playback, analysis and saved-review checks still pass.

## v0.4.14 — approve/export or delete

The saved review queue now has two outcomes: Approve & export and Delete clip.
Save tags, Save loaded clip for later, manual Export WAV + label, and the
Keep/Reduce selector were removed from this flow. Choose the sound tag, add
notes and optionally trim the range, then approve. Approval means useful
training sample; exported playbackPreference is unspecified.

The default destination is Documents/GamerSense/ApprovedClips. A folder picker
remembers a different destination in settings; a button opens the export folder.
The exporter stages the WAV and JSON, commits both, then deletes the source WAV
and JSON from the temporary library. Export errors preserve the source. Unique
stable filenames avoid overwriting earlier exports and allow an identical export
to be reused on retry. Existing legacy-approved library items appear in the queue
until exported or deleted. No model training or audible signal changes are made.

Temporary clips retain one-hour expiry and the 2 GB stop. Exported approved clips
are outside the temporary library and survive cleanup. The exports directory has
no automatic size cap. Export destinations inside the temporary library are
rejected so approvals cannot accidentally enter the expiry path.

Checks verify byte-identical trimmed float32 WAVs, sound labels/notes and useful-
sample meaning, source preservation after failed export, source removal only after
the exported pair exists, non-overwriting names, no staging leftovers and export
survival past temporary expiry. The review UI was rendered and its approval path
exercised. Previous playback settings, capture modes and BUILD-DEBUG.bat remain.
