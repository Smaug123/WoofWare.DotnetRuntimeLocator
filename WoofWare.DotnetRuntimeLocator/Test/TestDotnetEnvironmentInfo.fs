namespace WoofWare.DotnetRuntimeLocator.Test

open System
open FsUnitTyped
open WoofWare.DotnetRuntimeLocator
open NUnit.Framework

[<TestFixture>]
module TestDotnetEnvironmentInfo =

    [<Test>]
    let ``Can locate the runtime`` () =
        let runtimes = DotnetEnvironmentInfo.Get ()

        // In the test setup, there should be an SDK!
        runtimes.Sdks |> Seq.length |> shouldBeGreaterThan 0
        runtimes.Frameworks |> Seq.length |> shouldBeGreaterThan 0

        Console.WriteLine $"%O{runtimes}"
