using System.IO;
using System.Runtime.InteropServices;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     Information about a single instance of the .NET runtime.
/// </summary>
/// <param name="Name">The name of this runtime, e.g. "Microsoft.NETCore.App"</param>
/// <param name="Path">
///     The path to this runtime, e.g. "/usr/bin/dotnet/shared/Microsoft.AspNetCore.App" (I'm guessing at
///     the prefix here, I use Nix so my paths are all different)
/// </param>
/// <param name="Version">The version of this runtime, e.g. "8.0.5"</param>
public record DotnetEnvironmentFrameworkInfo(string Name, string Path, string Version)
{
    internal static DotnetEnvironmentFrameworkInfo FromNative(
        InteropStructs.DotnetEnvironmentFrameworkInfoNative native)
    {
        if (native.size < 0 || native.size > int.MaxValue)
            throw new InvalidDataException("size field did not fit in an int");

        var size = (int)native.size;
        if (size != Marshal.SizeOf(native))
            throw new InvalidDataException($"size field {size} did not match expected size {Marshal.SizeOf(native)}");

        return new DotnetEnvironmentFrameworkInfo(native.name, native.path, native.version);
    }
}
