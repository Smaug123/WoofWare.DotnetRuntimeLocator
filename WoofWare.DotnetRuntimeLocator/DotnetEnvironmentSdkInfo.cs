using System.IO;
using System.Runtime.InteropServices;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     Information about a single instance of the .NET SDK.
/// </summary>
/// <param name="Path">
///     The path to this SDK, e.g. "/usr/bin/dotnet/sdk/8.0.300" (I'm guessing at the prefix there, I use
///     Nix so my paths are different)
/// </param>
/// <param name="Version">e.g. "8.0.300"</param>
public record DotnetEnvironmentSdkInfo(string Path, string Version)
{
    internal static DotnetEnvironmentSdkInfo FromNative(InteropStructs.DotnetEnvironmentSdkInfoNative native)
    {
        if (native.size < 0 || native.size > int.MaxValue)
            throw new InvalidDataException("size field did not fit in an int");

        var size = (int)native.size;
        if (size != Marshal.SizeOf(native))
            throw new InvalidDataException($"size field {size} did not match expected size {Marshal.SizeOf(native)}");

        return new DotnetEnvironmentSdkInfo(native.path, native.version);
    }
}
