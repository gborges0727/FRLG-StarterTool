#!/usr/bin/env python3
"""Simulate when a beep reaches the WASAPI engine in FRLG-StarterTool, today and with the proposed fix.

Model of the engine (shared mode, event driven):
  * Pass j happens at T_j = j * Pf / fs_true and reads client frames [j*Pf, (j+1)*Pf).
  * So client frame n leaves the engine at n / fs_true (T_0 = 0). Device latency after the
    engine is a constant and is left out (L = 0).
  * After each pass the engine sets the event; the BeepFeed thread wakes w_j later.

Model of the app, following the code:
  * WasapiOutput.Finish fills to _targetFrames = 2 * periodFrames before Start (WasapiOutput.cs:200, 230).
  * Fill tops padding up to _targetFrames and returns early if padding >= target (WasapiOutput.cs:340-342).
    The frames it writes go at stream index _totalFedFrames (WasapiOutput.cs:370).
  * Write sets _pcm, _sourceFrame = 0 and calls Fill (WasapiOutput.cs:454-460). So pcm frame 0 lands
    at whatever _totalFedFrames is at the write, either now or at the feed thread's next Fill.
  * BeepPlayer.WriteSchedule puts beep i at floor(offset_i * 48) frames into pcm (BeepPlayer.cs:159).
    The offsets were computed by the caller against its own clock read t0.

The fix: each feed pass reads IAudioClock.GetPosition (pos, qpc), maps every frame about to be written
to a time, and starts each beep at the frame whose time matches its absolute target.
"""
import argparse
import math

import numpy as np

FS_NOM = 48000.0
GBA_FRAME_MS = 1000.0 / 59.7275


def feed_wake_ms(rng, n):
    # BeepFeed runs at Highest priority with MMCSS "Pro Audio" (WasapiOutput.cs:244, 278).
    return 0.05 + rng.exponential(0.15, n)


class Engine:
    """Event-level model of one WASAPI shared stream plus the BeepFeed thread."""

    def __init__(self, rng, period_ms, ppm, horizon_passes):
        self.pf = int(round(period_ms / 1000.0 * FS_NOM))          # periodFrames (480 or 144)
        self.target = 2 * self.pf                                    # _targetFrames
        self.fs_true = FS_NOM * (1.0 + ppm * 1e-6)                   # device clock
        self.pass_ms = self.pf / self.fs_true * 1000.0
        self.wake = feed_wake_ms(rng, horizon_passes)

    def pass_time(self, j):
        return j * self.pass_ms

    def consumed(self, t):
        # Passes at or before t have each read one period.
        return (int(math.floor(t / self.pass_ms)) + 1) * self.pf

    def leave_ms(self, frame):
        return frame / self.fs_true * 1000.0


def current_code_trial(rng, eng, t_write, offsets_ms, write_lag_ms):
    """Returns lateness (ms) of each beep for one write, following Write + Fill as coded."""
    # Replay feed-thread fills up to t_write. Fill before Start put 2 periods in.
    fed = eng.target
    k_last = int(math.floor(t_write / eng.pass_ms))
    for j in range(0, k_last + 1):
        wake_t = eng.pass_time(j) + eng.wake[j]
        if wake_t > t_write:
            break
        padding = fed - (j + 1) * eng.pf
        if padding < eng.target:
            fed += eng.target - padding
        assert fed == (j + 3) * eng.pf, "steady state broken"
    # Write: _pcm = pcm; _sourceFrame = 0; Fill(). Either Fill writes now at 'fed',
    # or it returns early and the next feed-thread Fill writes pcm frame 0 at 'fed'.
    n0 = fed
    t0 = t_write - write_lag_ms  # the caller's clock read that the offsets are relative to
    late = []
    for x in offsets_ms:
        dest = math.floor(x / 1000.0 * FS_NOM)       # BeepPlayer.cs:159
        late.append(eng.leave_ms(n0 + dest) - (t0 + x))
    return late


