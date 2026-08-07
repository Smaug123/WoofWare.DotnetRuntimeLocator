namespace WoofWare.DotnetRuntimeLocator.Test

open System
open System.IO
open System.Reflection
open FsUnitTyped
open NUnit.Framework
open WoofWare.DotnetRuntimeLocator

[<TestFixture>]
module TestDotnetRuntime =

    let inline shouldBeSome (x : 'a option) : unit =
        match x with
        | None -> failwith "option was None"
        | Some _ -> ()

    let inline shouldBeNone (x : 'a option) : unit =
        match x with
        | Some x -> failwith $"expected None, but option was Some %O{x}"
        | None -> ()

    [<Test>]
    let ``Test DotnetRuntime`` () =
        let assy = Assembly.GetExecutingAssembly ()
        let selectedRuntime = DotnetRuntime.SelectForDll assy.Location

        let existsDll (name : string) =
            selectedRuntime
            |> Seq.tryPick (fun dir ->
                let attempt = Path.Combine (dir, name)
                if File.Exists attempt then Some attempt else None
            )

        existsDll "System.Private.CoreLib.dll" |> shouldBeSome
        existsDll "System.Text.Json.dll" |> shouldBeSome
        existsDll "Test.dll" |> shouldBeSome
        existsDll "blah-de-blah.dll" |> shouldBeNone

    [<Test>]
    let ``RuntimeConfigPathForDll finds the file SelectForDll reads`` () =
        let assy = Assembly.GetExecutingAssembly ()
        let path = DotnetRuntime.RuntimeConfigPathForDll assy.Location

        Path.GetFileName path |> shouldEqual "Test.runtimeconfig.json"

        Path.GetDirectoryName path
        |> shouldEqual (FileInfo(assy.Location).Directory.FullName)

        File.Exists path |> shouldEqual true

    /// The file is not required to exist: callers who want to tolerate its absence need to be able to
    /// ask where it would be without being thrown at.
    [<Test>]
    let ``RuntimeConfigPathForDll does not require the file to exist`` () =
        let path =
            DotnetRuntime.RuntimeConfigPathForDll (Path.Combine (Path.GetTempPath (), "no-such-app.dll"))

        Path.GetFileName path |> shouldEqual "no-such-app.runtimeconfig.json"
        File.Exists path |> shouldEqual false

    [<Test>]
    let ``RuntimeConfigPathForDll rejects a non-dll`` () =
        let exn =
            Assert.Throws<ArgumentException> (fun () ->
                DotnetRuntime.RuntimeConfigPathForDll "/tmp/thing.exe" |> ignore<string>
            )

        exn.Message |> shouldContainText "extension '.dll'"
