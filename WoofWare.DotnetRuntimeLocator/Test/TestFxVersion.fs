namespace WoofWare.DotnetRuntimeLocator.Test

open System
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.DotnetRuntimeLocator

/// The oracle is hostfxr's `fx_ver_t` (`src/native/corehost/hostmisc/fx_ver.c`): `parse_internal` for
/// what is a version at all, and `c_fx_ver_compare` for how two of them order. Both implement SemVer
/// 2.0, so the specification's own worked example serves as a second, independent oracle for ordering.
[<TestFixture>]
module TestFxVersion =

    let private parse (s : string) : FxVersion option =
        match FxVersion.TryParse s with
        | true, v -> Some v
        | false, _ -> None

    let private parsed (s : string) : FxVersion =
        match parse s with
        | Some v -> v
        | None -> failwith $"expected '%s{s}' to parse as a version"

    [<TestCase("0.0.0", 0, 0, 0)>]
    [<TestCase("9.0.5", 9, 0, 5)>]
    [<TestCase("11.0.0-preview.7.26381.103", 11, 0, 0)>]
    [<TestCase("1.2.3+build", 1, 2, 3)>]
    [<TestCase("1.2.3-a.1+b.2", 1, 2, 3)>]
    [<TestCase("2147483647.0.0", 2147483647, 0, 0)>]
    let ``accepts the versions hostfxr accepts`` (s : string, major : int, minor : int, patch : int) =
        let v = parsed s
        (v.Major, v.Minor, v.Patch) |> shouldEqual (major, minor, patch)

    /// Every one of these is rejected by `parse_internal`, and most of them are versions
    /// `System.Version` would have accepted or mangled.
    [<TestCase("", Description = "empty")>]
    [<TestCase("1", Description = "no dot at all")>]
    [<TestCase("1.2", Description = "two components")>]
    [<TestCase("1.2.3.4", Description = "four components")>]
    [<TestCase("01.2.3", Description = "leading zero in major")>]
    [<TestCase("1.02.3", Description = "leading zero in minor")>]
    [<TestCase("1.2.03", Description = "leading zero in patch")>]
    [<TestCase("1.2.", Description = "empty patch")>]
    [<TestCase(".2.3", Description = "empty major")>]
    [<TestCase("1..3", Description = "empty minor")>]
    [<TestCase("1.2.3-", Description = "prerelease marker with no label")>]
    [<TestCase("1.2.3+", Description = "build marker with no label")>]
    [<TestCase("1.2.3-01", Description = "leading zero in a numeric prerelease identifier")>]
    [<TestCase("1.2.3-a..b", Description = "empty prerelease identifier")>]
    [<TestCase("1.2.3-a.", Description = "trailing empty prerelease identifier")>]
    [<TestCase("1.2.3-a_b", Description = "prerelease identifier outside [0-9A-Za-z-]")>]
    [<TestCase("1.2.3+a..b", Description = "empty build identifier")>]
    [<TestCase("1.2.3abc", Description = "prerelease label without its '-'")>]
    [<TestCase("1.2.-3", Description = "no digits before the prerelease label")>]
    [<TestCase("-1.2.3", Description = "negative major")>]
    [<TestCase("1.2.3 ", Description = "trailing space")>]
    [<TestCase(" 1.2.3", Description = "leading space")>]
    let ``rejects the versions hostfxr rejects`` (s : string) = parse s |> shouldEqual None

    /// hostfxr feeds each numeric component to `strtoul` without checking for overflow, so it reads
    /// `4294967296.0.0` as major 0 and a larger value as major -1, which its own `is_empty()` then
    /// reports as "no version at all". We refuse instead; see the remarks on `FxVersion`.
    [<TestCase("4294967296.0.0")>]
    [<TestCase("2147483648.0.0")>]
    [<TestCase("99999999999999999999999999999999999.0.0")>]
    let ``refuses a numeric component too large for an int`` (s : string) = parse s |> shouldEqual None

    /// A leading zero disqualifies only an identifier which is *entirely* numeric, and build metadata
    /// is exempt altogether.
    [<TestCase("1.2.3-0a")>]
    [<TestCase("1.2.3-0-1")>]
    [<TestCase("1.2.3+01")>]
    [<TestCase("1.2.3+0.00.000")>]
    let ``leading zeros are allowed where SemVer allows them`` (s : string) =
        parse s |> Option.isSome |> shouldEqual true

    /// https://semver.org/#spec-item-11, verbatim. hostfxr implements exactly this.
    let private semverExample =
        [
            "1.0.0-alpha"
            "1.0.0-alpha.1"
            "1.0.0-alpha.beta"
            "1.0.0-beta"
            "1.0.0-beta.2"
            "1.0.0-beta.11"
            "1.0.0-rc.1"
            "1.0.0"
        ]

    [<Test>]
    let ``orders the SemVer specification's own example`` () =
        let versions = semverExample |> List.map parsed

        for i in 0 .. versions.Length - 1 do
            for j in 0 .. versions.Length - 1 do
                let expected = compare i j
                let actual = sign (versions.[i].CompareTo versions.[j])

                if actual <> expected then
                    failwith
                        $"expected %s{semverExample.[i]} to compare %i{expected} against %s{semverExample.[j]}, got %i{actual}"

    [<Test>]
    let ``a release outranks every prerelease of the same three components`` () =
        parsed "1.0.0" |> shouldBeGreaterThan (parsed "1.0.0-rc.99")
        parsed "1.0.0" |> shouldBeSmallerThan (parsed "1.0.1-alpha")

    /// `c_fx_ver_compare` never looks at the build label, so two versions differing only there have
    /// the same precedence -- and hostfxr's `operator==` is that comparison, so they are equal.
    [<Test>]
    let ``build metadata takes no part in ordering or equality`` () =
        let bare = parsed "1.2.3"
        let withBuild = parsed "1.2.3+exp.sha.5114f85"
        let otherBuild = parsed "1.2.3+other"

        bare |> shouldEqual withBuild
        withBuild |> shouldEqual otherBuild
        withBuild.CompareTo otherBuild |> shouldEqual 0
        bare.GetHashCode () |> shouldEqual (withBuild.GetHashCode ())

    /// The prerelease label is kept with its leading '-' and the build label with its leading '+',
    /// exactly as hostfxr stores them, so rendering is concatenation.
    [<Test>]
    let ``renders what it parsed`` () =
        (parsed "11.0.0-preview.7.26381.103").ToString ()
        |> shouldEqual "11.0.0-preview.7.26381.103"

        (parsed "1.2.3+b").ToString () |> shouldEqual "1.2.3+b"
        (parsed "1.2.3").Pre |> shouldEqual ""
        (parsed "11.0.0-preview.7").Pre |> shouldEqual "-preview.7"
        (parsed "1.2.3+b").Build |> shouldEqual "+b"

    /// hostfxr decides "is this identifier numeric?" with `try_stou`, which copies into a 32-character
    /// buffer and refuses anything longer. A 32-digit identifier is therefore not numeric to it, and so
    /// ranks as an alphanumeric one -- above every numeric identifier, rather than below them all.
    [<Test>]
    let ``an identifier too long for hostfxr's buffer ranks as alphanumeric`` () =
        let thirtyOne = String ('9', 31)
        let thirtyTwo = String ('1', 32)

        // 31 digits is still numeric, so it ranks below any alphanumeric identifier.
        parsed $"1.0.0-%s{thirtyOne}" |> shouldBeSmallerThan (parsed "1.0.0-a")
        // 32 digits is not, so it ranks above every numeric one despite being all digits.
        parsed $"1.0.0-%s{thirtyTwo}"
        |> shouldBeGreaterThan (parsed $"1.0.0-%s{thirtyOne}")

        parsed $"1.0.0-%s{thirtyTwo}" |> shouldBeSmallerThan (parsed "1.0.0-b")

    /// hostfxr's own test corpus, ported from `src/native/corehost/test/fx_ver/test_fx_ver.cpp`.
    /// Each row is the version string, its three components, its prerelease and build labels, and
    /// whether it has the same precedence as the row above -- which is how the file records that build
    /// metadata does not affect ordering. The rows are in ascending precedence order.
    let private hostfxrOrderedCases =
        [
            "1.0.0-0.3.7", (1, 0, 0), "-0.3.7", "", false
            "1.0.0-alpha", (1, 0, 0), "-alpha", "", false
            "1.0.0-alpha+001", (1, 0, 0), "-alpha", "+001", true
            "1.0.0-alpha.1", (1, 0, 0), "-alpha.1", "", false
            "1.0.0-alpha.beta", (1, 0, 0), "-alpha.beta", "", false
            "1.0.0-beta", (1, 0, 0), "-beta", "", false
            "1.0.0-beta+exp.sha.5114f85", (1, 0, 0), "-beta", "+exp.sha.5114f85", true
            "1.0.0-beta.2", (1, 0, 0), "-beta.2", "", false
            "1.0.0-beta.11", (1, 0, 0), "-beta.11", "", false
            "1.0.0-rc.1", (1, 0, 0), "-rc.1", "", false
            "1.0.0-x.7.z.92", (1, 0, 0), "-x.7.z.92", "", false
            "1.0.0", (1, 0, 0), "", "", false
            "1.0.0+20130313144700", (1, 0, 0), "", "+20130313144700", true
            "1.9.0-9", (1, 9, 0), "-9", "", false
            "1.9.0-10", (1, 9, 0), "-10", "", false
            "1.9.0-1A", (1, 9, 0), "-1A", "", false
            "1.9.0", (1, 9, 0), "", "", false
            "1.10.0", (1, 10, 0), "", "", false
            "1.11.0", (1, 11, 0), "", "", false
            "2.0.0", (2, 0, 0), "", "", false
            "2.1.0", (2, 1, 0), "", "", false
            "2.1.1", (2, 1, 1), "", "", false
            "4.6.0-preview.19064.1", (4, 6, 0), "-preview.19064.1", "", false
            "4.6.0-preview1-27018-01", (4, 6, 0), "-preview1-27018-01", "", false
        ]

    [<Test>]
    let ``agrees with hostfxr's own parsing corpus`` () =
        for str, (major, minor, patch), pre, build, _ in hostfxrOrderedCases do
            let v = parsed str
            (v.Major, v.Minor, v.Patch) |> shouldEqual (major, minor, patch)
            v.Pre |> shouldEqual pre
            v.Build |> shouldEqual build
            v.IsPrerelease |> shouldEqual (pre <> "")
            // `checkParsing` asserts `as_str() == str` for every row.
            v.ToString () |> shouldEqual str

    /// hostfxr's `checkPrecedence`, which compares every row against every other. A row flagged "same"
    /// shares the rank of the row above it, so the two must compare equal in both directions.
    [<Test>]
    let ``agrees with hostfxr's own precedence corpus`` () =
        // Rank each row as hostfxr does: its index less the number of "same" rows at or before it.
        let rows =
            hostfxrOrderedCases
            |> List.indexed
            |> List.scan
                (fun (_, _, sameSoFar) (i, (str, _, _, _, same)) ->
                    let sameSoFar = if same then sameSoFar + 1 else sameSoFar
                    (str, i - sameSoFar, sameSoFar)
                )
                ("", 0, 0)
            |> List.tail
            |> List.map (fun (str, rank, _) -> str, rank)

        for iStr, iRank in rows do
            for jStr, jRank in rows do
                let a = parsed iStr
                let b = parsed jStr
                let expected = compare iRank jRank

                if sign (a.CompareTo b) <> expected then
                    failwith $"expected %s{iStr} to compare %i{expected} against %s{jStr}"

                (a = b) |> shouldEqual (expected = 0)
                (a < b) |> shouldEqual (expected < 0)
                (a > b) |> shouldEqual (expected > 0)
                (a <= b) |> shouldEqual (expected <= 0)
                (a >= b) |> shouldEqual (expected >= 0)

    /// hostfxr's `checkInvalidVersions`, ported verbatim.
    [<TestCase("")>]
    [<TestCase("1")>]
    [<TestCase("1.1")>]
    [<TestCase("A.1.1")>]
    [<TestCase("1.A.1")>]
    [<TestCase("1.1.A")>]
    [<TestCase("1A.1.1")>]
    [<TestCase("1.1A.1")>]
    [<TestCase("1.1.1A")>]
    [<TestCase("1.1.1-")>]
    [<TestCase("1.1.1-.")>]
    [<TestCase("1.1.1-A.")>]
    [<TestCase("1.1.1-A.B.")>]
    [<TestCase("1.1.1-.+id")>]
    [<TestCase("1.1.1-A.+id")>]
    [<TestCase("1.1.1-A.B.+id")>]
    [<TestCase("1.1.1-A.B+id.")>]
    [<TestCase("01.1.1")>]
    [<TestCase("1.01.1")>]
    [<TestCase("1.1.01")>]
    [<TestCase("1.1.1-01.B")>]
    [<TestCase("1.1.1-A.01")>]
    [<TestCase("00.1.1")>]
    [<TestCase("1.00.1")>]
    [<TestCase("1.1.00")>]
    [<TestCase("1.1.00-A")>]
    [<TestCase("1.1.1-00.B")>]
    [<TestCase("1.1.1-A.00")>]
    [<TestCase("1.1.1+")>]
    [<TestCase("1.1.1-A+")>]
    [<TestCase("1.1.1-A*B")>]
    [<TestCase("1.1.1-A/B")>]
    [<TestCase("1.1.1-A:B")>]
    [<TestCase("1.1.1-A^B")>]
    [<TestCase("1.1.1-A|B")>]
    let ``rejects everything in hostfxr's invalid corpus`` (s : string) = parse s |> shouldEqual None

    let private genIdentifier : Gen<string> =
        Gen.oneof
            [
                // A numeric identifier, which SemVer forbids from carrying a leading zero.
                Gen.choose (0, 5000) |> Gen.map string
                Gen.elements [ "alpha" ; "beta" ; "rc" ; "preview" ; "a" ; "z0" ; "0a" ; "-" ; "x-y" ]
            ]

    let private genLabel (marker : string) : Gen<string> =
        gen {
            let! count = Gen.choose (0, 3)

            if count = 0 then
                return ""
            else
                let! ids = Gen.listOfLength count genIdentifier
                return marker + String.Join (".", ids)
        }

    let private genVersionString : Gen<string> =
        gen {
            let! major = Gen.choose (0, 20)
            let! minor = Gen.choose (0, 20)
            let! patch = Gen.choose (0, 20)
            let! pre = genLabel "-"
            let! build = genLabel "+"
            return $"%i{major}.%i{minor}.%i{patch}%s{pre}%s{build}"
        }

    /// Whatever we accept, we render back to the string we were given, and that string parses to an
    /// equal version. hostfxr's `as_str` is the same concatenation, so a version which round-trips
    /// here is one hostfxr would recognise in a framework directory name.
    [<Test>]
    let ``parsing round-trips through rendering`` () =
        let property (s : string) : unit =
            let v = parsed s
            v.ToString () |> shouldEqual s
            parsed (v.ToString ()) |> shouldEqual v

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 500, Prop.forAll (Arb.fromGen genVersionString) property)

    /// Comparison is a total order: it is antisymmetric, and sorting by it agrees with it pairwise.
    [<Test>]
    let ``comparison is a total order`` () =
        let property (versions : string list) : unit =
            let parsedVersions = versions |> List.map parsed

            for a in parsedVersions do
                for b in parsedVersions do
                    sign (a.CompareTo b) |> shouldEqual (-(sign (b.CompareTo a)))

            let sorted = parsedVersions |> List.sortWith (fun a b -> a.CompareTo b)

            for i in 0 .. sorted.Length - 2 do
                sorted.[i].CompareTo sorted.[i + 1] |> shouldBeSmallerThan 1

        let arb = Arb.fromGen (Gen.listOf genVersionString)
        Check.One (Config.QuickThrowOnFailure.WithMaxTest 200, Prop.forAll arb property)

    /// The message has to name the framework, because a runtimeconfig can request several and the
    /// version alone would not say which one was wrong.
    [<Test>]
    let ``Parse reports which subject had the bad version`` () =
        let exn =
            Assert.Throws<FormatException> (fun () ->
                FxVersion.Parse ("not-a-version", "framework 'Microsoft.NETCore.App'") |> ignore
            )

        exn.Message |> shouldContainText "Microsoft.NETCore.App"
        exn.Message |> shouldContainText "not-a-version"
