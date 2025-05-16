using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     The result of a call to `DotnetRuntime.Select`.
///     This is `type DotnetRuntimeSelection = | Framework of DotnetEnvironmentFrameworkInfo | Sdk of
///     DotnetEnvironmentSdkInfo | Absent`.
/// </summary>
internal class DotnetRuntimeSelection
{
    private readonly int _discriminator;
    private readonly DotnetEnvironmentFrameworkInfo? _framework;
    private readonly DotnetEnvironmentSdkInfo? _sdk;

    /// <summary>
    ///     The constructor which means "We found the right runtime, and it's from this framework".
    /// </summary>
    /// <param name="framework">For example, </param>
    public DotnetRuntimeSelection(DotnetEnvironmentFrameworkInfo framework)
    {
        _discriminator = 1;
        _framework = framework;
    }

    /// <summary>
    ///     The constructor which means "We found the right runtime, and it's from this SDK".
    /// </summary>
    /// <param name="sdk">For example, </param>
    public DotnetRuntimeSelection(DotnetEnvironmentSdkInfo sdk)
    {
        _discriminator = 2;
        _sdk = sdk;
    }

    /// <summary>
    ///     The constructor which means "We were unable to find an appropriate runtime".
    /// </summary>
    public DotnetRuntimeSelection()
    {
        _discriminator = 3;
    }

    /// <summary>
    ///     Exhaustive match on this discriminated union.
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
///     Module to hold methods for automatically identifying a .NET runtime.
/// </summary>
public static class DotnetRuntime
{
    /// <returns>For each requested runtime in the RuntimeOptions, the resolved place in which to find that runtime.</returns>
    private static IReadOnlyDictionary<string, DotnetRuntimeSelection> SelectRuntime(RuntimeOptions options,
        DotnetEnvironmentInfo env)
    {
        var rollForwardEnvVar = Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD");
        RollForward rollForward;
        if (rollForwardEnvVar == null)
        {
            rollForward = options.RollForward ?? RollForward.Minor;
        }
        else
        {
            if (!Enum.TryParse(rollForwardEnvVar, out rollForward))
                throw new ArgumentException(
                    $"Unable to parse the value of environment variable DOTNET_ROLL_FORWARD, which was: {rollForwardEnvVar}");
        }

        IReadOnlyDictionary<string, Version> desiredVersions;
        if (options.IncludedFrameworks == null)
        {
            if (options.Framework == null)
            {
                if (options.Frameworks == null)
                    throw new InvalidDataException(
                        "Expected runtimeconfig.json file to have either a framework or frameworks entry, but it had neither");

                desiredVersions = options.Frameworks.Select(x => (x.Name, new Version(x.Version))).GroupBy(x => x.Name)
                    .Select(data =>
                    {
                        var versions = (IReadOnlyList<Version>)data.Select(datum => datum.Item2).ToList();
                        if (versions.Count != 1)
                        {
                            var description = string.Join(", ", versions.Select(x => x.ToString()));
                            throw new InvalidDataException(
                                $"Unexpectedly had not-exactly-one version desired for framework {data.Key}: {description}");
                        }

                        return (data.Key, versions[0]);
                    })
                    .ToDictionary();
            }
            else
            {
                var result = new Dictionary<string, Version>
                    { { options.Framework.Name, new Version(options.Framework.Version) } };
                desiredVersions = result;
            }
        }
        else
        {
            desiredVersions = options.IncludedFrameworks.Select(x => (x.Name, new Version(x.Version)))
                .GroupBy(x => x.Name)
                .Select(data =>
                {
                    var versions = (IReadOnlyList<Version>)data.Select(datum => datum.Item2).ToList();
                    if (versions.Count != 1)
                    {
                        var description = string.Join(", ", versions.Select(x => x.ToString()));
                        throw new InvalidDataException(
                            $"Unexpectedly had not-exactly-one version desired for framework {data.Key}: {description}");
                    }

                    return (data.Key, versions[0]);
                })
                .ToDictionary();
        }

        IReadOnlyDictionary<string, IReadOnlyList<RuntimeOnDisk>> availableRuntimes = env
            .Frameworks.SelectMany(availableFramework =>
            {
                var availableVersion = new Version(availableFramework.Version);
                if (!desiredVersions.TryGetValue(availableFramework.Name, out var desiredVersion))
                {
                    // we don't desire this framework at any version; skip it
                    return [];
                }

                if (availableVersion < desiredVersion)
                {
                    // It's never desired to roll *backward*.
                    return [];
                }
                return new List<(string, DotnetEnvironmentFrameworkInfo)>
                    { (availableFramework.Name, availableFramework) };
            }).GroupBy(x => x.Item1)
            .Select(group =>
            {
                var grouping = group.Select(x => new RuntimeOnDisk(x.Item2, new Version(x.Item2.Version))).ToList();
                return (group.Key, (IReadOnlyList<RuntimeOnDisk>)grouping);
            })
            .ToDictionary();

        switch (rollForward)
        {
            case RollForward.Minor:
            {
                return desiredVersions.Select(desired =>
                {
                    if (!availableRuntimes.TryGetValue(desired.Key, out var available))
                    {
                        return (desired.Key, new DotnetRuntimeSelection());
                    }

                    if (ReferenceEquals(available, null))
                    {
                        throw new NullReferenceException("logic error: contents of non-nullable dict can't be null");
                    }

                    // If there's a correct major and minor version, take the latest patch.
                    var correctMajorAndMinorVersion =
                        available.Where(data =>
                            data.InstalledVersion.Major == desired.Value.Major &&
                            data.InstalledVersion.Minor == desired.Value.Minor).ToList();
                    if (correctMajorAndMinorVersion.Count > 0)
                    {
                        return (desired.Key, new DotnetRuntimeSelection(correctMajorAndMinorVersion.MaxBy(v => v.InstalledVersion)!.Installed));
                    }

                    // Otherwise roll forward to lowest higher minor version
                    var candidate = available.Where(data => data.InstalledVersion.Major == desired.Value.Major)
                        .MinBy(v => (v.InstalledVersion.Minor, -v.InstalledVersion.Build));

                    return (desired.Key, candidate == null ? new DotnetRuntimeSelection() : new DotnetRuntimeSelection(candidate.Installed));
                }).ToDictionary();
            }
            case RollForward.Major:
            {
                throw new NotImplementedException();
            }
            case RollForward.LatestPatch:
            {
                return desiredVersions.Select(desired =>
                {
                    var matches = availableRuntimes[desired.Key]
                        .Where(data =>
                            data.InstalledVersion.Minor == desired.Value.Minor &&
                            data.InstalledVersion.Major == desired.Value.Major).MaxBy(data => data.InstalledVersion);
                    return matches == null
                        ? (desired.Key, new DotnetRuntimeSelection())
                        : (desired.Key, new DotnetRuntimeSelection(matches.Installed));
                }).ToDictionary();
            }
            case RollForward.LatestMinor:
            {
                return desiredVersions.Select(desired =>
                {
                    var matches = availableRuntimes[desired.Key]
                        .Where(data =>
                            data.InstalledVersion.Major == desired.Value.Major).MaxBy(data => data.InstalledVersion);
                    return matches == null
                        ? (desired.Key, new DotnetRuntimeSelection())
                        : (desired.Key, new DotnetRuntimeSelection(matches.Installed));
                }).ToDictionary();
            }
            case RollForward.LatestMajor:
            {
                return desiredVersions.Select(desired =>
                {
                    var match = availableRuntimes[desired.Key].MaxBy(data => data.InstalledVersion);
                    return match == null ? (desired.Key, new DotnetRuntimeSelection()) : (desired.Key, new DotnetRuntimeSelection(match.Installed));
                }).ToDictionary();
            }
            case RollForward.Disable:
            {
                return desiredVersions.Select(desired =>
                    {
                        var exactMatch = availableRuntimes[desired.Key]
                            .FirstOrDefault(available => available.InstalledVersion == desired.Value);
                        if (exactMatch != null)
                        {
                            return (desired.Key, new DotnetRuntimeSelection(exactMatch.Installed));
                        }
                        else
                        {
                            return (desired.Key, new DotnetRuntimeSelection());
                        }
                    }
                ).ToDictionary();
            }
            default:
            {
                throw new ArgumentOutOfRangeException();
            }
        }
    }

