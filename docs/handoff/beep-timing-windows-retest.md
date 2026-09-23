# Beep timing: Windows retest for the IAudioClock fallback

Written 2026-09-23. This session retests branch `audio-clock` on Windows after commit `c875f97`. It checks that the new fallback works and that nothing else changed.

## What changed since the last run

The last run is written up in `docs/handoff/beep-timing-windows-results.md`, and its raw data is in `docs/handoff/windows-results/`. Commit `c875f97` changes two files.

- In DeviceClock mode, a device without `IAudioClock` used to make `WasapiOutput` fail to open, so `BeepPlayer` fell back to waveOut. Now `WasapiOutput` logs `audio: WASAPI has no IAudioClock, using Legacy scheduling` and keeps running on WASAPI with Legacy scheduling.
- `WasapiOutput.DeviceClockActive` reports which mode the output actually runs. `BeepPlayer` schedules beeps by device time only when that flag is true. The settings window's "Active output" label uses the same check.
- Setting the environment variable `FRLG_DISABLE_AUDIOCLOCK=1` skips the `IAudioClock` request. The fallback can then be tested on any device. The variable affects only the playback stream, not the self-test's loopback capture.

## Steps

Use the same machine and default output as the last run ("Speakers (Yeti Classic)", 10 ms engine period). Close apps that play sound.

1. Pull and build.

   ```powershell
   git switch audio-clock
   git pull
   dotnet build
   dotnet test
   dotnet publish src/FRLG.StarterTool.App -p:PublishProfile=win-x64
   cd src/FRLG.StarterTool.App/bin/publish/win-x64
   ```

   `dotnet build` should report 0 errors, and `dotnet test` should report 11 passed.

2. Check that nothing else changed.

   ```powershell
   .\FRLGStarterTool.exe --beep-selftest --mode both --presses 25 --out .\selftest-regression
   ```

   Compare "Final beeps" in `selftest-regression\beep-selftest-summary.txt` with `docs/handoff/windows-results/run1-default/beep-selftest-summary.txt`.

   | Check | Pass when |
   |---|---|
   | Legacy final-beep spread | 9 to 10 ms |
   | DeviceClock final-beep spread | under 2 ms |
   | Undetected beeps | 0 in both modes |
   | Summary text | has no `WASAPI has no IAudioClock` line |

   To match the last run, also cross-check with BeepProbe (`docs/handoff/windows-results/tools/BeepProbe/`, `selftest` mode). Its fitted DeviceClock spread should stay under 1 ms.

3. Test the fallback with the self-test, in the same PowerShell window.

   ```powershell
   $env:FRLG_DISABLE_AUDIOCLOCK = "1"
   .\FRLGStarterTool.exe --beep-selftest --mode deviceclock --presses 25 --out .\selftest-noclock
   echo $LASTEXITCODE
   ```

   | Check | Pass when |
   |---|---|
   | Exit code | 0 |
   | Summary text | has `audio: WASAPI has no IAudioClock, using Legacy scheduling` |
   | Summary text | has no `falling back to waveOut` line |
   | Undetected beeps | 0 |
   | Final-beep spread | 9 to 10 ms, like Legacy in step 2 |

   The summary header still says "Mode DeviceClock", because that is the mode the test requested. The log line shows the mode the output actually ran.

4. Test the fallback in the app. Keep the variable set, and run `.\FRLGStarterTool.exe` from the same window.

   | Check | Pass when |
   |---|---|
   | Settings, "Scheduling" | still shows Device clock (the saved choice) |
   | Settings, "Active output" | ends in `Legacy` |
   | Variable Offset countdown | all 5 beeps play, 500 ms apart |
   | Default output switched mid-countdown (use `tools/AudioSwitch` as in the last run) | the remaining beeps play on the new device, with no crash and no silence |
   | Newest file in `%APPDATA%\frlg-startertool\runs\` | has the `no IAudioClock` line once per output open, and no `waveOut` line |

   Switch the default output back to the Yeti afterwards.

5. Turn the switch off and confirm DeviceClock returns.

   ```powershell
   Remove-Item Env:FRLG_DISABLE_AUDIOCLOCK
   .\FRLGStarterTool.exe
   ```

   Settings "Active output" should end in `DeviceClock`. Run one Variable Offset countdown to confirm the beeps play.

## Reporting

- Put the raw output in `docs/handoff/windows-results/retest-c875f97/`.
  - Both self-test folders.
  - The BeepProbe cross-check, if you ran it.
  - The run logs from steps 4 and 5.
  - A screenshot of the settings window in steps 4 and 5.
- Write `docs/handoff/beep-timing-windows-retest-results.md`. Give each check above a pass or fail with the measured number, then list anything not covered.
- Leave build outputs out. Put the machine's audio settings back to the defaults, as the last run did.
- Commit with a Conventional Commits message such as `docs(handoff): add the IAudioClock fallback retest results`, and push to `audio-clock`.

## If a check fails

Use the `gborges-standard:investigate` skill to find the cause before changing code. The fallback code is in `WasapiOutput.InitializeOn` (the `FRLG_DISABLE_AUDIOCLOCK` check and the `_deviceClock = false` switch) and in the `BeepPlayer.UsesDeviceClock` property.
