# FRLG Starter Tool beep timing: Windows test results

Written 2026-09-23. This file reports a run of the steps in `docs/handoff/beep-timing-windows-testing.md` on branch `audio-clock` (commit `7572e91`, code change `fb0b677`). The raw data, screenshots and tools are in `docs/handoff/windows-results/`.

## Setup

- Windows 11 Home 26200, QPC 10 MHz.
- `dotnet build` clean (0 warnings), `dotnet test` 11 passed, `dotnet publish -p:PublishProfile=win-x64` produced the single-file exe.
- Device under test: the default output, "Speakers (Yeti Classic)", a USB headphone output. The mix format is float32, 44.1 kHz, 8 channels, and the engine period is fixed at 10 ms (441 frames is the minimum, default and maximum). That matches main's 10 ms case. The default device was left as it was.
- Other apps stayed open (Discord, Slack, Waterfox). One unrelated sound showed up in one GUI recording. None showed up in the self-test runs, where 0 of 500 beeps went undetected.
- Second instrument: the `BeepProbe` harness from the FlowTimer investigation of 2026-09-22, now with a `selftest` mode (source in `windows-results/tools/BeepProbe/`). It listens to the same endpoint through loopback and re-times every beep in the self-test CSVs. It maps the capture's device position to QPC with a least-squares fit against packet arrival times instead of using the packets' own QPC stamps. On 2026-09-22 that method read a clock-exact beep to ±0.05 ms. Below, "fitted" means this clock.

## Self-test results (steps 2 to 4)

Final beeps, 25 presses per mode. Lateness is in ms against the target the tool computed. Each instrument has its own constant, so compare spreads, not means.

| Run | Mode | Self-test spread | Self-test mean | Fitted spread | Fitted sd | Hit rate (self-test / fitted) |
|---|---|---|---|---|---|---|
| 1, default period (10 ms) | Legacy | 9.80 | 61.26 | 8.98 | 2.84 | 85.2% / 85.8% |
| 1, default period (10 ms) | DeviceClock | 0.88 | 25.64 | **0.06** | 0.01 | 99.2% / 99.9% |
| 2, `AudioPeriodMs` = 10 | Legacy | 8.98 | 61.28 | 8.98 | 2.97 | 84.6% / 84.4% |
| 2, `AudioPeriodMs` = 10 | DeviceClock | **1.66** | 25.50 | 0.85 | 0.17 | 99.5% / 99.7% |

All 125 beeps per mode:

| Run | Mode | Self-test spread | Fitted spread |
|---|---|---|---|
| 1 | Legacy | 9.83 | 8.99 |
| 1 | DeviceClock | 1.71 | 0.88 |
| 2 | Legacy | 9.82 | 9.00 |
| 2 | DeviceClock | 1.69 | 0.87 |

What this shows:

1. **DeviceClock works.** Spread drops from about 9 ms to under 1 ms on the fitted clock. The estimated hit rate goes from about 85% to 99.5% or better.
2. **The DeviceClock constant is stable across stream opens.** The fitted mean was 15.54 ms in run 1 and 15.56 ms in run 2, in two processes. A tuned offset should survive app restarts on the same device.
3. **The means differ from the handoff table, but only by a device constant.** Legacy reads about 61 ms and DeviceClock about +25.5 ms on the self-test scale. The guide predicted 20 to 30 ms and "possibly negative". The gap between the two modes is 35.7 ms, which fits 2 queued periods plus the device's position lag. On this Yeti the loopback timestamps run about 25 ms behind the render stream's `IAudioClock`. The offset absorbs it either way.
4. **Run 2 tripped the step 6 rule.** The self-test's final-beep spread was 1.66 ms, over the 1 ms threshold. Two separate effects cause this, both from the same driver behaviour:
   - About 10% of DeviceClock beeps are placed 0.85 ms early. In the fitted data, 12 of 125 beeps per run sit at 14.8 ms instead of 15.6 ms. Run 2 also logged one `audio: beep started 0.567 ms late`.
   - The self-test's loopback onsets carry their own 0.85 ms error in both modes. Self-test minus fitted is 9.88 or 10.72 ms, never in between. So the self-test reports up to about 1.7 ms of DeviceClock spread when the placement spread is 0.06 to 0.88 ms.

## Step 6: why some beeps are 0.85 ms early

A probe opened a render stream the same way `WasapiOutput` does (`InitializeSharedAudioStream` at 441 frames, fed 2 periods ahead) and logged `IAudioClock::GetPosition` over 6001 engine passes. It logged the first call in each pass, as `FillScheduled` makes it, plus polls every 0.5 ms for the rest of every 10th pass.

