namespace WoofWare.DotnetRuntimeLocator.Test

open System
open System.IO
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.DotnetRuntimeLocator

/// The oracle here is hostfxr's `fx_resolver.cpp`. Its
/// `search_for_best_framework_match_without_roll_to_latest_patch` admits the installed versions at
/// or above the requested one which the policy's compatibility range allows (any major for `Major`,
/// the requested major for `Minor`, ...) and keeps the lowest of them, or the highest for the
/// `Latest*` policies; `automatic_roll_to_latest_patch` then moves to the highest patch at that
/// major.minor. Pre-release versions are outside what this library models (`System.Version`
/// cannot hold one), so nothing here exercises them.
[<TestFixture>]
module TestSelectRuntime =

    let private frameworkName = "Microsoft.NETCore.App"

    /// An environment in which exactly these versions of Microsoft.NETCore.App are installed.
    let private installed (versions : string list) : DotnetEnvironmentInfo =
        let frameworks =
            versions
            |> List.map (fun v -> DotnetEnvironmentFrameworkInfo (frameworkName, "/dotnet/shared/" + frameworkName, v))

        DotnetEnvironmentInfo ("10.0.0", "0000000", [], frameworks)

    let private requesting (rollForward : RollForward) (version : string) : RuntimeOptions =
        RuntimeOptions (
            Tfm = "net8.0",
            RollForward = rollForward,
            Framework = RuntimeConfigFramework (Name = frameworkName, Version = version)
        )

    /// The version `SelectRuntime` picks for the one framework requested, if it picks one.
    let private select (options : RuntimeOptions) (env : DotnetEnvironmentInfo) : Version option =
        let selection = DotnetRuntime.SelectRuntime (options, env)

        selection.[frameworkName]
            .Visit (
                (fun framework -> Some (Version framework.Version)),
                (fun sdk -> failwith $"unexpectedly selected an SDK: %O{sdk}"),
                (fun () -> None)
            )

    [<Test>]
    let ``Major rolls to the lowest installed major above a missing requested major`` () =
        // 8 is absent and 9 is the lowest major above it, so 9's lowest minor at its highest patch.
        installed [ "10.0.7" ; "9.1.0" ; "9.0.5" ; "9.0.2" ; "7.0.20" ]
        |> select (requesting RollForward.Major "8.0.0")
        |> shouldEqual (Some (Version "9.0.5"))

    [<Test>]
    let ``Major stays on the requested major when it is installed`` () =
        // hostfxr's Major and Minor differ only when the requested major is absent.
        installed [ "10.0.7" ; "8.1.2" ; "8.0.10" ; "8.0.3" ]
        |> select (requesting RollForward.Major "8.0.0")
        |> shouldEqual (Some (Version "8.0.10"))

    [<Test>]
    let ``Major never rolls backward, even within the requested major`` () =
        installed [ "8.1.0" ; "8.0.3" ]
        |> select (requesting RollForward.Major "8.0.5")
        |> shouldEqual (Some (Version "8.1.0"))

    [<Test>]
    let ``Major selects nothing when every installed version is older`` () =
        installed [ "7.0.20" ; "6.0.36" ]
        |> select (requesting RollForward.Major "8.0.0")
        |> shouldEqual None

        installed []
        |> select (requesting RollForward.Major "8.0.0")
        |> shouldEqual None

    let private genVersion : Gen<Version> =
        gen {
            let! major = Gen.choose (1, 12)
            let! minor = Gen.choose (0, 3)
            let! patch = Gen.choose (0, 25)
            return Version (major, minor, patch)
        }

    /// hostfxr's two phases, stated as laws on the answer rather than re-derived: the lowest
    /// admissible version fixes the major.minor, and the answer is the highest patch there.
    [<Test>]
    let ``Major agrees with hostfxr's two-phase search`` () =
        let property (requested : Version, versions : Version list) : unit =
            let picked =
                installed (versions |> List.map string)
                |> select (requesting RollForward.Major (string requested))

            let admissible = versions |> List.filter (fun v -> v >= requested)

            match picked with
            | None -> admissible |> shouldEqual []
            | Some picked ->
                let lowest = List.min admissible
                (picked.Major, picked.Minor) |> shouldEqual (lowest.Major, lowest.Minor)

                let atThatMinor =
                    admissible
                    |> List.filter (fun v -> v.Major = lowest.Major && v.Minor = lowest.Minor)

                picked |> shouldEqual (List.max atThatMinor)

        let arb = Arb.fromGen (Gen.zip genVersion (Gen.listOf genVersion))
        Check.One (Config.QuickThrowOnFailure.WithMaxTest 500, Prop.forAll arb property)

    /// hostfxr reads both the file's `rollForward` and `DOTNET_ROLL_FORWARD` through
    /// `roll_forward_option_from_string`, a `strcasecmp` loop, so any casing names the policy.
    [<Test>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD is read case-insensitively`` () =
        let previous = Environment.GetEnvironmentVariable "DOTNET_ROLL_FORWARD"

        try
            Environment.SetEnvironmentVariable ("DOTNET_ROLL_FORWARD", "latestMAJOR")

            installed [ "10.0.7" ; "9.0.5" ; "8.0.10" ]
            |> select (requesting RollForward.Disable "8.0.0")
            |> shouldEqual (Some (Version "10.0.7"))
        finally
            Environment.SetEnvironmentVariable ("DOTNET_ROLL_FORWARD", previous)

    /// The `runtimeconfig.json` the SDK ships in a dotnet tool package (this one is Fantomas
    /// 7.0.0's, verbatim), read through the entry point a consumer actually calls. The test host
    /// runs on a Microsoft.NETCore.App of major at least 8, so `Major` from 8.0.0 has somewhere to land.
    [<Test>]
    let ``SelectForDll accepts a dotnet tool's runtimeconfig`` () =
        let dir =
            Path.Combine (Path.GetTempPath (), "locator-test-" + Path.GetRandomFileName ())

        Directory.CreateDirectory dir |> ignore

        try
            File.WriteAllText (
                Path.Combine (dir, "fantomas.runtimeconfig.json"),
                """{
  "runtimeOptions": {
    "tfm": "net8.0",
    "rollForward": "Major",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "8.0.0"
    },
    "configProperties": {
      "System.GC.Server": true,
      "System.Reflection.Metadata.MetadataUpdater.IsSupported": false,
      "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false
    }
  }
}"""
            )

            let dirs = DotnetRuntime.SelectForDll (Path.Combine (dir, "fantomas.dll"))

            dirs.[0] |> shouldEqual (DirectoryInfo(dir).FullName)

            let frameworks =
                dirs
                |> Seq.skip 1
                |> Seq.filter (fun d -> File.Exists (Path.Combine (d, "System.Private.CoreLib.dll")))
                |> Seq.toList

            frameworks |> shouldNotEqual []

            for framework in frameworks do
                Version(Path.GetFileName framework).Major |> shouldBeGreaterThan 7
        finally
            Directory.Delete (dir, true)
