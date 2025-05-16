using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
/// The result of a call to `DotnetRuntime.Select`.
/// This is `type DotnetRuntimeSelection = | Framework of DotnetEnvironmentFrameworkInfo | Sdk of DotnetEnvironmentSdkInfo | Absent`.
/// </summary>
internal class DotnetRuntimeSelection
{
    private readonly DotnetEnvironmentFrameworkInfo? _framework;
    private readonly DotnetEnvironmentSdkInfo? _sdk;

    private readonly int _discriminator;

    /// <summary>
    /// The constructor which means "We found the right runtime, and it's from this framework".
    /// </summary>
    /// <param name="framework">For example, </param>
    public DotnetRuntimeSelection(DotnetEnvironmentFrameworkInfo framework)
    {
        _discriminator = 1;
        _framework = framework;
    }

    /// <summary>
    /// The constructor which means "We found the right runtime, and it's from this SDK".
    /// </summary>
    /// <param name="sdk">For example, </param>
    public DotnetRuntimeSelection(DotnetEnvironmentSdkInfo sdk)
    {
        _discriminator = 2;
        _sdk = sdk;
    }

    /// <summary>
    /// The constructor which means "We were unable to find an appropriate runtime".
    /// </summary>
    public DotnetRuntimeSelection()
    {
        _discriminator = 3;
    }

    /// <summary>
    /// Exhaustive match on this discriminated union.
    /// </summary>
    /// <param name="withFramework">If `this` is a `Framework`, call this continuation with its value.</param>
    /// <param name="withSdk">If `this` is a `Sdk`, call this continuation with its value.</param>
    /// <param name="withNone">If `this` represents the absence of a result, call this continuation.</param>
    /// <returns>The result of the continuation which was called.</returns>
    public TRet Visit<TRet>(Func<DotnetEnvironmentFrameworkInfo, TRet> withFramework,
        Func<DotnetEnvironmentSdkInfo, TRet> withSdk,
        Func<TRet> withNone)
    {
        return _discriminator switch
        {
            1 => withFramework.Invoke(_framework!),
            2 => withSdk.Invoke(_sdk!),
            3 => withNone.Invoke(),
            _ => throw new InvalidOperationException($"unrecognised union discriminator %i{_discriminator}")
        };
    }
}

/// <summary>
/// Module to hold methods for automatically identifying a .NET runtime.
/// </summary>
public static class DotnetRuntime
{
    private record RuntimeOnDisk(
        Version Desired,
        string Name,
        DotnetEnvironmentFrameworkInfo Installed,
        Version InstalledVersion);

