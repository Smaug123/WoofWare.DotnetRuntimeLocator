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
/// major.minor. Pre-release versions order below the release of the same three components, and a
/// framework reference for a release version searches the release versions alone before falling back
/// to the whole list, which is hostfxr's `prefer_release`.
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

    /// Run `f` with `DOTNET_ROLL_FORWARD` set to `value` (or unset, for `None`), restoring it after.
    let private withRollForwardEnv (value : string option) (f : unit -> 'a) : 'a =
        let previous = Environment.GetEnvironmentVariable "DOTNET_ROLL_FORWARD"

        try
            Environment.SetEnvironmentVariable ("DOTNET_ROLL_FORWARD", Option.toObj value)
            f ()
        finally
            Environment.SetEnvironmentVariable ("DOTNET_ROLL_FORWARD", previous)

    /// An environment and a file setting under which the env var's answer is distinguishable
    /// from the file's: `Disable` at 8.0.10 picks 8.0.10, while `LatestMajor` picks 10.0.7.
    let private selectUnderEnv (value : string option) : Version option =
        withRollForwardEnv
            value
            (fun () ->
                installed [ "10.0.7" ; "9.0.5" ; "8.0.10" ]
                |> select (requesting RollForward.Disable "8.0.10")
            )

    /// hostfxr reads both the file's `rollForward` and `DOTNET_ROLL_FORWARD` through
    /// `roll_forward_option_from_string`, a `strcasecmp` loop, so any casing names the policy.
    [<Test>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD is read case-insensitively`` () =
        selectUnderEnv (Some "latestMAJOR") |> shouldEqual (Some (Version "10.0.7"))

    /// `strcasecmp` compares the whole string, so anything but a policy name makes hostfxr
    /// refuse to launch ("Invalid value for environment variable 'DOTNET_ROLL_FORWARD'"):
    /// padding is not trimmed, and the numeric form of the enum is not a name.
    [<TestCase(" latestMAJOR ")>]
    [<TestCase("LatestMajor\n")>]
    [<TestCase("\tMajor")>]
    [<TestCase("3")>]
    [<TestCase("Major,Minor")>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD must be exactly a policy name`` (value : string) =
        let exn =
            Assert.Throws<ArgumentException> (fun () -> selectUnderEnv (Some value) |> ignore)

        exn.Message |> shouldContainText "DOTNET_ROLL_FORWARD"

    /// hostfxr's `pal::getenv` reports an empty variable as unset, so the file's setting applies.
    [<Test>]
    [<NonParallelizable>]
    let ``an empty DOTNET_ROLL_FORWARD is unset`` () =
        selectUnderEnv (Some "") |> shouldEqual (Some (Version "8.0.10"))
        selectUnderEnv None |> shouldEqual (Some (Version "8.0.10"))

    let private genPolicyName : Gen<RollForward * string> =
        gen {
            let! policy = Gen.elements (Enum.GetValues<RollForward> ())
            let name = string policy
            let! flips = Gen.listOfLength name.Length (ArbMap.defaults |> ArbMap.generate<bool>)

            let spelling =
                Seq.zip name flips
                |> Seq.map (fun (c, flip) ->
                    if flip then
                        Char.ToUpperInvariant c
                    else
                        Char.ToLowerInvariant c
                )
                |> Seq.toArray
                |> String

            return policy, spelling
        }

    /// Every casing of a policy name is that policy, and that name with one more character
    /// anywhere is nothing at all.
    [<Test>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD accepts exactly the casings of the six names`` () =
        let expected : Map<RollForward, Version option> =
            Map.ofList
                [
                    RollForward.Disable, Some (Version "8.0.10")
                    RollForward.LatestPatch, Some (Version "8.0.10")
                    RollForward.Minor, Some (Version "8.0.10")
                    RollForward.LatestMinor, Some (Version "8.0.10")
                    RollForward.Major, Some (Version "8.0.10")
                    RollForward.LatestMajor, Some (Version "10.0.7")
                ]

        let property ((policy, spelling) : RollForward * string, extra : char, position : int) : unit =
            selectUnderEnv (Some spelling) |> shouldEqual expected.[policy]

            let padded = spelling.Insert (position % (spelling.Length + 1), string extra)

            Assert.Throws<ArgumentException> (fun () -> selectUnderEnv (Some padded) |> ignore)
            |> ignore

        let genExtra = Gen.elements [ ' ' ; '\n' ; '\t' ; '\r' ; '0' ; 'x' ; '-' ]
        let genPosition = Gen.choose (0, 20)
        let arb = Arb.fromGen (Gen.zip3 genPolicyName genExtra genPosition)
        Check.One (Config.QuickThrowOnFailure.WithMaxTest 300, Prop.forAll arb property)

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

    /// The version `SelectRuntime` picks, as the string it was installed under, so that a prerelease
    /// label survives the comparison.
    let private selectVersion (options : RuntimeOptions) (env : DotnetEnvironmentInfo) : string option =
        let selection = DotnetRuntime.SelectRuntime (options, env)

        selection.[frameworkName]
            .Visit (
                (fun framework -> Some framework.Version),
                (fun sdk -> failwith $"unexpectedly selected an SDK: %O{sdk}"),
                (fun () -> None)
            )

    /// Run `f` with `DOTNET_ROLL_FORWARD_TO_PRERELEASE` set to `value` (or unset), restoring it after.
    let private withPrereleaseEnv (value : string option) (f : unit -> 'a) : 'a =
        let name = "DOTNET_ROLL_FORWARD_TO_PRERELEASE"
        let previous = Environment.GetEnvironmentVariable name

        try
            Environment.SetEnvironmentVariable (name, Option.toObj value)
            f ()
        finally
            Environment.SetEnvironmentVariable (name, previous)

    /// The motivating regression: `System.Version` cannot parse a prerelease, so every lookup threw
    /// as soon as any prerelease framework was installed -- even one of an unrelated major, and even
    /// for an app which asked for a release version.
    [<Test>]
    let ``an installed prerelease does not disturb an unrelated request`` () =
        installed [ "11.0.0-preview.7.26381.103" ; "9.0.5" ; "9.0.2" ]
        |> selectVersion (requesting RollForward.Minor "9.0.0")
        |> shouldEqual (Some "9.0.5")

    /// 9.0.6-preview.1 is the highest patch at 9.0, so the patch roll-forward would land on it; the
    /// release preference is what keeps a release request on a release.
    [<Test>]
    [<NonParallelizable>]
    let ``a release request prefers a release to a higher prerelease`` () =
        withPrereleaseEnv
            None
            (fun () ->
                installed [ "9.0.5" ; "9.0.6-preview.1" ]
                |> selectVersion (requesting RollForward.Minor "9.0.0")
                |> shouldEqual (Some "9.0.5")
            )

    /// hostfxr's release-only search is a first pass, not a filter: when it finds nothing the whole
    /// list is searched again.
    [<Test>]
    [<NonParallelizable>]
    let ``a release request falls back to a prerelease when no release will do`` () =
        withPrereleaseEnv
            None
            (fun () ->
                installed [ "9.0.6-preview.1" ; "8.0.10" ]
                |> selectVersion (requesting RollForward.Minor "9.0.0")
                |> shouldEqual (Some "9.0.6-preview.1")
            )

    /// `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` clears the preference, so the same environment and
    /// request now roll onto the prerelease.
    [<Test>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD_TO_PRERELEASE removes the release preference`` () =
        let selectHere () =
            installed [ "9.0.5" ; "9.0.6-preview.1" ]
            |> selectVersion (requesting RollForward.Minor "9.0.0")

        withPrereleaseEnv (Some "1") selectHere |> shouldEqual (Some "9.0.6-preview.1")

    /// hostfxr reads the variable with `atoi` and tests the result against 1, so anything whose
    /// leading integer is not 1 leaves the preference in place; an empty variable is unset.
    [<TestCase("0")>]
    [<TestCase("2")>]
    [<TestCase("-1")>]
    [<TestCase("")>]
    [<TestCase("true")>]
    [<TestCase("x1")>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD_TO_PRERELEASE takes only the value 1`` (value : string) =
        withPrereleaseEnv
            (Some value)
            (fun () ->
                installed [ "9.0.5" ; "9.0.6-preview.1" ]
                |> selectVersion (requesting RollForward.Minor "9.0.0")
            )
        |> shouldEqual (Some "9.0.5")

    /// `atoi` skips leading whitespace and accepts a leading '+', and stops at the first non-digit,
    /// so all of these are the value 1.
    [<TestCase(" 1")>]
    [<TestCase("+1")>]
    [<TestCase("1x")>]
    [<TestCase("\t1.9")>]
    [<NonParallelizable>]
    let ``DOTNET_ROLL_FORWARD_TO_PRERELEASE reads its value as atoi does`` (value : string) =
        withPrereleaseEnv
            (Some value)
            (fun () ->
                installed [ "9.0.5" ; "9.0.6-preview.1" ]
                |> selectVersion (requesting RollForward.Minor "9.0.0")
            )
        |> shouldEqual (Some "9.0.6-preview.1")

    /// `automatic_roll_to_latest_patch` is skipped when the best match is a prerelease: "for
    /// pre-release we will only roll to closest available". So a prerelease request stays put rather
    /// than climbing to the release at the same major.minor.
    [<Test>]
    let ``a prerelease request does not roll to the latest patch`` () =
        installed [ "9.0.0-preview.1" ; "9.0.0-preview.5" ; "9.0.0" ]
        |> selectVersion (requesting RollForward.LatestPatch "9.0.0-preview.1")
        |> shouldEqual (Some "9.0.0-preview.1")

    /// A prerelease request still rolls forward to a later prerelease when its own is absent, because
    /// the two-phase search picks the lowest admissible version.
    [<Test>]
    let ``a prerelease request rolls forward to the closest available`` () =
        installed [ "9.0.0-preview.5" ; "9.0.0" ]
        |> selectVersion (requesting RollForward.LatestPatch "9.0.0-preview.1")
        |> shouldEqual (Some "9.0.0-preview.5")

    /// hostfxr pushes onto its version list only what `fx_ver_t::parse` accepted, so a directory it
    /// cannot read is skipped rather than fatal.
    [<Test>]
    let ``an unparseable installed version is skipped`` () =
        installed [ "9.0.5" ; "not-a-version" ; "9.0" ; "" ]
        |> selectVersion (requesting RollForward.Minor "9.0.0")
        |> shouldEqual (Some "9.0.5")

    /// A version the host could not parse in the app's own runtimeconfig is the app's bug, and
    /// silently treating it as "no constraint" -- which is what hostfxr's release builds do, since it
    /// ignores the result of the parse -- would pick an arbitrary runtime.
    [<Test>]
    let ``an unparseable requested version is rejected`` () =
        let exn =
            Assert.Throws<FormatException> (fun () ->
                installed [ "9.0.5" ]
                |> selectVersion (requesting RollForward.Minor "9.0")
                |> ignore
            )

        exn.Message |> shouldContainText frameworkName
        exn.Message |> shouldContainText "9.0"

    /// All six policies over one environment, so that the compatibility range each of them selects is
    /// pinned in one place. The requested version is installed here, which is the case in which the
    /// ranges differ only by how far above it they are willing to look.
    [<Test>]
    let ``each policy looks exactly as far as its compatibility range`` () =
        let env = installed [ "10.0.7" ; "9.2.0" ; "9.1.5" ; "9.1.2" ; "9.1.0" ]

        let expected =
            [
                RollForward.Disable, Some "9.1.0"
                RollForward.LatestPatch, Some "9.1.5"
                RollForward.Minor, Some "9.1.5"
                RollForward.LatestMinor, Some "9.2.0"
                RollForward.Major, Some "9.1.5"
                RollForward.LatestMajor, Some "10.0.7"
            ]

        for policy, answer in expected do
            env |> selectVersion (requesting policy "9.1.0") |> shouldEqual answer

    /// The same six policies when the requested major is absent, which is the only case separating
    /// `Minor` from `Major` and `LatestMinor` from `LatestMajor`: the minor-ranged policies refuse to
    /// cross a major boundary, and the major-ranged ones do it.
    [<Test>]
    let ``only the major-ranged policies cross a major boundary`` () =
        let env = installed [ "10.0.7" ; "9.2.0" ; "9.1.5" ; "9.1.2" ]

        let expected =
            [
                RollForward.Disable, None
                RollForward.LatestPatch, None
                RollForward.Minor, None
                RollForward.LatestMinor, None
                RollForward.Major, Some "9.1.5"
                RollForward.LatestMajor, Some "10.0.7"
            ]

        for policy, answer in expected do
            env |> selectVersion (requesting policy "8.0.0") |> shouldEqual answer

    /// hostfxr's exact compatibility range never parses or compares versions: `fx_resolver.cpp`
    /// appends the requested version *string* to the framework directory and takes that directory or
    /// nothing. So `Disable` is spelled-out string equality, not precedence -- and the two differ
    /// exactly when build metadata is in play, since build metadata takes no part in precedence.
    [<Test>]
    let ``Disable matches the requested version's spelling, not its precedence`` () =
        // hostfxr would look for a directory named "9.0.5" and not find one.
        installed [ "9.0.5+custom" ]
        |> selectVersion (requesting RollForward.Disable "9.0.5")
        |> shouldEqual None

        // ... and here it finds "9.0.5" itself, not the one which merely ranks the same.
        installed [ "9.0.5+custom" ; "9.0.5" ]
        |> selectVersion (requesting RollForward.Disable "9.0.5")
        |> shouldEqual (Some "9.0.5")

    /// The same distinction when the requested version is itself carrying build metadata: the
    /// directory hostfxr looks for is the whole string, so an installed version of equal precedence
    /// listed earlier must not be taken in its place.
    [<Test>]
    let ``Disable picks the requested build, not one of equal precedence`` () =
        installed [ "9.0.5+other" ; "9.0.5+expected" ]
        |> selectVersion (requesting RollForward.Disable "9.0.5+expected")
        |> shouldEqual (Some "9.0.5+expected")

        installed [ "9.0.5+other" ]
        |> selectVersion (requesting RollForward.Disable "9.0.5+expected")
        |> shouldEqual None

    /// Every policy answers "nothing" for a framework with no installed version at all, rather than
    /// throwing on a missing dictionary key.
    [<TestCase(RollForward.Minor)>]
    [<TestCase(RollForward.Major)>]
    [<TestCase(RollForward.LatestPatch)>]
    [<TestCase(RollForward.LatestMinor)>]
    [<TestCase(RollForward.LatestMajor)>]
    [<TestCase(RollForward.Disable)>]
    let ``an uninstalled framework selects nothing`` (rollForward : RollForward) =
        DotnetEnvironmentInfo ("10.0.0", "0000000", [], [])
        |> selectVersion (requesting rollForward "9.0.0")
        |> shouldEqual None
