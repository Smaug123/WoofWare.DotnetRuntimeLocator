namespace WoofWare.DotnetRuntimeLocator.Test

open System.IO
open System.Reflection
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
