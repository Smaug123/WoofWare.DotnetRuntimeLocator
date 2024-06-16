namespace Example

open System
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

        0
