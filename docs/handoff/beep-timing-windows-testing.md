# Beep timing: testing on Windows

Written 2026-09-22. The next session runs on Windows. It measures whether the FRLG Starter Tool's new beep timing removes the random delay in each beep. It also checks that the rest of the app still behaves the same.

## Where the work stands

The FRLG Starter Tool is ConstructiveCynicism's Pokémon FireRed/LeafGreen speedrun helper. Its countdown beeps tell the runner when to press, so the press hits one exact GBA frame (16.743 ms). The runner main misses that frame often.

- The fork is `git@github.com:gborges0727/FRLG-StarterTool.git`, on branch `audio-clock`. Commit `fb0b677` holds the whole change, on top of upstream commit `c2d6f1c`.
- GitHub Actions builds the Windows exe from the branch. The run is at https://github.com/gborges0727/FRLG-StarterTool/actions/runs/35814896411, and its artifact `FRLGStarterTool` holds `FRLGStarterTool.exe`.
- The code builds on a Mac with `dotnet build -p:EnableWindowsTargeting=true`. Its 11 new unit tests pass. Nothing has run on Windows, so no audio has played through the new code.
- The maintainer said on Discord to send PRs and they will review them. No PR is open yet.
- The Bear note "FlowTimer beep timing investigation" covers the same problem in stringflow's FlowTimer. It has the background on the manip and on Windows audio.

## The bug

WASAPI is the Windows audio interface the tool uses. WASAPI plays sound in fixed chunks, called the engine period. main's wired earbuds refuse any period below 10 ms.

Today a countdown starts playing behind the 1 to 2 chunks of audio already queued. At a 10 ms period each beep plays 20 to 30 ms late. How late depends on where the engine is in its cycle when the countdown is written, and that changes on every press. The runner's offset cancels the average delay but not the 10 ms spread. A spread that wide makes about 15% of perfectly timed presses miss the frame.

The maintainer thinks the 10 ms period itself caps accuracy. It does not. A beep can start on any of the 480 samples inside a chunk. The tool just never asks where playback is when it places the beep.

## What the change does