- **Cause:** on 500 of 6001 passes (8.3%), the first `GetPosition` call after the event wake returns the position from the pass boundary paired with the current QPC. That reading is 0.73 to 0.79 ms stale (median 0.75). It happens on exactly every 12th pass, every 120 ms. `FillScheduled` then thinks playback is 0.75 ms behind and places the next beep that many frames early. When the shift crosses a pass boundary, the beep is placed late instead, which explains the one "started late" log line.
- **The guide's hypothesis does not hold for this driver.** `GetPosition` does not advance only once per pass. Within a pass the position changed on 10200 of 10217 consecutive polls, and no poll after the first one was stale (apart from the first pass after Start).
- The loopback capture's packet timestamps show the same 0.75 to 0.85 ms two-state error. That is what inflates the self-test's own numbers.
- A fix could go in either place, and I haven't written one. For placement, `WasapiOutput.FillScheduled` could stop trusting a single reading. One option keeps the largest `position - rate x qpc` over the last 16 or so passes, since a stale reading only makes the position smaller. Another reads twice, or fits a line over recent passes. The filter could live in Core as a pure class, and the raw readings in `windows-results/clockprobe/clockprobe.csv.raw.csv` can serve as a test fixture. For the self-test, `WasapiLoopback.Drain` could time onsets from a fit of capture position against packet arrival instead of the packet QPC stamps.
- Impact: hit rate goes from 99.9% to about 99.5% at most. It's worth fixing, but it doesn't block the change.

## Hands-on checks (step 5), DeviceClock mode

Driven with UI Automation, mouse and key injection, plus a loopback recorder that timestamps every onset. "+63 ms" below is a constant: 40 ms from mouse-down to the button's Click event, about 8 ms of handling, and the device constant.

| Check | Result |
|---|---|
| Settings window shows output, mix format and DeviceClock | **Partly.** It shows Output "WASAPI (low latency)", Scheduling "Device clock" and "Active output: wasapi 10.00 ms, DeviceClock". **The mix format is not shown** (neither is the device name). `OutputDescription` only has the period and mode. Either the guide or the label needs changing. |
| Variable Offset countdown | Pass. Frame 300 at 60 fps: beeps at +3064, +3563, +4064, +4564, +5064 ms (target 3000 to 5000). |
| Fixed Offset countdown | Pass. +3063 to +5063 ms, intervals 500.00 ± 0.02 ms. |
| IGT Tracking countdown | Pass. Play gave 30 beeps. Finals were exactly 1004.56 ms apart (one IGT second at 59.7275 fps), with lead-ins at 250 ms. |
| Offset trainer, early press | Pass. The second Start press landed about 24 ms into beep 2. Beep 2 played its full length (34.8 ms above 10% of peak, same as every other beep), beeps 3 to 5 of that round were dropped, and round 2 beeped on its own schedule. This was driven with the Start button (`StartTimer` → `ClearPending`). No hotkey is bound by default, and a hotkey press inside the early window records a landing and lets cues finish instead. This change does not touch that path. |
| Preview, sound and volume | Pass. All 8 sound changes played a preview, with the same loudness each time a sound repeated. Volume 70 → 35 halved the peak (0.24 → 0.12). |
| Switching output mid-countdown | Pass. The default was switched from the Yeti to "Realtek Digital Output" 0.7 s after beep 2, then back. Beeps 1 and 2 played on the Yeti and beeps 3 to 5 on the Realtek, 500 ms apart, with none lost. The app reopened on the new device (48 kHz, 2 channels) within about 0.3 s. On the new device the constant is about 10 ms different, as expected. Physically unplugging a device was not tested. |
| Stop cuts the countdown | Pass. Stop at +4.32 s: the beeps due at +4.56 and +5.06 s did not play. |
| Extra: +1 frame nudge mid-countdown | Pass. The gap after the nudge was 516.65 ms (500 + 16.67), with no doubled beep. |
| Extra: switching to Legacy and back at runtime | Pass. The label follows the setting. Legacy countdowns landed at +3097, +3103, +3099 ms, which is Legacy's spread and delay coming back. |

## Not covered

- `IAudioClock` unavailable (the reviewer's open item: DeviceClock falls back to waveOut). The waveOut path. A physical unplug. Non-float mix formats. Main's actual earbuds and output.
- The guide's `beep_sim.py` model was not run. Its "once per pass" case does not match what this driver does.

## Files

Everything is under `docs/handoff/windows-results/`:

- `run1-default/`, `run2-period10/`: self-test summaries and CSVs, `beepprobe.log` (fitted-clock cross-check), `beepprobe-crosscheck.csv` (per beep, both instruments).
- `clockprobe/`: `GetPosition` probe log and raw readings (`kind,pass,call_100ns,qpc_100ns,pos_s`, QPC in 100 ns units).
- `gui/`: screenshots cropped to the app window, the app's run logs (`app-runlogs/`), and the driver scripts (`gui.ps1`, `switch-test.ps1`).
- `gui/<check>/`: one folder of onset recordings per check. `onsets.csv` lists each onset, `envelope.csv` the loudness per 5 ms, and `actions.csv` the QPC time of each click.
- `tools/BeepProbe/`: the measurement harness (.NET 8, x86). The modes used here are `selftest`, `clockprobe` and `record`, plus the `--device-id` option.
- `tools/AudioSwitch/`: a small tool that lists output devices and sets the default. The switch test used it, and it restored the Yeti as default on all roles afterwards.

Build outputs are left out. The test machine's `%APPDATA%\frlg-startertool\` was created by the app on first launch, and its audio settings were put back to the defaults (ping1, volume 70, WASAPI, DeviceClock, period 0).