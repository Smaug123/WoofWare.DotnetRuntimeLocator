namespace WoofWare.DotnetRuntimeLocator.Test

open System.IO
open System.Reflection
open System.Text.Json
open FsUnitTyped
open NUnit.Framework
open WoofWare.DotnetRuntimeLocator

[<TestFixture>]
module TestRuntimeConfigParse =

    [<Test>]
    let ``Can parse our own runtime config`` () =
        let assy = Assembly.GetExecutingAssembly ()

        let runtimeConfig =
            Path.Combine (FileInfo(assy.Location).Directory.FullName, $"%s{assy.GetName().Name}.runtimeconfig.json")
            |> File.ReadAllText

        let actual = JsonSerializer.Deserialize<RuntimeConfig> runtimeConfig

        let expected =
            RuntimeConfig (
                RuntimeOptions =
                    RuntimeOptions (
                        Tfm = "net8.0",
                        Framework = RuntimeConfigFramework (Name = "Microsoft.NETCore.App", Version = "8.0.0")
                    )
            )

        actual |> shouldEqual expected

    [<Test>]
    let ``Example 1`` () =
        let content =
            Assembly.getEmbeddedResource (Assembly.GetExecutingAssembly ()) "runtimeconfig1.json"

        let expected =
            RuntimeConfig (
                RuntimeOptions =
                    RuntimeOptions (
                        Tfm = "net8.0",
                        RollForward = RollForward.Major,
                        Framework = RuntimeConfigFramework (Name = "Microsoft.NETCore.App", Version = "8.0.0")
                    )
            )

        DotnetRuntime.DeserializeRuntimeConfig content |> shouldEqual expected
