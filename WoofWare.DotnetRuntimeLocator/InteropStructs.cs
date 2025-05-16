using System;
using System.Runtime.InteropServices;

namespace WoofWare.DotnetRuntimeLocator;

internal static class InteropStructs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct DotnetEnvironmentSdkInfoNative
    {
        public nuint size;
        public string version;
        public string path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct DotnetEnvironmentFrameworkInfoNative
    {
        public nuint size;
        public string name;
        public string version;
        public string path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct DotnetEnvironmentInfoNative
    {
        public nuint size;
        public string hostfxr_version;
        public string hostfxr_commit_hash;

        public nuint sdk_count;

        /// <summary>
        ///     Pointer to an array of DotnetEnvironmentSdkInfoNative, of length `sdk_count`
        /// </summary>
        public IntPtr sdks;

        public nuint framework_count;

        /// <summary>
        ///     Pointer to an array of DotnetEnvironmentFrameworkInfoNative, of length `framework_count`
        /// </summary>
        public IntPtr frameworks;
    }
}
