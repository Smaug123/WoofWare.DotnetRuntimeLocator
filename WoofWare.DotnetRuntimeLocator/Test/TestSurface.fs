namespace WoofWare.DotnetRuntimeLocator.Test

open NUnit.Framework
open ApiSurface

[<TestFixture>]
module TestSurface =
    let assembly = typeof<WoofWare.DotnetRuntimeLocator.DotnetEnvironmentInfo>.Assembly

    [<Test>]
    let ``Ensure API surface has not been modified`` () = ApiSurface.assertIdentical assembly

    [<Test ; Explicit>]
    let ``Update API surface`` () =
        ApiSurface.writeAssemblyBaseline assembly

    [<Test>]
    let ``Ensure public API is fully documented`` () =
        DocCoverage.assertFullyDocumented assembly

    [<Test ; Explicit "Not yet published">]
    let ``Ensure version is monotonic`` () =
        MonotonicVersion.validate assembly "WoofWare.DotnetRuntimeLocator"