    private static DotnetRuntimeSelection SelectRuntime(RuntimeOptions options, DotnetEnvironmentInfo env)
    {
        string? rollForwardEnvVar = Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD");
        RollForward rollForward;
        if (rollForwardEnvVar == null)
        {
            rollForward = options.RollForward ?? RollForward.Minor;
        }
        else
        {
            if (!Enum.TryParse(rollForwardEnvVar, out rollForward))
            {
                throw new ArgumentException(
                    $"Unable to parse the value of environment variable DOTNET_ROLL_FORWARD, which was: {rollForwardEnvVar}");
            }
        }

        IReadOnlyList<(Version, string)> desiredVersions;
        if (options.IncludedFrameworks == null)
        {
            if (options.Framework == null)
            {
                if (options.Frameworks == null)
                {
                    throw new InvalidDataException(
                        "Expected runtimeconfig.json file to have either a framework or frameworks entry, but it had neither");
                }

                desiredVersions = options.Frameworks.Select(x => (new Version(x.Version), x.Name)).ToList();
            }
            else
            {
                desiredVersions = [(new Version(options.Framework.Version), options.Framework.Name)];
            }
        }
        else
        {
            desiredVersions = options.IncludedFrameworks.Select(x => (new Version(x.Version), x.Name)).ToList();
        }

        IReadOnlyList<RuntimeOnDisk> compatiblyNamedRuntimes = env.Frameworks.SelectMany(availableFramework =>
            desiredVersions.Where(desired => desired.Item2 == availableFramework.Name)
                .Select(desired => new RuntimeOnDisk(desired.Item1, desired.Item2,
                    availableFramework, new Version(availableFramework.Version)))
        ).ToList();

        switch (rollForward)
        {
            case RollForward.Minor:
            {
                (string, RuntimeOnDisk)? available =
                    compatiblyNamedRuntimes
                        .Where(data =>
                            data.InstalledVersion.Major == data.Desired.Major &&
                            data.InstalledVersion.Minor >= data.Desired.Minor).GroupBy(data => data.Name).Select((
                                data) =>
                            (data.Key,
                                data.MinBy(runtimeOnDisk => (runtimeOnDisk.InstalledVersion.Minor,
                                    runtimeOnDisk.InstalledVersion.Build))!)
                        )
                        // TODO: how do we select between many available frameworks?
                        .FirstOrDefault();
                return available == null
                    ?
                    // TODO: maybe we can ask the SDK. But we keep on trucking: maybe we're self-contained,
                    // and we'll actually find all the runtime next to the DLL.
                    new DotnetRuntimeSelection()
                    : new DotnetRuntimeSelection(available.Value.Item2.Installed);
            }
            case RollForward.Major:
            case RollForward.LatestPatch:
            case RollForward.LatestMinor:
            case RollForward.LatestMajor:
            case RollForward.Disable:
            {
                throw new NotImplementedException();
            }
            default:
            {
                throw new ArgumentOutOfRangeException();
            }
        }
    }

    /// <summary>
    /// Given a .NET executable DLL, identify the most appropriate .NET runtime to run it.
    ///
    /// This is pretty half-baked at the moment; test this yourself to make sure it does what you want it to!
    /// </summary>
    /// <param name="dllPath">Path to an OutputType=Exe .dll file.</param>
    /// <param name="dotnet">Path to the `dotnet` binary which you would use e.g. in `dotnet exec` to run the DLL specified by `dllPath`.</param>
    /// <returns>An ordered collection of folder paths. When resolving any particular DLL during the execution of the input DLL, search these folders; if a DLL name appears in multiple of these folders, the earliest is correct for that DLL.</returns>
    public static IReadOnlyList<string> SelectForDll(string dllPath, string? dotnet = null)
    {
        if (!dllPath.EndsWith(".dll", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"SelectForDll requires the input DLL to have the extension '.dll'; provided: {dllPath}");
        }

        var dll = new FileInfo(dllPath);
        var dllParentDir = dll.Directory ?? throw new ArgumentException($"dll path {dllPath} had no parent");
        string name = dll.Name.Substring(0, dll.Name.Length - ".dll".Length);

        string configFilePath = Path.Combine(dllParentDir.FullName, $"{name}.runtimeconfig.json");

        // It appears to be undocumented why this returns a nullable, and the Rider decompiler doesn't suggest there are
        // any code paths where it can return null?
        RuntimeConfig runtimeConfig =
            System.Text.Json.JsonSerializer.Deserialize<RuntimeConfig>(File.ReadAllText(configFilePath)) ??
            throw new NullReferenceException($"Failed to parse contents of file {configFilePath} as a runtime config");

        DotnetEnvironmentInfo availableRuntimes = dotnet == null
            ? DotnetEnvironmentInfo.Get()
            : DotnetEnvironmentInfo.GetSpecific(new FileInfo(dotnet));

        var runtime = SelectRuntime(runtimeConfig.RuntimeOptions, availableRuntimes);

        return runtime.Visit(framework => new[] {dllParentDir.FullName, $"{framework.Path}/{framework.Version}"},
            sdk => [dllParentDir.FullName, sdk.Path],
            () =>
                // Keep on trucking: let's be optimistic and hope that we're self-contained.
                [dllParentDir.FullName]
            );
    }
}
