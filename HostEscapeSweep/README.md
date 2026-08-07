# HostEscapeSweep

Regenerates `WoofWare.DotnetRuntimeLocator/Test/host-escape-sweep.json`, the fixture recording what
a real .NET host does to a `configProperties` value on its way to `AppContext.GetData`.

`ConfigProperties.ToHostString` has to reproduce the host's JSON writer exactly, and that writer's
escaping policy is not documented anywhere we can cite — it is rapidjson's, reached through
hostpolicy. So rather than infer it, we measure it, and the test suite replays every observation.
This tool produces those observations.

## Running it

```bash
dotnet run --project HostEscapeSweep -- WoofWare.DotnetRuntimeLocator/Test/host-escape-sweep.json
```

**Run it against the runtime you want to measure.** The tool targets `net8.0` with
`RollForward=Major`, so it executes on whatever runtime it finds, and it records which one that was
in the fixture's `provenance` field. The committed fixture was measured on .NET 10, which this
repository's own devshell does not provide; to reproduce it, run under a devshell that does, e.g.

```bash
nix develop /path/to/WoofWare.PawPrint -c dotnet run --project HostEscapeSweep -- \
  WoofWare.DotnetRuntimeLocator/Test/host-escape-sweep.json
```

If the escaping ever differs between runtime versions, that is itself worth knowing — the diff will
show it, and `provenance` will say which runtimes the two sides came from.

## How it works

It runs twice.

The parent injects a `configProperties` section into its own `runtimeconfig.json`, one property per
sampled code point, each holding `["<that one character>"]`, and launches itself again. The array
wrapper is the whole point: it forces the value through the host's JSON *writer*. A top-level string
is passed through untouched, so it would measure nothing.

The child, started by a host which has just read that config, asks `AppContext.GetData` what it
actually received, and writes the fixture. The parent then restores its `runtimeconfig.json`, so a
second run measures the same thing as the first.

Because it works by editing that file, it cannot run from a single-file or self-contained publish;
it says so rather than misbehaving.

## What it samples

Every code point below U+0100 — so the C0 controls, ASCII, the C1 range and Latin-1 supplement
exhaustively — plus a spread of higher ones chosen for where encoders disagree: the JavaScript line
terminators U+2028 and U+2029, the edges of the BMP, and the astral planes. `UnicodeRanges.All`
covers only the BMP, so encoders built from it escape everything beyond it, and the host does not.