def fix_trial(rng, eng, t_write, offsets_ms, write_lag_ms, qpc_jitter_ms, late_policy, pos_per_pass=False):
    """Feed thread places each beep at the frame GetPosition says matches its target time."""
    t0 = t_write - write_lag_ms
    targets = sorted(t0 + x for x in offsets_ms)
    placed = {}
    # Steady state (checked by the replay in current_code_trial): wake j writes frames
    # [(j+2)*Pf, (j+3)*Pf). Start at the first wake after the write, skip passes that
    # cannot hold the earliest target.
    j = int(math.floor(t_write / eng.pass_ms))
    if eng.pass_time(j) + eng.wake[j] < t_write:
        j += 1
    first_frame = targets[0] / 1000.0 * eng.fs_true
    j = max(j, int(first_frame // eng.pf) - 4)
    while len(placed) < len(targets):
        wake_t = eng.pass_time(j) + eng.wake[j]
        fed = (j + 2) * eng.pf
        frames = eng.pf
        # GetPosition: device position latched at t_latch, returned with its QPC stamp.
        t_latch = wake_t
        pos = t_latch / 1000.0 * eng.fs_true
        if pos_per_pass:
            # Worst plausible driver: position moves only at each pass, stamped at the call.
            pos = eng.pass_time(j) / 1000.0 * eng.fs_true
        qpc = t_latch + rng.normal(0.0, qpc_jitter_ms)
        for tau in targets:
            if tau in placed:
                continue
            n_star = pos + (tau - qpc) / 1000.0 * FS_NOM   # nominal rate, fresh anchor each pass
            if n_star < fed:
                # Target is behind the frames already handed to the engine.
                placed[tau] = fed if late_policy == "asap" else None
            elif n_star < fed + frames:
                placed[tau] = int(round(n_star))
        j += 1
    return [None if placed[t] is None else eng.leave_ms(placed[t]) - t for t in targets]


def run(kind, period_ms, trials, seed, offsets_fn, ppm=0.0, write_lag_fn=None,
        qpc_jitter_ms=0.02, late_policy="asap", pos_per_pass=False):
    rng = np.random.default_rng(seed)
    out, dropped = [], 0
    for _ in range(trials):
        offsets = offsets_fn(rng)
        span_passes = int((max(offsets) + 400.0) / period_ms) + 200
        eng = Engine(rng, period_ms, ppm, span_passes + 400)
        t_write = rng.uniform(100.0, 100.0 + 20 * period_ms)  # random phase against the engine
        lag = write_lag_fn(rng) if write_lag_fn else 0.0
        if kind == "current":
            res = current_code_trial(rng, eng, t_write, offsets, lag)
        else:
            res = fix_trial(rng, eng, t_write, offsets, lag, qpc_jitter_ms, late_policy, pos_per_pass)
        for r in res:
            if r is None:
                dropped += 1
            else:
                out.append(r)
    return np.array(out), dropped


def hit_rate(late_ms):
    # Perfect tuning removes the median. Frame phase uniform: a press off by e misses with
    # probability min(|e|, F) / F.
    e = late_ms - np.median(late_ms)
    return float(np.mean(1.0 - np.minimum(np.abs(e), GBA_FRAME_MS) / GBA_FRAME_MS))


def row(label, a, dropped=0):
    p5, p95 = np.percentile(a, [5, 95])
    extra = f"  dropped {dropped}" if dropped else ""
    return (f"| {label:<34} | {a.min():7.3f} | {a.mean():7.3f} | {p5:7.3f} | {p95:7.3f} | {a.max():7.3f} "
            f"| {a.max() - a.min():6.3f} | {100 * hit_rate(a):5.1f}% |{extra}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--trials", type=int, default=20000)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    n = args.trials

    typical = lambda rng: [rng.uniform(500.0, 5000.0)]
    hdr = ("| case                               |   min   |  mean   |   p5    |   p95   |   max   | spread | hit  |\n"
           "|------------------------------------|---------|---------|---------|---------|---------|--------|------|")
    print("Beep lateness in ms at the engine output (device latency L left out), offsets 0.5 to 5 s")
    print(hdr)
    for p in (10.0, 3.0):
        a, _ = run("current", p, n, args.seed, typical)
        print(row(f"current code, {p:g} ms period", a))
    for p in (10.0, 3.0):
        a, d = run("fix", p, n, args.seed, typical)
        print(row(f"fix, {p:g} ms period", a, d))

    print("\nWith 0 to 3 ms of caller-to-Write lag (uniform), 10 ms period")
    print(hdr)
    lagf = lambda rng: rng.uniform(0.0, 3.0)
    a, _ = run("current", 10.0, n, args.seed, typical, write_lag_fn=lagf)
    print(row("current code + write lag", a))
    a, d = run("fix", 10.0, n, args.seed, typical, write_lag_fn=lagf)
    print(row("fix + write lag", a, d))

    print("\nDevice clock 100 ppm fast against QPC, 10 ms period, one beep at a fixed offset")
    print(hdr)
    for x in (10000.0, 30000.0, 60000.0):
        a, _ = run("current", 10.0, max(n // 20, 500), args.seed, lambda rng, x=x: [x], ppm=100.0)
        print(row(f"current code, offset {x / 1000:g} s", a))
        a, d = run("fix", 10.0, max(n // 20, 500), args.seed, lambda rng, x=x: [x], ppm=100.0)
        print(row(f"fix, offset {x / 1000:g} s", a, d))

    print("\nFix sensitivity to GetPosition quality, 10 ms period")
    print(hdr)
    for jit in (0.1, 0.25):
        a, d = run("fix", 10.0, n, args.seed, typical, qpc_jitter_ms=jit)
        print(row(f"fix, QPC stamp jitter sd {jit:g} ms", a, d))
    a, d = run("fix", 10.0, n, args.seed, typical, pos_per_pass=True)
    print(row("fix, position steps once per pass", a, d))

    print("\nAnalytic hit rate 1 - w/(4F), F = %.3f ms:" % GBA_FRAME_MS)
    for w in (10.0, 3.0, 0.46, 0.2):
        print(f"  uniform error {w:g} ms wide -> {100 * (1 - w / (4 * GBA_FRAME_MS)):.1f}%")

    print("\nShort lead: one beep 0 to 40 ms after the caller's read (Alert, Preview, re-cut, IGT)")
    print(hdr)
    short = lambda rng: [rng.uniform(0.0, 40.0)]
    for p in (10.0, 3.0):
        a, _ = run("current", p, n, args.seed, short)
        print(row(f"current code, {p:g} ms", a))
        a, d = run("fix", p, n, args.seed, short, late_policy="asap")
        on_time = np.mean(np.abs(a) < 0.1)
        print(row(f"fix asap, {p:g} ms ({100 * on_time:.0f}% on time)", a, d))


if __name__ == "__main__":
    main()
