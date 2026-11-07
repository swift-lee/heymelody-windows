# Current Native-Only Filament Benchmark

This isolated research project references the current managed renderer. It
does not reference Avalonia, initialize a headset service, or edit preferences.
No legacy native runtime is accepted. Verify the complete runtime independently
with `tools/Verify-FilamentRuntime.ps1 -RequireCurrent` before building.

The maintained copy lives in `tools/research/HeyMelody.Rendering.Benchmark`.
Build only after other audit runs have released their source/build inputs:

```powershell
dotnet build tools/research/HeyMelody.Rendering.Benchmark/NativeBenchmark.csproj -c Release -p:NativeRuntimeDirectory=C:\absolute\verified\win-arm64
```

The directory supplied must directly contain the manifest, DLL, probe, materials
and licenses. Run under the native ARM64 `dotnet` host. Run only ONE GPU workload
at a time; do not run this benchmark simultaneously with UI measurements.

```powershell
dotnet tools/research/HeyMelody.Rendering.Benchmark/bin/Release/net10.0/NativeBenchmark.dll --backend vulkan --size 640 --msaa 4 --model src/HeyMelody.App/Assets/Devices/EncoFree4Dynaudio/model.glb --output-dir artifacts/native-vulkan-640-4-NEW --warmup 5 --seconds 15 --request-hz 120
```

Run the 320, 640 and 960 square physical pixel sizes with MSAA 1 and 4 on each
backend, using a fresh output directory for every run. Repeat the same inputs
with `--request-hz 0` for a serialized one-request/one-copy throughput experiment,
if useful. It is a different queueing regime, NOT an Avalonia presentation rate.

Polling uses a Windows high-resolution waitable timer created with
`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, armed for a relative one-shot 1 ms and
waited via `WaitForSingleObject`. Its `SafeWaitHandle` is disposed at exit. Timer
creation, arming or wait errors fail the run explicitly; there is no coarse-timer
fallback. No global `timeBeginPeriod`, power plan or system setting is changed.

`--request-hz` is only the request TARGET. Inspect `actualRequestHz`,
`pollCallHz`, and the full `waitWallMilliseconds` distribution before interpreting
throughput. CPU load, scheduling and API calls can keep actual requests below the
target. Earlier local schema-1 runs using `Thread.Sleep(1)` were limited to about
65 requests/s on the audited Windows device despite a 120 Hz target. Those runs
must not be used as evidence of a GPU throughput ceiling. Their original source
remains in the local audit archive; current schema-2 runs record every timer wait.

Each run retains full measured per-call samples and copied-frame statistics.
Summary percentiles do not use the UI's rolling 120-frame history. Skipped frames
are inherently absent from copied-frame statistics. `RequestFrame` calls can
coalesce; maximum completed frame ID advance is NOT an exact native submission
or completion count. Native render time is a CPU wall-clock submit measurement,
not GPU time; native readback includes queuing, callbacks and background blending.

The native-only experiment intentionally omits WriteableBitmap locking,
CPU-to-Skia uploads and DWM. It measures a square viewport, so it is a scaling
control, not a geometry-identical substitute for the UI's actual model viewport.
CPU includes the managed sampler and the high-resolution waiting poll loop. Model
and runtime hashes, full camera configuration, architecture and native diagnostic
metadata are saved with each run. The benchmark does not change system timer,
power, driver, foreground or user preference settings. It does not claim exact
1 ms wakeups or a guaranteed target request frequency.
