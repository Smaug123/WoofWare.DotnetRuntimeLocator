using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HostEscapeSweep;

/// <summary>
///     Regenerates <c>WoofWare.DotnetRuntimeLocator/Test/host-escape-sweep.json</c> by measuring what a
///     real .NET host does to a "configProperties" value, rather than by reasoning about it.
/// </summary>
/// <remarks>
///     <para>
///         The tool runs twice. The parent injects a <c>configProperties</c> section into its own
///         runtimeconfig.json — one property per sampled code point, each holding <c>["&lt;that one
///         character&gt;"]</c> — and then launches itself again. The array wrapper is the point: it forces
///         the value through the host's JSON *writer*, whereas a top-level string is passed through
///         untouched and would measure nothing. The child, now started by a host which has read that
///         config, asks <c>AppContext.GetData</c> what it actually received and writes the fixture.
///     </para>
///     <para>
///         The parent restores its runtimeconfig.json afterwards, so a second run measures the same thing
///         as the first.
///     </para>
/// </remarks>
internal static class Program
{
    private const string ChildFlag = "--child";

    /// <summary>
    ///     Every code point below U+0100, which covers the C0 controls, ASCII, the C1 range and Latin-1
    ///     supplement exhaustively, plus a spread of higher ones. The interesting boundaries above U+00FF
    ///     are the ones a JavaScript encoder treats specially — the line terminators U+2028 and U+2029 —
    ///     and the edges of the BMP and of the astral planes, since <c>UnicodeRanges.All</c> covers only
    ///     the BMP and encoders built from it escape everything beyond.
    /// </summary>
    private static IEnumerable<int> SampledCodePoints()
    {
        for (var codePoint = 0x00; codePoint <= 0xFF; codePoint++) yield return codePoint;

        int[] higher =
        [
            0x0100, 0x01FF, 0x0370, 0x05D0, 0x0600, 0x0E01, 0x1000, 0x1E00, 0x2000,
            0x2018, 0x2028, 0x2029, 0x2030, 0x20AC, 0x2100, 0x2190, 0x3000, 0x3042,
            0x4E00, 0x9FFF, 0xA000, 0xD7FF, 0xE000, 0xF8FF, 0xFB00, 0xFDD0, 0xFEFF,
            0xFFF9, 0xFFFD, 0xFFFE, 0xFFFF,
            0x10000, 0x1F600, 0x2F800, 0x10FFFF,
        ];

        foreach (var codePoint in higher)
            // A surrogate cannot appear in a JSON document at all, so there is nothing to measure.
            if (codePoint is < 0xD800 or > 0xDFFF)
                yield return codePoint;
    }

    /// <summary>The config property name carrying the sample for this code point.</summary>
    private static string PropertyName(int codePoint) =>
        "CP_" + codePoint.ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>The key this code point's observation is filed under in the fixture.</summary>
    private static string FixtureKey(int codePoint) =>
        codePoint.ToString("X", CultureInfo.InvariantCulture).PadLeft(4, '0');

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // The fixture is read back by System.Text.Json, so its own escaping round-trips fine; this
        // only keeps ordinary non-ASCII text legible in the committed file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == ChildFlag) return RunChild(args[1]);

            if (args.Length == 1) return RunParent(Path.GetFullPath(args[0]));

            Console.Error.WriteLine(
                "usage: HostEscapeSweep <path-to-host-escape-sweep.json>\n\n" +
                "Rewrites that file with what the .NET host running this tool does to a\n" +
                "configProperties value. Point it at the runtime you want to measure: the fixture\n" +
                "records which one it saw, and the committed one was measured on .NET 10.");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static int RunParent(string fixturePath)
    {
        var assembly = Assembly.GetEntryAssembly()
                       ?? throw new InvalidOperationException("no entry assembly; cannot locate our own config");
        var assemblyPath = assembly.Location;
        var configPath = Path.Combine(
            AppContext.BaseDirectory,
            Path.GetFileNameWithoutExtension(assemblyPath) + ".runtimeconfig.json");

        if (!File.Exists(configPath))
            throw new FileNotFoundException(
                $"Expected our own runtimeconfig at {configPath}. This tool works by editing that file, so it cannot run from a single-file or self-contained publish.",
                configPath);

        // Edit rather than regenerate, so whatever framework and roll-forward the build chose survive.
        var original = File.ReadAllText(configPath);
        var config = JsonNode.Parse(original)
                     ?? throw new InvalidOperationException($"{configPath} parsed as JSON null");

        var properties = new JsonObject();
        foreach (var codePoint in SampledCodePoints())
            properties[PropertyName(codePoint)] = new JsonArray(JsonValue.Create(char.ConvertFromUtf32(codePoint)));

        var runtimeOptions = config["runtimeOptions"]
                             ?? throw new InvalidOperationException($"{configPath} has no runtimeOptions");
        runtimeOptions["configProperties"] = properties;

        File.WriteAllText(configPath, config.ToJsonString(WriteOptions));

        try
        {
            return LaunchChild(assemblyPath, fixturePath);
        }
        finally
        {
            File.WriteAllText(configPath, original);
        }
    }

    private static int LaunchChild(string assemblyPath, string fixturePath)
    {
        var host = Environment.ProcessPath
                   ?? throw new InvalidOperationException("cannot determine how this process was launched");

        var startInfo = new ProcessStartInfo(host);

        // Launched through the muxer (`dotnet run`, `dotnet X.dll`) the child needs the assembly named
        // explicitly; launched through its own apphost it does not.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.Ordinal))
            startInfo.ArgumentList.Add(assemblyPath);

        startInfo.ArgumentList.Add(ChildFlag);
        startInfo.ArgumentList.Add(fixturePath);

        using var child = Process.Start(startInfo)
                          ?? throw new InvalidOperationException($"failed to start {host}");
        child.WaitForExit();
        return child.ExitCode;
    }

    private static int RunChild(string fixturePath)
    {
        var observations = new JsonObject();
        var missing = new List<string>();

        foreach (var codePoint in SampledCodePoints())
        {
            var name = PropertyName(codePoint);

            // A null here means the host did not carry the property through at all, which would make the
            // fixture silently short rather than wrong. Fail instead.
            if (AppContext.GetData(name) is not string observed)
            {
                missing.Add(name);
                continue;
            }

            observations[FixtureKey(codePoint)] = observed;
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"The host did not supply {missing.Count} of the sampled properties, so this run cannot produce a complete fixture. First few: {string.Join(", ", missing.GetRange(0, Math.Min(5, missing.Count)))}.");

        var fixture = new JsonObject
        {
            ["note"] =
                "Keys are the code point in uppercase hex. Values are exactly what AppContext.GetData "
                + "returned in a real .NET process for a config property whose value was [\"<that one "
                + "character>\"]. Do not hand-edit: regenerate with the HostEscapeSweep tool in this "
                + "repository, `dotnet run --project HostEscapeSweep -- <path to this file>`, run against "
                + "the runtime you want to measure.",
            ["observations"] = observations,
            ["provenance"] =
                "The array wrapper is what forces the value through the host's JSON writer; a top-level "
                + "string would be passed through untouched and would measure nothing. Measured on "
                + $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}.",
        };

        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        File.WriteAllText(fixturePath, fixture.ToJsonString(WriteOptions) + Environment.NewLine);

        Console.WriteLine(
            $"Wrote {observations.Count} observations to {fixturePath}, measured on {RuntimeInformation.FrameworkDescription}.");
        return 0;
    }
}
