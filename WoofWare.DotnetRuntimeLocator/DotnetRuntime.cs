using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("Test")]

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
    /// <summary>
    ///     The policy which <paramref name="value" /> names, as hostfxr's roll_forward_option_from_string decides it: a
    ///     strcasecmp of the whole string against each policy name, so ASCII letters match regardless of case and every
    ///     other character must match exactly. Nothing is trimmed, and the numeric form of the enum is not a name.
    /// </summary>
    /// <returns>The named policy, or null when hostfxr would report the value as invalid.</returns>
    private static RollForward? ParseRollForwardName(string value)
    {
        foreach (var policy in Enum.GetValues<RollForward>())
        {
            var name = policy.ToString();
            if (name.Length != value.Length) continue;

            var matches = true;
            for (var i = 0; i < name.Length; i++)
            {
                // The policy names are ASCII letters, so folding the candidate's ASCII uppercase is the whole of what
                // strcasecmp does here; a non-ASCII character in the candidate can only ever fail to match.
                var c = value[i];
                if (c is >= 'A' and <= 'Z') c = (char) (c + ('a' - 'A'));
                if (c != char.ToLowerInvariant(name[i]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches) return policy;
        }

        return null;
    }

    /// <returns>For each requested runtime in the RuntimeOptions, the resolved place in which to find that runtime.</returns>
    internal static IReadOnlyDictionary<string, DotnetRuntimeSelection> SelectRuntime(RuntimeOptions options,
        DotnetEnvironmentInfo env)
    {
        var rollForwardEnvVar = Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD");
        RollForward rollForward;
        if (string.IsNullOrEmpty(rollForwardEnvVar))
        {
            // hostfxr's pal::getenv reports an empty variable as unset.
            rollForward = options.RollForward ?? RollForward.Minor;
        }
        else
        {
            rollForward = ParseRollForwardName(rollForwardEnvVar) ??
                          throw new ArgumentException(
                              $"Unable to parse the value of environment variable DOTNET_ROLL_FORWARD, which was: '{rollForwardEnvVar}'. hostfxr accepts exactly the six policy names (Disable, LatestPatch, Minor, LatestMinor, Major, LatestMajor), in any casing but with nothing added, and refuses to launch on anything else.");
        }

        IReadOnlyDictionary<string, FxVersion> desiredVersions;
        if (options.IncludedFrameworks == null)
        {
            if (options.Framework == null)
            {
                if (options.Frameworks == null)
                    throw new InvalidDataException(
                        "Expected runtimeconfig.json file to have either a framework or frameworks entry, but it had neither");

                desiredVersions = SoleVersionPerFramework(options.Frameworks);
            }
            else
            {
                desiredVersions = new Dictionary<string, FxVersion>
                {
                    {
                        options.Framework.Name,
                        FxVersion.Parse(options.Framework.Version, $"framework '{options.Framework.Name}'")
                    }
                };
            }
        }
        else
        {
            desiredVersions = SoleVersionPerFramework(options.IncludedFrameworks);
        }

        IReadOnlyDictionary<string, IReadOnlyList<RuntimeOnDisk>> availableRuntimes = env
            .Frameworks.SelectMany(availableFramework =>
            {
                // hostfxr skips an installed framework whose version string it cannot parse, rather than
                // failing: fx_resolver.cpp pushes onto its version_list only when fx_ver_t::parse succeeds.
                // So one unrecognisable directory name does not make every lookup fail.
                if (!FxVersion.TryParse(availableFramework.Version, out var availableVersion)) return [];

                if (!desiredVersions.TryGetValue(availableFramework.Name, out var desiredVersion))
                {
                    // we don't desire this framework at any version; skip it
                    return [];
                }

                if (availableVersion! < desiredVersion)
                {
                    // It's never desired to roll *backward*.
                    return [];
                }

                return new List<(string, RuntimeOnDisk)>
                    { (availableFramework.Name, new RuntimeOnDisk(availableFramework, availableVersion!)) };
            }).GroupBy(x => x.Item1)
            .Select(group => (group.Key, (IReadOnlyList<RuntimeOnDisk>)group.Select(x => x.Item2).ToList()))
            .ToDictionary();

        var rollForwardToPrerelease = RollForwardToPrereleaseFromEnv();

        return desiredVersions.Select(desired =>
        {
            if (!availableRuntimes.TryGetValue(desired.Key, out var available))
                // Nothing installed for this framework is at or above the requested version, or nothing is
                // installed for it at all.
                return (desired.Key, new DotnetRuntimeSelection());

            // hostfxr's runtime_config.cpp gives a framework reference prefer_release when the requested
            // version is a release and DOTNET_ROLL_FORWARD_TO_PRERELEASE is not set. Such a reference
            // searches the release versions on their own first and only falls back to the whole list when
            // that finds nothing, which is what stops an installed preview displacing a release.
            if (!desired.Value.IsPrerelease && !rollForwardToPrerelease)
            {
                var releaseOnly = available.Where(r => !r.InstalledVersion.IsPrerelease).ToList();
                var releaseMatch = SearchForBestFrameworkMatch(rollForward, desired.Value, releaseOnly);
                if (releaseMatch != null)
                    return (desired.Key, new DotnetRuntimeSelection(releaseMatch.Installed));
            }

            var match = SearchForBestFrameworkMatch(rollForward, desired.Value, available);
            return (desired.Key,
                match == null ? new DotnetRuntimeSelection() : new DotnetRuntimeSelection(match.Installed));
        }).ToDictionary();
    }

    /// <summary>
    ///     The version each named framework is requested at, rejecting a list which names one framework more
    ///     than once.
    /// </summary>
    private static IReadOnlyDictionary<string, FxVersion> SoleVersionPerFramework(
        IReadOnlyList<RuntimeConfigFramework> frameworks)
    {
        return frameworks
            .GroupBy(x => x.Name)
            .Select(data =>
            {
                var versions = data.Select(datum => datum.Version).ToList();
                if (versions.Count != 1)
                    throw new InvalidDataException(
                        $"Unexpectedly had not-exactly-one version desired for framework {data.Key}: {string.Join(", ", versions)}");

                return (data.Key, FxVersion.Parse(versions[0], $"framework '{data.Key}'"));
            })
            .ToDictionary();
    }

    /// <summary>
    ///     The version hostfxr's <c>search_for_best_framework_match</c> picks out of
    ///     <paramref name="available" />, which holds exactly those installed versions at or above
    ///     <paramref name="desired" />.
    /// </summary>
    /// <remarks>
    ///     Two phases. First the policy's compatibility range says which candidates are admissible, and the
    ///     lowest of them is taken — or the highest, for the policies which set hostfxr's
    ///     <c>roll_to_highest_version</c>. Then <c>automatic_roll_to_latest_patch</c> moves to the highest
    ///     patch at that major.minor, except from a prerelease, where hostfxr keeps the closest match rather
    ///     than rolling.
    /// </remarks>
    /// <returns>The chosen runtime, or null when the policy admits none of the candidates.</returns>
    private static RuntimeOnDisk? SearchForBestFrameworkMatch(RollForward rollForward, FxVersion desired,
        IReadOnlyList<RuntimeOnDisk> available)
    {
        if (rollForward == RollForward.Disable)
            // hostfxr's "exact" compatibility range does not consult the version list at all: it looks for
            // the requested version's own directory and takes it or nothing.
            return available.FirstOrDefault(a => a.InstalledVersion == desired);

        var admissible = available
            .Where(a => WithinCompatibilityRange(rollForward, desired, a.InstalledVersion))
            .ToList();
        if (admissible.Count == 0) return null;

        // LatestMinor and LatestMajor are the policies which set roll_to_highest_version. It has no effect
        // on the patch range, which hostfxr excludes explicitly, and LatestPatch is that range.
        var best = rollForward is RollForward.LatestMinor or RollForward.LatestMajor
            ? admissible.MaxBy(a => a.InstalledVersion)!
            : admissible.MinBy(a => a.InstalledVersion)!;

        // "If we've found a pre-release version match, then don't apply automatic roll to latest patch."
        if (best.InstalledVersion.IsPrerelease) return best;

        return admissible
            .Where(a => a.InstalledVersion.Major == best.InstalledVersion.Major
                        && a.InstalledVersion.Minor == best.InstalledVersion.Minor)
            .MaxBy(a => a.InstalledVersion)!;
    }

    /// <summary>
    ///     Whether <paramref name="candidate" /> is within the version compatibility range which
    ///     <paramref name="rollForward" /> selects, given that it is already at or above
    ///     <paramref name="desired" />.
    /// </summary>
    private static bool WithinCompatibilityRange(RollForward rollForward, FxVersion desired, FxVersion candidate)
    {
        return rollForward switch
        {
            RollForward.LatestPatch => candidate.Major == desired.Major && candidate.Minor == desired.Minor,
            RollForward.Minor or RollForward.LatestMinor => candidate.Major == desired.Major,
            RollForward.Major or RollForward.LatestMajor => true,
            RollForward.Disable => throw new InvalidOperationException(
                "logic error: the exact compatibility range does not consult the version list"),
            _ => throw new ArgumentOutOfRangeException(nameof(rollForward), rollForward,
                "unrecognised roll-forward policy")
        };
    }

    /// <summary>
    ///     Whether DOTNET_ROLL_FORWARD_TO_PRERELEASE asks for prerelease versions to be considered on an
    ///     equal footing with releases, as hostfxr's runtime_config.cpp reads it: <c>pal::getenv</c> reports
    ///     an empty variable as unset, and the value goes through <c>atoi</c>, which skips leading
    ///     whitespace, takes an optional sign and the leading run of digits, and yields 0 when there are
    ///     none. Only the value 1 turns the preference off.
    /// </summary>
    private static bool RollForwardToPrereleaseFromEnv()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD_TO_PRERELEASE");
        if (string.IsNullOrEmpty(value)) return false;

        var i = 0;
        // The whitespace atoi skips is the C locale's isspace, not Unicode's.
        while (i < value!.Length && value[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r') i++;

        var negated = false;
        if (i < value.Length && value[i] is '+' or '-')
        {
            negated = value[i] == '-';
            i++;
        }

        long accumulated = 0;
        var digitsStart = i;
        while (i < value.Length && value[i] is >= '0' and <= '9')
        {
            accumulated = accumulated * 10 + (value[i] - '0');
            // Anything this large is not 1, so stop rather than model atoi's overflow.
            if (accumulated > int.MaxValue) return false;
            i++;
        }

        if (i == digitsStart) return false;

        return !negated && accumulated == 1;
    }

    private static JsonSerializerOptions _options = new() {PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter<RollForward>() }};

    /// <summary>
    /// Parse a runtimeconfig.json file.
    /// </summary>
    /// <param name="contents">Contents of the runtimeconfig.json file to parse.</param>
    /// <exception cref="NullReferenceException">I think this can't happen, but the docs suggest that deserialization might return null.</exception>
    public static RuntimeConfig? DeserializeRuntimeConfig(string contents)
    {
        return JsonSerializer.Deserialize<RuntimeConfig>(contents, _options);
    }

    /// <summary>
    ///     Given a .NET executable DLL, compute the path at which its runtimeconfig.json sits: the file is named
    ///     after the DLL and lives beside it. The file is not required to exist, and this method does not check.
    /// </summary>
    /// <param name="dllPath">Path to an OutputType=Exe .dll file.</param>
    /// <returns>The absolute path at which the runtimeconfig.json for <paramref name="dllPath" /> would sit.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="dllPath" /> does not end in ".dll", or names no parent directory.
    /// </exception>
    public static string RuntimeConfigPathForDll(string dllPath)
    {
        if (!dllPath.EndsWith(".dll", StringComparison.Ordinal))
            throw new ArgumentException(
                $"RuntimeConfigPathForDll requires the input DLL to have the extension '.dll'; provided: {dllPath}");

        var dll = new FileInfo(dllPath);
        var dllParentDir = dll.Directory ?? throw new ArgumentException($"dll path {dllPath} had no parent");
        var name = dll.Name.Substring(0, dll.Name.Length - ".dll".Length);

        return Path.Combine(dllParentDir.FullName, $"{name}.runtimeconfig.json");
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

        var configFilePath = RuntimeConfigPathForDll(dllPath);

        // It appears to be undocumented why this returns a nullable, and the Rider decompiler doesn't suggest there are
        // any code paths where it can return null?
        var contents = File.ReadAllText(configFilePath);
        var runtimeConfig = DeserializeRuntimeConfig(contents) ?? throw new NullReferenceException($"Failed to parse contents of file {configFilePath} as a runtime config");

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
        FxVersion InstalledVersion);
}
