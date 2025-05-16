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

    [<Test ; Explicit "Bug in ApiSurface: https://github.com/G-Research/ApiSurface/pull/111">]
    let ``Ensure public API is fully documented`` () =
        DocCoverage.assertFullyDocumented assembly

    [<Test>]
    // https://github.com/nunit/nunit3-vs-adapter/issues/876
    let ``EnsureVersionIsMonotonic`` () =
        MonotonicVersion.validate assembly "WoofWare.DotnetRuntimeLocator"
