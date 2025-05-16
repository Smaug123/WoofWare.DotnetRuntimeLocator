namespace Example

open System
open System.IO
open System.Reflection
open WoofWare.DotnetRuntimeLocator

module Program =
    [<EntryPoint>]
    let main argv =
        let info = DotnetEnvironmentInfo.Get ()
        Console.WriteLine info
        Console.WriteLine "SDKs:"

        for sdk in info.Sdks do
            Console.WriteLine $"SDK: %O{sdk}"

        Console.WriteLine "Frameworks:"

        for f in info.Frameworks do
            Console.WriteLine $"Framework: %O{f}"

        // Identify the runtime which would execute this DLL
        let self = Assembly.GetExecutingAssembly().Location
        let runtimeSearchDirs = DotnetRuntime.SelectForDll self
        // For example, the System.Text.Json.dll which this DLL would load:
        runtimeSearchDirs
        |> Seq.tryPick (fun dir ->
            let attempt = Path.Combine (dir, "System.Text.Json.dll")
            if File.Exists attempt then Some attempt else None
        )
        |> Option.get
        |> fun s -> Console.WriteLine $"System.Text.Json location: %s{s}"

        0