- On every engine pass, the WASAPI output (`WasapiOutput.FillScheduled`) calls `IAudioClock.GetPosition`. The call returns how many samples have played and the system time when that was true. From that pair the tool knows when each sample it writes next will play.
- `DeviceBeepMixer` (in `src/FRLG.StarterTool.Core/Audio/`) keeps a list of beeps with absolute target times. It mixes each beep in starting at the sample whose play time matches the target. Beeps that overlap add together, a beep can run across two chunks, and a beep's start stays fixed once its first sample is written.
- A beep asked for too late to place, such as Preview at 0 ms, plays at the first sample the tool can still write. The log line `audio: beep started N ms late` records how late.
- The timers pass in the clock reading they already took, so `BeepPlayer` no longer reads the clock a second time.
- A new setting, `AudioScheduling`, chooses `DeviceClock` (the new default) or `Legacy` (today's code, unchanged). It appears in the settings window, which also shows which output and mode are active.
- A device change reopens the output right away and keeps every beep still in the future. Today the tool waits until playback is idle and drops the countdown.
- The waveOut fallback keeps today's timing. It runs only when WASAPI fails to open.
- `FRLGStarterTool.exe --beep-selftest` measures the timing. The next section covers it.

## Decisions and why

- **Keep the 10 ms period and fix placement.** main's earbuds cannot go lower, and placement makes the period irrelevant to accuracy.
- **Keep Legacy selectable.** Both modes can then be measured on the same PC and earbuds in one run, and a user can switch back if the new mode misbehaves.
- **The self-test schedules beeps 1 to 3 s after each simulated press.** A beep due at 0 ms is late in both modes, and it would hide the difference between them.
- **Leave waveOut alone.** It can report its position, but every new schedule resets that position to zero, so placing beeps would need a rewrite of that output.

## Do not retry

- stringflow/FlowTimer PRs 8 and 9 (https://github.com/stringflow/FlowTimer/pull/8, /pull/9) fixed the same bug in FlowTimer. FlowTimer plays audio through SDL, and the Starter Tool does not use SDL. Those PRs are closed and do not apply to this repo.
- Asking for a shorter engine period does not fix main's case, because his earbuds refuse it.

## Steps on Windows

1. Get the build. Either download the artifact from the Actions run above, or clone and build.

   ```powershell
   git clone git@github.com:gborges0727/FRLG-StarterTool.git
   cd FRLG-StarterTool
   git switch audio-clock
   dotnet build
   dotnet test
   dotnet publish src/FRLG.StarterTool.App -p:PublishProfile=win-x64
   ```

   The published exe is `src/FRLG.StarterTool.App/bin/publish/win-x64/FRLGStarterTool.exe`. `dotnet test` should report 11 passed.

2. Close every other app that plays sound. Set the default output to the device under test (main uses wired earbuds). Then run the self-test.

   ```powershell
   .\FRLGStarterTool.exe --beep-selftest --mode both --presses 25 --out .\selftest
   ```

   It runs without a window, takes about 3 minutes (25 presses at about 3.4 s each, per mode), and exits 0 on success. It writes one CSV per mode and `beep-selftest-summary.txt` into `.\selftest`. The self-test reads the saved settings (sound, volume, period, clock drift) from `settings.json` under the tool's settings folder. It uses the saved volume, so the volume must be above 0.

3. Read the summary. Compare the "Final beeps" rows for Legacy and DeviceClock.

   | Mode | Expected spread (max minus min) | Expected mean |
   |---|---|---|
   | Legacy, 10 ms period | about 10 ms | 20 to 30 ms late |
   | Legacy, 3 ms period | about 3 ms | 7 to 9 ms late |
   | DeviceClock, any period | under 1 ms | a constant, possibly negative |

   Judge by the spread, not the mean. The self-test records the engine's output through loopback capture. `GetPosition` reports when a sample reaches the speaker, so DeviceClock beeps can show up early in loopback by the device's own latency. That shift is constant, and the runner's offset absorbs it. The spread is what decides hits and misses.

4. Repeat step 2 with the period forced to 10 ms, to match main's earbuds. Set it in the settings window, or set `AudioPeriodMs` to 10 in `settings.json`, then run the self-test again.

5. Check the app by hand in DeviceClock mode.
   - The settings window shows the output, the mix format, and DeviceClock.
   - Variable Offset, Fixed Offset, and IGT Tracking countdowns beep at the right times.
   - In the offset trainer, pressing early lets the current beep finish and cancels only the later ones.
   - Preview plays, and changing the sound and the volume takes effect.
   - Unplugging or switching the output device mid-countdown keeps the remaining beeps on the new device.
   - Stop cuts the countdown off.

6. If DeviceClock shows no improvement, or the spread is over 1 ms, use the `gborges-standard:investigate` skill before changing code. Check whether `GetPosition` advances only once per pass on this driver. The Mac model predicts about 1.5 ms of spread in that case. That model is `docs/handoff/beep_sim.py`, next to this file. Run it with `python docs/handoff/beep_sim.py --trials 20000`.

## Things a reviewer flagged, not yet fixed

- If `IAudioClock` is unavailable, DeviceClock mode fails to open WASAPI at all and falls back to waveOut. That makes timing worse, not the same. Falling back to the Legacy WASAPI path would be better.
- The self-test's hit rate assumes the runner presses perfectly. It shows only the error the tool adds.

## After testing

When the numbers hold, open a PR from `gborges0727:audio-clock` to `ConstructiveCynicism/FRLG-StarterTool` `main`. Put the self-test summary in the PR body. Run the `gborges-standard:writing-voice` passes on the PR body before posting it.

## Questions only the user can answer

- Should the PR go upstream as one change, or split into the scheduling fix and the self-test?
- Should DeviceClock be the default, or ship off by default until main has tried it?
- Should the results go to main directly, or only through the maintainer?
