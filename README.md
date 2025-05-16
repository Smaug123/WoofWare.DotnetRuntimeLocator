# WoofWare.DotnetRuntimeLocator

Helpers to locate the .NET runtime and SDKs programmatically.
(If you're parsing `dotnet --list-runtimes`, you're doing it wrong!)

## Usage

See [the example](Example/Program.fs).

```fsharp
let info = DotnetEnvironmentInfo.Get ()
// or, if you already know a path to the `dotnet` executable...
let info = DotnetEnvironmentInfo.GetSpecific "/path/to/dotnet"
```

## Troubleshooting

The easiest way to make sure we can find a `dotnet` is to have one on your PATH.

If you have a *very* strange setup, we may be unable to locate the `libhostfxr` library we use to find the runtimes.
In that case, you can supply the environment variable `WOOFWARE_DOTNET_LOCATOR_LIBHOSTFXR`,
which should be a full path to a `libhostfxr` DLL on your system.
(Normally this is in `/usr/share/dotnet/host/fxr/{runtime}/libhostfxr.so`;
you must make sure your version is from runtime 6 or greater,
because the required symbols were not added until then.)
