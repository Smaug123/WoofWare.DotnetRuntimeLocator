# WoofWare.DotnetRuntimeLocator

Helpers to locate the .NET runtime and SDKs programmatically.
(If you're parsing `dotnet --list-runtimes`, you're doing it wrong!)

## Usage

See [the example](Example/Program.fs).

```fsharp
let info = DotnetEnvironmentInfo.Get ()
// or, if you already know a path to the `dotnet` executable...
let info = DotnetEnvironmentInfo.Get "/path/to/dotnet"
```