    /// <summary>
    ///     Given a .NET executable DLL, identify the most appropriate .NET runtime to run it.
    ///     This is pretty half-baked at the moment; test this yourself to make sure it does what you want it to!
    /// </summary>
    /// <param name="dllPath">Path to an OutputType=Exe .dll file.</param>
    /// <param name="dotnet">
    ///     Path to the `dotnet` binary which you would use e.g. in `dotnet exec` to run the DLL specified by
    ///     `dllPath`.
    /// </param>
    /// <returns>
    ///     An ordered collection of folder paths. When resolving any particular DLL during the execution of the input
    ///     DLL, search these folders; if a DLL name appears in multiple of these folders, the earliest is correct for that
    ///     DLL.
    /// </returns>
    public static IReadOnlyList<string> SelectForDll(string dllPath, string? dotnet = null)
    {
        if (!dllPath.EndsWith(".dll", StringComparison.Ordinal))
            throw new ArgumentException(
                $"SelectForDll requires the input DLL to have the extension '.dll'; provided: {dllPath}");

        var dll = new FileInfo(dllPath);
        var dllParentDir = dll.Directory ?? throw new ArgumentException($"dll path {dllPath} had no parent");
        var name = dll.Name.Substring(0, dll.Name.Length - ".dll".Length);

        var configFilePath = Path.Combine(dllParentDir.FullName, $"{name}.runtimeconfig.json");

        // It appears to be undocumented why this returns a nullable, and the Rider decompiler doesn't suggest there are
        // any code paths where it can return null?
        var runtimeConfig =
            JsonSerializer.Deserialize<RuntimeConfig>(File.ReadAllText(configFilePath)) ??
            throw new NullReferenceException($"Failed to parse contents of file {configFilePath} as a runtime config");

        var availableRuntimes = dotnet == null
            ? DotnetEnvironmentInfo.Get()
            : DotnetEnvironmentInfo.GetSpecific(new FileInfo(dotnet));

        var runtimes = SelectRuntime(runtimeConfig.RuntimeOptions, availableRuntimes);

        return runtimes.SelectMany(runtime => runtime.Value.Visit(framework => new[] { $"{framework.Path}/{framework.Version}" },
            sdk => [sdk.Path],
            () => []
        )).Prepend(dllParentDir.FullName).ToList();
    }

    private record RuntimeOnDisk(
        DotnetEnvironmentFrameworkInfo Installed,
        Version InstalledVersion);
}
