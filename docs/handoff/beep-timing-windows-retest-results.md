# Beep timing: Windows retest results for the IAudioClock fallback

Written 2026-09-23. This file reports a run of `docs/handoff/beep-timing-windows-retest.md` on branch `audio-clock` at commit `caf948f`, which includes the fallback fix `c875f97`. The raw output is in `docs/handoff/windows-results/retest-c875f97/`.

Same machine and output as the first run: "Speakers (Yeti Classic)", float32 44.1 kHz 8 channels, 10 ms engine period. The audio sessions of Discord, Slack and Waterfox were muted from the step 2 rerun onward and unmuted at the end. In an earlier diagnostic run, a Discord notification started 10 ms before a beep, and the self-test timed the notification instead of the beep.

## Result

All checks pass except one. With `FRLG_DISABLE_AUDIOCLOCK=1`, a default-device switch in the middle of a countdown leaves the remaining beeps on the old device. There is no crash and no silence, but the check expects the beeps to move to the new device. The cause and a fix sketch are under "Step 4 failure" below.

## Step 1: build

`dotnet build` 0 errors, 0 warnings. `dotnet test` 11 passed. `dotnet publish` produced the exe.

## Step 2: regression, both modes

"Fitted" is BeepProbe's clock, which maps the loopback's device position to QPC by a fit against packet arrival times. It reads a clock-exact beep to about ±0.05 ms. The self-test's own timestamps carry a 0 or 0.85 ms error on this device (see the first results file).

| Check | Pass when | Measured | Verdict |
|---|---|---|---|
| Legacy final-beep spread | 9 to 10 ms | 10.24 ms self-test, 9.77 ms fitted | Pass on the fitted clock. The self-test reads 0.24 ms over the band. Its 0.85 ms timestamp error accounts for that. |
| DeviceClock final-beep spread | under 2 ms | 1.66 ms self-test, 0.87 ms fitted | Pass |
| Undetected beeps | 0 in both modes | 0 and 0 | Pass |
| Summary text | no `WASAPI has no IAudioClock` line | none | Pass |
| BeepProbe fitted DeviceClock spread | under 1 ms | 0.87 ms final beeps, 0.88 ms all beeps | Pass |

The first run measured Legacy at 9.80 ms and DeviceClock at 0.88 ms on the self-test, and 8.98 ms and 0.06 to 0.85 ms fitted. The fitted DeviceClock mean is 15.48 ms now against 15.54 ms then, so the device constant did not move.

The step 2 data is from a rerun, in `selftest-regression/`. The first attempt is kept in `selftest-regression-attempt1/`. Its self-test numbers also pass (Legacy 9.34 ms, DeviceClock 1.66 ms, 0 undetected). Its BeepProbe cross-check read 1.60 ms, which was an instrument error:

- During the first session's GUI tests, the app's atomic-clock sync saved `ClockDrift = 1.0000113` (+11.3 ppm) to `settings.json`. The self-test applies that correction to its own timeline.
- BeepProbe assumed the self-test's times were raw QPC. The mismatch grows by 11.3 µs per second, so it reached about 2 ms over the 3-minute run.
- BeepProbe now reads `ClockDrift` from the same settings file and converts the targets back to raw QPC. After the fix, BeepProbe's packet timestamps again equal the self-test's minus 0.08 ms on every beep, as in the first run.
- The first results file is unaffected, because no settings file existed when its self-tests ran (drift 1.0).
- `diag-threshold/` holds a diagnostic run. Onset thresholds of 15% and 30% gave identical timings, and each beep's peak (0.241) matched the first run, so the onset threshold does not explain the 1.60 ms.

## Step 3: fallback in the self-test

`FRLG_DISABLE_AUDIOCLOCK=1`, `--mode deviceclock --presses 25`.

| Check | Pass when | Measured | Verdict |
|---|---|---|---|
| Exit code | 0 | 0 | Pass |
| Summary text | has `audio: WASAPI has no IAudioClock, using Legacy scheduling` | present | Pass |
| Summary text | no `falling back to waveOut` | none | Pass |
| Undetected beeps | 0 | 0 of 125 | Pass |
| Final-beep spread | 9 to 10 ms, like Legacy in step 2 | 10.50 ms self-test, 9.69 ms fitted | Pass. It matches step 2's Legacy (10.24 / 9.77 ms), and the mean is 61.37 ms against Legacy's 61.30 ms. |

## Step 4: fallback in the app

App started with `FRLG_DISABLE_AUDIOCLOCK=1` (`scripts/launch-noclock.ps1`).

| Check | Pass when | Measured | Verdict |
|---|---|---|---|
| Settings, "Scheduling" | Device clock | Device clock (`step4-settings.png`) | Pass |
| Settings, "Active output" | ends in `Legacy` | "wasapi 10.00 ms, Legacy" | Pass |
| Variable Offset countdown | 5 beeps, 500 ms apart | 5 beeps, gaps 500.002 to 500.003 ms, first at +3103 ms (the Legacy delay) | Pass |
| Default output switched mid-countdown | remaining beeps on the new device, no crash, no silence | Beeps 1 to 5 all played on the Yeti. Nothing played on the Realtek. No crash, no silence. | **Fail** |
| Newest run log | one `no IAudioClock` line per output open, no `waveOut` | The newest file has one line for its one open. Across the session there were 2 lines for 2 opens (launch at 09:39:46, and a reopen at 09:41:34). No `waveOut` line. | Pass |

