namespace WoofWare.DotnetRuntimeLocator.Test

open System
open System.IO
open System.Reflection
open System.Text.Json
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.DotnetRuntimeLocator

[<TestFixture>]
module TestRuntimeConfigParse =

    /// The host's property bag as a sorted assoc list, which — unlike a dictionary — F# compares
    /// structurally. `RuntimeOptions` deliberately does not compare its `ConfigProperties`
    /// structurally (see the remarks on that property), so tests must go through the projection.
    let private hostProperties (options : RuntimeOptions) : (string * string) list =
        ConfigProperties.ToHostStrings options.ConfigProperties
        |> Seq.map (fun (KeyValue (k, v)) -> k, v)
        |> Seq.sortBy fst
        |> List.ofSeq

    [<Test>]
    let ``Can parse our own runtime config`` () =
        let assy = Assembly.GetExecutingAssembly ()

        let runtimeConfig =
            Path.Combine (FileInfo(assy.Location).Directory.FullName, $"%s{assy.GetName().Name}.runtimeconfig.json")
            |> File.ReadAllText

        let actual = JsonSerializer.Deserialize<RuntimeConfig> runtimeConfig

        actual.RuntimeOptions.Tfm |> shouldEqual "net9.0"

        actual.RuntimeOptions.Framework
        |> shouldEqual (RuntimeConfigFramework (Name = "Microsoft.NETCore.App", Version = "9.0.0"))

        actual.RuntimeOptions.Frameworks |> shouldEqual null
        actual.RuntimeOptions.IncludedFrameworks |> shouldEqual null
        actual.RuntimeOptions.RollForward |> shouldEqual (Nullable ())

        // The exact set here is whatever the test SDK decided to bake in, so assert only the entry
        // the .NET SDK puts in every runtimeconfig.json rather than pinning the whole bag.
        hostProperties actual.RuntimeOptions
        |> List.contains ("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", "false")
        |> shouldEqual true

    [<Test>]
    let ``Example 1`` () =
        let content =
            Assembly.getEmbeddedResource (Assembly.GetExecutingAssembly ()) "runtimeconfig1.json"

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        actual.RuntimeOptions.Tfm |> shouldEqual "net8.0"
        actual.RuntimeOptions.RollForward |> shouldEqual (Nullable RollForward.Major)

        actual.RuntimeOptions.Framework
        |> shouldEqual (RuntimeConfigFramework (Name = "Microsoft.NETCore.App", Version = "8.0.0"))

        hostProperties actual.RuntimeOptions
        |> shouldEqual
            [
                "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", "false"
            ]

    /// Every JSON value kind, projected the way the .NET host projects it. The expected strings here
    /// were measured against the real host on .NET 10: an app was given exactly these
    /// `configProperties` and printed `AppContext.GetData` for each key. Every value comes back as a
    /// `System.String`, whatever its JSON type.
    [<Test>]
    let ``Example 2: every JSON value kind renders as the host renders it`` () =
        let content =
            Assembly.getEmbeddedResource (Assembly.GetExecutingAssembly ()) "runtimeconfig2.json"

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        actual.RuntimeOptions.Tfm |> shouldEqual "net10.0"

        hostProperties actual.RuntimeOptions
        |> shouldEqual
            [
                "Probe.ArrayVal", """[1,"two",true]"""
                "Probe.BoolFalse", "false"
                "Probe.BoolTrue", "true"
                "Probe.EmptyString", ""
                "Probe.IntVal", "42"
                "Probe.NegativeIntVal", "-7"
                "Probe.NullVal", "null"
                "Probe.ObjVal", """{"a":1,"b":"c"}"""
                "Probe.StringVal", "hello world"
            ]

    [<Test>]
    let ``A runtimeconfig with no configProperties projects to an empty bag`` () =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}"""

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        actual.RuntimeOptions.ConfigProperties |> shouldEqual null
        hostProperties actual.RuntimeOptions |> shouldEqual []

    [<Test>]
    let ``ToHostStrings of null is empty`` () =
        ConfigProperties.ToHostStrings null |> Seq.toList |> shouldEqual []

    /// A JSON string is handed to the runtime verbatim — in particular it is *not* re-escaped, so a
    /// value containing quotes, backslashes or non-ASCII arrives at `AppContext.GetData` unchanged.
    [<Test>]
    let ``String values pass through verbatim, without re-escaping`` () =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{"K":"a\"b\\cé😀<&>"}}}"""

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        hostProperties actual.RuntimeOptions
        |> shouldEqual [ "K", "a\"b\\cé\U0001F600<&>" ]

    /// A string *nested* inside an array or object goes through the host's JSON writer, unlike a
    /// top-level one. Every expectation here was measured against the real host on
    /// Microsoft.NETCore.App 10.0.7: it escapes only `"`, `\` and the C0 controls, so HTML-sensitive
    /// characters, DEL, Latin-1, U+2028, U+2029 and astral-plane characters all survive as
    /// themselves. This is precisely what the first version of this code got wrong by rendering
    /// through `Utf8JsonWriter`, whose default encoder escapes most of them.
    [<Test>]
    let ``Nested strings are escaped exactly as the host escapes them`` () =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{
                 "InArray": ["é<&>"],
                 "InObject": {"k": "é<&>"},
                 "AsKey": {"é<&>": 1},
                 "Quote": ["a\"b"],
                 "Backslash": ["a\\b"],
                 "Newline": ["a\nb"],
                 "VerticalTab": ["a\u000Bb"],
                 "Control": ["a\u0001b"],
                 "Del": ["a\u007Fb"],
                 "Astral": ["😀"],
                 "Separators": ["\u2028\u2029\u00A0"],
                 "Deep": {"a": [{"b": "é"}]}
               }}}"""

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        hostProperties actual.RuntimeOptions
        |> shouldEqual
            [
                "AsKey", """{"é<&>":1}"""
                "Astral", """["😀"]"""
                "Backslash", """["a\\b"]"""
                "Control", """["a\u0001b"]"""
                "Deep", """{"a":[{"b":"é"}]}"""
                // Not triple-quoted: these two expect the *literal* character in the output, since
                // the host does not escape it, so F# must process the escape rather than preserve it.
                "Del", "[\"a\u007Fb\"]"
                "InArray", """["é<&>"]"""
                "InObject", """{"k":"é<&>"}"""
                "Newline", """["a\nb"]"""
                "Quote", """["a\"b"]"""
                "Separators", "[\"\u2028\u2029\u00A0\"]"
                "VerticalTab", """["a\u000Bb"]"""
            ]

    /// The escape policy above, replayed against every code point it was measured at. The fixture
    /// records what `AppContext.GetData` actually returned in a real process for a config property
    /// whose value was `["<one character>"]`, for each of 291 sampled code points; see the
    /// "provenance" key in the file. This is the one oracle here which is not a restatement of the
    /// implementation's own table.
    [<Test>]
    let ``Every measured code point is escaped exactly as the real host escaped it`` () =
        let fixture =
            Assembly.getEmbeddedResource (Assembly.GetExecutingAssembly ()) "host-escape-sweep.json"
            |> JsonDocument.Parse

        let observations =
            fixture.RootElement.GetProperty "observations"
            |> fun o -> o.EnumerateObject () |> List.ofSeq

        // Guard against the fixture silently becoming empty or truncated, which would make every
        // assertion below vacuous.
        observations.Length |> shouldEqual 291

        let failures =
            observations
            |> List.choose (fun observation ->
                let codePoint = Convert.ToInt32 (observation.Name, 16)
                let expected = observation.Value.GetString ()

                // Rebuild the probe's input: an array holding just this character. Serializing it
                // with System.Text.Json keeps the *input* independent of the writer under test.
                let document = JsonSerializer.Serialize [| Char.ConvertFromUtf32 codePoint |]
                let actual = ConfigProperties.ToHostString (JsonDocument.Parse document).RootElement

                if actual = expected then
                    None
                else
                    Some (observation.Name, expected, actual)
            )

        failures |> shouldEqual []

    /// The host hands the runtime null-terminated strings, so an embedded NUL cuts a name or a
    /// top-level value short. All measured on Microsoft.NETCore.App 10.0.7.
    [<Test>]
    let ``An embedded NUL truncates a name or a top-level value`` () =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{
                 "Value": "a\u0000b",
                 "OnlyNul": "\u0000",
                 "Trailing": "abc\u0000",
                 "Nested": ["a\u0000b"],
                 "Key\u0000suffix": "keyed"
               }}}"""

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        hostProperties actual.RuntimeOptions
        |> shouldEqual
            [
                "Key", "keyed"
                // Escaped rather than embedded by the time it reaches the bag, so it survives whole.
                "Nested", """["a\u0000b"]"""
                "OnlyNul", ""
                "Trailing", "abc"
                "Value", "a"
            ]

    /// Two names which become equal once truncated collide, and the later one in the file wins.
    /// Measured in both declaration orders, so this pins the direction rather than assuming it.
    [<Test>]
    let ``Names colliding after NUL truncation resolve to the last one in the file`` () =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{
                 "Coll\u0000a": "first",
                 "Coll\u0000b": "second",
                 "Plain": "plain-first",
                 "Plain\u0000x": "truncated-second",
                 "Rev\u0000x": "truncated-first",
                 "Rev": "plain-second"
               }}}"""

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        hostProperties actual.RuntimeOptions
        |> shouldEqual [ "Coll", "second" ; "Plain", "truncated-second" ; "Rev", "plain-second" ]

    /// The host holds an integer token as a 64-bit integer where it fits and prints that back, so `-0`
    /// becomes "0"; outside that range it goes through a double, which is the documented divergence.
    /// The boundaries here are exact, and every expectation was measured.
    [<Test>]
    let ``Integers match the host inside 64 bits, and are pinned as divergent outside`` () =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{
                 "NegZero": -0,
                 "NegZeroNested": [-0],
                 "NegZeroInObject": {"k": -0},
                 "Int64Min": -9223372036854775808,
                 "UInt64Max": 18446744073709551615
               }}}"""

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        hostProperties actual.RuntimeOptions
        |> shouldEqual
            [
                "Int64Min", "-9223372036854775808"
                "NegZero", "0"
                "NegZeroInObject", """{"k":0}"""
                "NegZeroNested", "[0]"
                "UInt64Max", "18446744073709551615"
            ]

    /// The other side of that boundary. The host would render each of these through a double —
    /// measured as "1.5", "1.5", "0.1", "1000.0", "18446744073709552000.0" and
    /// "-9223372036854776000.0" respectively — via rapidjson's dtoa, which this library does not
    /// model. Rather than echo the token back and hope, it refuses.
    ///
    /// Note `1.5` is refused too, even though echoing it back would happen to be right. Whether a
    /// given token survives the round trip is precisely what the unmodelled dtoa decides, so the
    /// whole class goes: an API which is right except when it silently isn't is the worse one.
    [<TestCase("1.5")>]
    [<TestCase("1.50")>]
    [<TestCase("0.10")>]
    [<TestCase("1e3")>]
    [<TestCase("18446744073709551616")>]
    [<TestCase("-9223372036854775809")>]
    let ``A number the host would render through a double is refused`` (literal : string) =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{"K":"""
            + literal
            + "}}}"

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        let exn =
            Assert.Throws<ArgumentException> (fun () -> hostProperties actual.RuntimeOptions |> ignore)

        exn.Message |> shouldContainText literal
        exn.Message |> shouldContainText "dtoa"

    /// ... and the refusal reaches inside arrays and objects too, rather than only the top level.
    [<TestCase("""["a",1.5]""")>]
    [<TestCase("""{"k":1.5}""")>]
    [<TestCase("""[[[1.5]]]""")>]
    let ``A nested unmodellable number is refused too`` (literal : string) =
        let content =
            """{"runtimeOptions":{"tfm":"net8.0","configProperties":{"K":"""
            + literal
            + "}}}"

        let actual = DotnetRuntime.DeserializeRuntimeConfig content

        Assert.Throws<ArgumentException> (fun () -> hostProperties actual.RuntimeOptions |> ignore)
        |> ignore

    [<Test>]
    let ``ToHostString refuses an undefined element`` () =
        let exn =
            Assert.Throws<ArgumentException> (fun () -> ConfigProperties.ToHostString (JsonElement ()) |> ignore)

        exn.Message |> shouldContainText "undefined JsonElement"

    // ------------------------------------------------------------------
    // Property test: the projection against an independent renderer.
    // ------------------------------------------------------------------

    /// A JSON value, used both to build the input document and to compute the expected projection
    /// independently of the code under test.
    type private Json =
        | JString of string
        | JBool of bool
        | JNull
        | JInt of int
        | JArray of Json list
        | JObject of (string * Json) list

    /// Strings are drawn from fragments spanning every class the host's escaping policy distinguishes:
    /// plain ASCII, the two characters JSON must escape, the C0 controls both with and without a short
    /// form, DEL, Latin-1, the HTML-sensitive characters a JavaScript encoder would escape but the host
    /// does not, the JavaScript line terminators, and an astral-plane character.
    ///
    /// An earlier version of this generator was restricted to `[A-Za-z0-9 ._-]`, on the reasoning that
    /// the oracle should not have to reimplement an escaping policy to agree with it. That did not
    /// decouple the test from the policy; it deleted the only inputs which could detect getting the
    /// policy wrong, and it duly hid a real bug. Fragments are whole strings rather than chars so that
    /// the astral one contributes a complete surrogate pair: a lone surrogate cannot appear in JSON,
    /// and the serialiser would substitute U+FFFD for it.
    let private genSafeString : Gen<string> =
        [
            "a"
            "Z"
            "0"
            " "
            "."
            "-"
            "_"
            "\""
            "\\"
            "/"
            "'"
            "+"
            "<"
            "&"
            ">"
            "\b"
            "\t"
            "\n"
            "\f"
            "\r"
            "\u000B"
            "\u0000"
            "\u0001"
            "\u001F"
            "\u007F"
            "é"
            "\u00A0"
            "\u2028"
            "\u2029"
            "\U0001F600"
        ]
        |> Gen.elements
        |> Gen.listOf
        |> Gen.map (String.concat "")

    let private genJson : Gen<Json> =
        let rec go (size : int) : Gen<Json> =
            let leaves =
                [
                    genSafeString |> Gen.map JString
                    ArbMap.defaults |> ArbMap.generate<bool> |> Gen.map JBool
                    Gen.constant JNull
                    ArbMap.defaults |> ArbMap.generate<int> |> Gen.map JInt
                ]

            if size <= 0 then
                Gen.oneof leaves
            else
                let smaller = go (size / 2)

                Gen.oneof (
                    leaves
                    @ [
                        smaller |> Gen.listOf |> Gen.map JArray
                        Gen.zip genSafeString smaller
                        |> Gen.listOf
                        // Distinct keys: a JSON object with a repeated key is not a thing any
                        // producer emits, and what a parser does with one is not this code's problem.
                        |> Gen.map (List.distinctBy fst >> JObject)
                    ]
                )

        Gen.sized go

    /// Distinct keys, so that the generated object has exactly as many entries as the list.
    let private genConfigProperties : Gen<(string * Json) list> =
        Gen.zip (genSafeString |> Gen.filter (fun s -> s <> "")) genJson
        |> Gen.listOf
        |> Gen.map (List.distinctBy fst)

    /// Quote a string the way the *host* quotes it: only `"`, `\` and the C0 controls are escaped,
    /// with the short forms preferred and `\u00XX` in uppercase hex otherwise.
    ///
    /// This restates the policy the implementation applies, so on its own it would be circular. It is
    /// not on its own: `Every measured code point is escaped exactly as the real host escaped it`
    /// checks that same policy against 291 observations of a real .NET process, so a mistake here
    /// would have to be duplicated there, where the expectations are not written by hand at all.
    let private hostQuote (s : string) : string =
        let builder = Text.StringBuilder ()

        builder.Append '"' |> ignore

        for c in s do
            match c with
            | '"' -> builder.Append "\\\"" |> ignore
            | '\\' -> builder.Append "\\\\" |> ignore
            | '\b' -> builder.Append "\\b" |> ignore
            | '\f' -> builder.Append "\\f" |> ignore
            | '\n' -> builder.Append "\\n" |> ignore
            | '\r' -> builder.Append "\\r" |> ignore
            | '\t' -> builder.Append "\\t" |> ignore
            | c when c < ' ' -> builder.Append $"\\u%04X{int c}" |> ignore
            | c -> builder.Append c |> ignore

        builder.Append '"' |> ignore
        builder.ToString ()

    /// Render compactly, as the host does. This is the oracle for what the projection should produce.
    let rec private render (json : Json) : string =
        match json with
        | JString s -> hostQuote s
        | JBool true -> "true"
        | JBool false -> "false"
        | JNull -> "null"
        | JInt i -> string<int> i
        | JArray items -> items |> List.map render |> String.concat "," |> sprintf "[%s]"
        | JObject fields ->
            fields
            |> List.map (fun (k, v) -> $"%s{hostQuote k}:%s{render v}")
            |> String.concat ","
            |> sprintf "{%s}"

    /// Render with insignificant whitespace, for building the *input* document. Without this the
    /// generated input would already be compact, and the property could not tell a projection which
    /// compacts (correct) from one which echoes the file's raw text (wrong).
    ///
    /// Strings are quoted by `System.Text.Json` rather than by `hostQuote`. Any valid JSON escaping
    /// parses back to the same string, so this is free to differ from the expectation's quoting — and
    /// it should differ, so that the input is not built by the very policy under test.
    let rec private renderSpaced (json : Json) : string =
        match json with
        | JString s -> JsonSerializer.Serialize s
        | JArray items -> items |> List.map renderSpaced |> String.concat ", " |> sprintf "[ %s ]"
        | JObject fields ->
            fields
            |> List.map (fun (k, v) -> $"%s{JsonSerializer.Serialize k} : %s{renderSpaced v}")
            |> String.concat ", "
            |> sprintf "{ %s }"
        | leaf -> render leaf

    /// The host's property bag is transported as null-terminated strings, so a name or a top-level
    /// value stops at its first NUL. A *nested* string does not, having been escaped to backslash-u-0000 text
    /// by then — which is why `render` above does not do this.
    let private truncateAtNul (s : string) : string =
        match s.IndexOf '\000' with
        | -1 -> s
        | i -> s.Substring (0, i)

    /// What the host's property bag should contain for this value: strings verbatim up to any NUL,
    /// everything else as its compact JSON text.
    let private expectedHostString (json : Json) : string =
        match json with
        | JString s -> truncateAtNul s
        | other -> render other

    [<Test>]
    let ``ToHostStrings preserves every key and renders every value as the host would`` () =
        let property (properties : (string * Json) list) : bool =
            let document =
                properties
                |> List.map (fun (k, v) -> $"%s{JsonSerializer.Serialize k} : %s{renderSpaced v}")
                |> String.concat ", "
                |> sprintf """{"runtimeOptions":{"tfm":"net8.0","configProperties":{ %s }}}"""

            let actual =
                DotnetRuntime.DeserializeRuntimeConfig document
                |> fun c -> hostProperties c.RuntimeOptions

            // Folding into a Map in document order models both halves of the name rule: a name is
            // truncated at its first NUL, and two names which collide once truncated resolve to the
            // later one in the file.
            let expected =
                properties
                |> List.fold (fun acc (k, v) -> Map.add (truncateAtNul k) (expectedHostString v) acc) Map.empty
                |> Map.toList

            actual = expected

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 500, Prop.forAll (Arb.fromGen genConfigProperties) property)
