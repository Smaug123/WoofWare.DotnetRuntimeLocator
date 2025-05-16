using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     The type of a "framework" entry in the "runtimeOptions" setting of a runtimeconfig.json file.
/// </summary>
public record RuntimeConfigFramework
{
    /// <summary>
    ///     For example, "Microsoft.NETCore.App".
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    ///     For example, "9.0.0".
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
///     The value of e.g. `--roll-forward` or DOTNET_ROLL_FORWARD.
/// </summary>
public enum RollForward
{
    /// <summary>
    ///     If the requested version is missing, roll forward to the lowest available minor version higher than requested.
    ///     If the requested version is available, silently use the LatestPatch policy.
    ///     Minor is the default if unspecified.
    /// </summary>
    Minor,

    /// <summary>
    ///     If the requested version is missing, roll forward to the lowest available major version higher than requested,
    ///     at "lowest minor version" (the docs are unclear whether this means "lowest *available*", or "0").
    ///     If the requested version is available, silently use the Minor policy.
    /// </summary>
    Major,

    /// <summary>
    ///     Roll forward to the highest patch version at exactly the requested major and minor versions.
    /// </summary>
    LatestPatch,

    /// <summary>
    ///     Roll forward to the highest minor version, even if the requested minor version is available.
    /// </summary>
    LatestMinor,

    /// <summary>
    ///     Roll forward to the highest available major version and highest available minor version at that major version,
    ///     even if the requested version is available.
    /// </summary>
    LatestMajor,

    /// <summary>
    ///     Suppress all rolling forward: use only the exact specified version.
    /// </summary>
    Disable
}

/// <summary>
///     The contents of the "runtimeOptions" key in a runtimeconfig.json file.
/// </summary>
public record RuntimeOptions
{
    /// Target framework moniker, such as "net9.0".
    [JsonPropertyName("tfm")]
    public required string Tfm { get; init; }

    /// <summary>
    ///     The .NET runtime which this executable expects.
    ///     This is optional, because you can instead specify multiple Frameworks, in which case any of the frameworks
    ///     is acceptable (according to Claude; the MS docs are impenetrable as ever).
    /// </summary>
    [JsonPropertyName("framework")]
    public RuntimeConfigFramework? Framework { get; init; }

    /// <summary>
    ///     Any of these runtimes by itself would be enough to run this executable.
    ///     It's much more normal to see a single `framework` instead of this.
    /// </summary>
    [JsonPropertyName("frameworks")]
    public IReadOnlyList<RuntimeConfigFramework>? Frameworks { get; init; }

    /// <summary>
    ///     This application advertises that it's fine with running under this roll-forward.
    /// </summary>
    [JsonPropertyName("rollForward")]
    public RollForward? RollForward { get; init; }
}

/// <summary>
///     The contents of a runtimeconfig.json file.
///     Note that this record doesn't capture everything: for example, "configProperties" might be present in the file,
///     but is not represented in this type.
/// </summary>
public record RuntimeConfig
{
    /// <summary>
    ///     The contents of the file.
    /// </summary>
    [JsonPropertyName("runtimeOptions")]
    public required RuntimeOptions RuntimeOptions { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RuntimeConfig))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}