The switch test ran as in the first session. It starts a countdown at frame 600, switches the default to "Realtek Digital Output" at +8.7 s, after beep 2, and switches it back at +10.9 s. The loopback on the Yeti recorded beeps at +8109, +8609, +9109, +9609 and +10110 ms. The loopback on the Realtek recorded nothing. The first session ran the same test in DeviceClock mode, and beeps 3 to 5 moved to the Realtek within about 0.3 s.

## Step 4 failure

Cause:
- `BeepPlayer.OnDeviceChanged` reopens the output right away only when `UsesDeviceClock` is true. Otherwise it waits until the countdown is idle, which is the Legacy rule.
- Commit `c875f97` made `UsesDeviceClock` false whenever the output runs the fallback. So during a fallback countdown, the old WASAPI stream keeps playing the countdown on the old device.

A second gap sits behind the first. The fallback path (`QueueLegacy`) never fills `_scheduledBeeps`. If `OnDeviceChanged` reopened right away, `Reopen()` would find no remaining beeps to rewrite, and the countdown would go silent.

Evidence:
- The run log `app-runlogs/094056_2026-09-23.txt` shows `default device changed, the output will be reopened` at 09:41:04, and no new `output wasapi shared` line during the countdown. The next open is in `094133_2026-09-23.txt` at 09:41:34, when the next countdown armed.
- The recordings in `recordings/step4-switch-yeti/` and `recordings/step4-switch-realtek/` show the beeps on the old device only.

Hypothesis 1 was predicted from the code before the switch test ran. Hypotheses 2 and 3 were checked against the log and recordings afterwards.

1. `OnDeviceChanged` waits for idle. Predicted a device-change line with no reopen, and the beeps continuing on the old device. That is what happened, so this is the cause.
2. The reopen ran but failed on the Realtek. Predicted an open or failure line after 09:41:04. There is none, so no reopen ran.
3. The reopen ran and dropped the beeps because `_scheduledBeeps` was empty. Predicted silence after the switch. The beeps kept playing on the Yeti, so this is not what happened. Reading `QueueBeeps` confirms the empty list, which makes it the next failure once the first one is fixed.

Reproducing it:

```powershell
powershell -File docs\handoff\windows-results\retest-c875f97\scripts\launch-noclock.ps1
powershell -File docs\handoff\windows-results\gui\switch-test.ps1 -YetiName retest-step4-switch-yeti -RealtekName retest-step4-switch-realtek
```

The switch test printed this:

```
switched to Realtek at +8700.1 ms, restored Yeti at +10860.5 ms
Yeti loopback:
  0: stamp + 8109.397 ms (fit + 8099.557), peak 0.2412
  1: stamp + 8609.401 ms (fit + 8599.563), peak 0.2412
  2: stamp + 9109.404 ms (fit + 9099.568), peak 0.2412
  3: stamp + 9609.429 ms (fit + 9599.574), peak 0.2413
  4: stamp +10110.253 ms (fit +10099.581), peak 0.2412
Realtek loopback:
```

The scripts hold machine-specific paths and device IDs. The smallest case needs the fallback active, one armed countdown, and a default-device change before its last beep.

No unit test can reach this path today. `BeepPlayer` creates its outputs through the static `WasapiOutput.Open` and `WaveOutOutput.Open`, so a test cannot inject a fake `IBeepOutput` and raise `DeviceChanged`. A test would need a way to pass in the output factory.

A fix would touch two places in `BeepPlayer`:
- In `OnDeviceChanged`, test `_scheduling == AudioScheduling.DeviceClock` instead of `UsesDeviceClock`, as `ScheduleReopen` already does.
- In `QueueLegacy`, record each beep's absolute target in `_scheduledBeeps` when `_scheduling` is DeviceClock. `Reopen()` already has a branch that rewrites the remaining beeps through `WriteSchedule` when the new output has no device clock.

## Step 5: DeviceClock returns

App restarted without the variable (`scripts/launch-clock.ps1`).

| Check | Pass when | Measured | Verdict |
|---|---|---|---|
| Settings, "Active output" | ends in `DeviceClock` | "wasapi 10.00 ms, DeviceClock" (`step5-settings.png`) | Pass |
| Variable Offset countdown | the beeps play | 5 beeps from +3063.8 ms (the DeviceClock delay), gaps 500.005, 499.166, 500.821 and 500.005 ms. The pair around 499.2 and 500.8 is the 0.85 ms stale `GetPosition` reading from the first results file. | Pass |

## Not covered

- Physically unplugging a device. With the fallback active, the old stream would stop on the device error, and `OnDeviceChanged` would still wait for idle. So the rest of the countdown would likely be silent. This was not tested.
- A device that lacks `IAudioClock`. The environment variable simulates one.
- The guide says the fallback code is in `WasapiOutput.InitializeOn`. The `FRLG_DISABLE_AUDIOCLOCK` check and the `_deviceClock = false` switch are in `WasapiOutput.Finish`.
- The guide's 9 to 10 ms band for Legacy spread leaves no room for the self-test's 0.85 ms timestamp error. On this device the self-test can read up to about 10.85 ms for a Legacy spread that is 10 ms on the fitted clock.

## Machine state afterwards

The default output is the Yeti on all roles. The muted audio sessions are unmuted. The audio settings are back to the defaults (ping1, volume 70, WASAPI, DeviceClock, period 0). `ClockDrift` stays at the value the app measured (1.0000113). `FRLG_DISABLE_AUDIOCLOCK` was only ever set inside the test processes.
