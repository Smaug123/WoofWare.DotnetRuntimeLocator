using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     Enforcement for the members of this file's types whose slots are non-nullable.
/// </summary>
/// <remarks>
///     <para>
///         C#'s <c>required</c> constrains the JSON member to be <em>present</em>; it says nothing about
///         its value. So <c>{"runtimeOptions":null}</c> satisfies <c>required</c> and lands a null in a
///         slot the type declares can never hold one, and the first consumer to dereference it gets a
///         <see cref="NullReferenceException" /> naming their own code rather than the file which caused
///         it. Checking on the way in instead makes the type's non-nullability a fact rather than a wish.
///     </para>
///     <para>
///         Refusing is also what the format itself does for three of the four members: on the real host
///         (Microsoft.NETCore.App 9.0.0), a null "tfm", "name" or "version" makes it exit with
///         0x8000808b (ResolverInitFailure) and print nothing whatsoever, so nulling one of those does
///         not produce a runnable app under any reading. The fourth, "runtimeOptions", is the one
///         divergence, and is discussed on <see cref="RuntimeConfig.RuntimeOptions" />.
///     </para>
///     <para>
///         This lives in the <c>init</c> accessors rather than in a converter or in
///         <see cref="DotnetRuntime.DeserializeRuntimeConfig" />, because the invariant belongs to the
///         type: these records are public, so a consumer can deserialize them itself — the test suite
///         does — or build one in code, and both paths must uphold it. An accessor also covers an
///         element nested in "frameworks" or "includedFrameworks" without any extra machinery.
///     </para>
///     <para>
///         The exception is a <see cref="JsonException" /> because deserializing is overwhelmingly how
///         these records get built, and because it is what the same file already gets for its other
///         defects: <c>System.Text.Json</c> raises <see cref="JsonException" /> both for malformed JSON
///         and for an absent <c>required</c> member, so a caller who is parsing a file it does not
///         control still needs exactly one catch. <see cref="JsonSerializer" /> propagates whatever an
///         <c>init</c> accessor throws, unwrapped, so choosing anything else here would leak a second
///         exception type out of a parse call.
///     </para>
/// </remarks>
internal static class NonNullableMember
{
    /// <summary>
    ///     Return <paramref name="value" />, or refuse it if it is null.
    /// </summary>
    /// <param name="value">The value the member is being set to.</param>
    /// <param name="jsonName">The member's name as it is spelled in the file, for the error message.</param>
    /// <exception cref="JsonException"><paramref name="value" /> is null.</exception>
    internal static T OrThrow<T>(T? value, string jsonName) where T : class
    {
        return value ?? throw new JsonException(
            $"The runtimeconfig.json member \"{jsonName}\" was present but null. This library declares that member non-nullable, so it refuses the file rather than hand you a null in a slot which cannot hold one. Give the member a value, or omit it if it is one of the optional ones.");
    }

    /// <summary>
    ///     Return an unaliased, unmodifiable copy of <paramref name="value" />, or refuse it if any
    ///     element is null.
    /// </summary>
    /// <param name="value">
    ///     The list the member is being set to. A null list is fine and passes straight through: the
    ///     members holding lists are themselves optional, and it is only their <em>elements</em> which
    ///     are declared non-nullable.
    /// </param>
    /// <param name="jsonName">The member's name as it is spelled in the file, for the error message.</param>
    /// <remarks>
    ///     <para>
    ///         This case needs checking on the list rather than in an accessor of the element type,
    ///         because an element spelled <c>null</c> constructs no element at all — there is nothing
    ///         whose accessor could run.
    ///     </para>
    ///     <para>
    ///         It copies rather than validating in place, because a check on a list someone else holds
    ///         only establishes the invariant for an instant: the caller could null an entry afterwards,
    ///         and what the deserializer builds is a <see cref="List{T}" />, which anyone holding the
    ///         <see cref="IReadOnlyList{T}" /> can downcast and write to. Both would falsify a claim
    ///         these records make about themselves. The copy is wrapped rather than handed back as an
    ///         array for the same reason: an array exposed as <see cref="IReadOnlyList{T}" /> is one
    ///         cast away from being writable again.
    ///     </para>
    ///     <para>
    ///         One consequence worth knowing: two records built from the same source list hold different
    ///         list instances, and this record's compiler-generated equality compares lists by reference,
    ///         so they compare unequal. The same goes for two separately-parsed configs.
    ///     </para>
    /// </remarks>
    /// <exception cref="JsonException">Some element of <paramref name="value" /> is null.</exception>
    internal static IReadOnlyList<T>? ElementsOrThrow<T>(IReadOnlyList<T?>? value, string jsonName) where T : class
    {
        if (value == null) return null;

        var copy = new T[value.Count];

        for (var i = 0; i < value.Count; i++)
            copy[i] = value[i] ?? throw new JsonException(
                $"Entry {i} of the runtimeconfig.json member \"{jsonName}\" was null. This library declares the entries of that list non-nullable, so it refuses the file rather than hand you a null in a slot which cannot hold one. Give the entry a value, or remove it.");

        return new ReadOnlyCollection<T>(copy);
    }
}

/// <summary>
///     The type of a "framework" entry in the "runtimeOptions" setting of a runtimeconfig.json file.
/// </summary>
public record RuntimeConfigFramework
{
    private readonly string _name = null!;
    private readonly string _version = null!;

    /// <summary>
    ///     For example, "Microsoft.NETCore.App". Never null: a file spelling it <c>null</c> is rejected
    ///     when it is parsed.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name
    {
        get => _name;
        init => _name = NonNullableMember.OrThrow(value, "name");
    }

    /// <summary>
    ///     For example, "9.0.0". Never null: a file spelling it <c>null</c> is rejected when it is parsed.
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version
    {
        get => _version;
        init => _version = NonNullableMember.OrThrow(value, "version");
    }
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
///     Helpers for the "configProperties" section of a runtimeconfig.json file: the runtime knobs which
///     the .NET host hands to the runtime at process startup, and which managed code reads back through
///     <c>System.AppContext</c>.
/// </summary>
public static class ConfigProperties
{
    /// <summary>
    ///     Render one "configProperties" value the way the .NET host renders it before handing it to the
    ///     runtime.
    /// </summary>
    /// <param name="value">A value from the "configProperties" object of a runtimeconfig.json file.</param>
    /// <returns>The string the host would place in the runtime's property bag for this value.</returns>
    /// <remarks>
    ///     <para>
    ///         Every value in the runtime's property bag is a string, whatever its JSON type. Managed code
    ///         calling <c>AppContext.GetData</c> always gets a <see cref="string" /> back, and
    ///         <c>AppContext.TryGetSwitch</c> works by <c>bool.TryParse</c>-ing that string. So strings are
    ///         passed through verbatim, and every other JSON value becomes its compact JSON text: <c>true</c>
    ///         becomes "true", <c>null</c> becomes the four-character string "null", and objects and arrays
    ///         lose all insignificant whitespace.
    ///     </para>
    ///     <para>
    ///         A string nested inside an array or object is escaped exactly as the host escapes it: only
    ///         <c>"</c>, <c>\</c> and the C0 control range are escaped, so non-ASCII text, HTML-sensitive
    ///         characters, U+2028, U+2029 and astral-plane characters all survive as themselves. This is
    ///         emphatically not what <see cref="Utf8JsonWriter" /> does with any stock encoder, and it was
    ///         established by measuring a real process rather than by reading the host's source.
    ///     </para>
    ///     <para>
    ///         A value carrying an embedded NUL is cut short at it, because the host hands the runtime
    ///         null-terminated strings; see <see cref="TruncateAtNul" />. This applies to a top-level
    ///         string, which is passed through, but not to one nested in an array or object, which is
    ///         escaped and so cannot contain a NUL by the time it reaches the property bag.
    ///     </para>
    ///     <para>
    ///         What this returns, it returns exactly: there is no case where it renders a value which
    ///         merely resembles what the host would produce. The price is that it is a partial function.
    ///         The host puts every number it cannot hold as a 64-bit integer through a double and prints
    ///         it with rapidjson's dtoa, which is not modelled here, so such a number throws rather than
    ///         being echoed back and quietly differing — it renders <c>1.50</c> as "1.5", <c>1e3</c> as
    ///         "1000.0" and <c>18446744073709551616</c> as "18446744073709552000.0". No .NET SDK emits a
    ///         number in any affected spelling.
    ///     </para>
    ///     <para>
    ///         So an integer the host *can* hold — anything in [<see cref="long.MinValue" />,
    ///         <see cref="ulong.MaxValue" />] — matches exactly, including <c>-0</c>, which both it and
    ///         this method render as "0". Strings, booleans, nulls, and the structure of arrays and
    ///         objects all match exactly too.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     The value is <see cref="JsonValueKind.Undefined" /> (a <c>default(JsonElement)</c> that never came
    ///     from parsing any JSON), or it contains a number this cannot render faithfully, or it is nested
    ///     more deeply than the JSON reader's own default limit.
    /// </exception>
    public static string ToHostString(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
                throw new ArgumentException(
                    "Cannot render an undefined JsonElement as a config property value; this is a default(JsonElement) rather than a parsed one.",
                    nameof(value));
            case JsonValueKind.String:
                // GetString is only null for JsonValueKind.Null, which this arm has excluded.
                return TruncateAtNul(value.GetString()!);
            default:
                var builder = new StringBuilder();
                WriteValue(value, builder, 0);
                // No truncation needed here: a NUL inside a nested string is escaped to the six
                // characters backslash-u-0000 by WriteString, so a rendered composite never contains one.
                return builder.ToString();
        }
    }

    /// <summary>
    ///     Cut <paramref name="value" /> at its first NUL, if it has one.
    /// </summary>
    /// <remarks>
    ///     The host hands the runtime its property bag as null-terminated strings, so a name or value
    ///     carrying an embedded NUL reaches managed code as only the part before it. Measured: a config
    ///     property whose value is <c>"a[NUL]b"</c> arrives at <c>AppContext.GetData</c> as "a", and one
    ///     whose *name* is <c>"K[NUL]suffix"</c> is registered under "K".
    /// </remarks>
    private static string TruncateAtNul(string value)
    {
        var nul = value.IndexOf('\0');
        return nul < 0 ? value : value.Substring(0, nul);
    }

    /// <summary>
    ///     Render a JSON number's raw token the way the host renders it, or refuse.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The host's parser holds an integer token as a 64-bit integer when it fits, and prints that
    ///         integer back. Round-tripping through <see cref="long" />/<see cref="ulong" /> reproduces
    ///         this. JSON forbids a leading <c>+</c> and leading zeros, so the only in-range token whose
    ///         spelling this actually changes is <c>-0</c>, which the host prints as "0" — measured, at
    ///         top level and at every nesting depth.
    ///     </para>
    ///     <para>
    ///         Every other number — fractional, exponent-bearing, or an integer outside
    ///         [<see cref="long.MinValue" />, <see cref="ulong.MaxValue" />] — the host puts through a
    ///         double and prints with rapidjson's dtoa, which this does not model, so this throws rather
    ///         than return a value it cannot vouch for. Some of those tokens would in fact survive being
    ///         echoed back — the host renders <c>1.5</c> as "1.5" — but neighbouring ones would not: it
    ///         renders <c>1.50</c> as "1.5", <c>0.10</c> as "0.1" and <c>1e3</c> as "1000.0". Telling the
    ///         two groups apart is exactly the modelling being declined, so the whole class is refused.
    ///     </para>
    ///     <para>
    ///         The integer boundaries were measured exactly: -9223372036854775808 and 18446744073709551615
    ///         keep their spelling, while one step beyond either becomes "-9223372036854776000.0" and
    ///         "18446744073709552000.0" respectively.
    ///     </para>
    /// </remarks>
    private static string NumberToHostString(string rawText)
    {
        if (long.TryParse(rawText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                out var signed))
            return signed.ToString(CultureInfo.InvariantCulture);

        if (ulong.TryParse(rawText, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
            return unsigned.ToString(CultureInfo.InvariantCulture);

        throw new ArgumentException(
            $"Cannot render the config property number {rawText} as the host would: it is not an integer in [{long.MinValue}, {ulong.MaxValue}], so the host puts it through a double and prints it with rapidjson's dtoa, which this library does not model. Rather than return a value which may silently differ from what AppContext will report, this refuses. Spell the value as an in-range integer, or as a string if the consumer parses it itself.",
            nameof(rawText));
    }

    /// <summary>
    ///     The deepest value this will render, matching <see cref="JsonReaderOptions.MaxDepth" />'s default
    ///     of 64: an element parsed with default options cannot exceed it, so this bound only rejects an
    ///     element built by hand or parsed with a raised limit. It exists because the alternative to
    ///     rejecting such an element is a <see cref="StackOverflowException" />, which cannot be caught and
    ///     takes the process out with no diagnostic.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    ///     Append <paramref name="value" /> to <paramref name="builder" /> as the host would write it.
    /// </summary>
    /// <remarks>
    ///     This does not use <see cref="Utf8JsonWriter" />, because that writer escapes through a
    ///     <see cref="System.Text.Encodings.Web.JavaScriptEncoder" /> and no available encoder has the
    ///     host's escaping policy. The default one escapes every non-ASCII and HTML-sensitive character,
    ///     and <c>UnsafeRelaxedJsonEscaping</c> still escapes U+2028, U+2029 and everything outside the
    ///     BMP; the host escapes none of those. See <see cref="WriteString" /> for the policy, which was
    ///     measured rather than inferred.
    /// </remarks>
    private static void WriteValue(JsonElement value, StringBuilder builder, int depth)
    {
        if (depth > MaxDepth)
            throw new ArgumentException(
                $"Cannot render a config property value nested more than {MaxDepth} deep; this is deeper than the JSON reader's own default limit, so it cannot have come from parsing a runtimeconfig.json with default options.",
                nameof(value));

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstMember = true;
                foreach (var member in value.EnumerateObject())
                {
                    if (!firstMember) builder.Append(',');
                    firstMember = false;
                    WriteString(member.Name, builder);
                    builder.Append(':');
                    WriteValue(member.Value, builder, depth + 1);
                }

                builder.Append('}');
                return;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstElement = true;
                foreach (var element in value.EnumerateArray())
                {
                    if (!firstElement) builder.Append(',');
                    firstElement = false;
                    WriteValue(element, builder, depth + 1);
                }

                builder.Append(']');
                return;
            case JsonValueKind.String:
                // GetString is only null for JsonValueKind.Null, which this arm has excluded.
                WriteString(value.GetString()!, builder);
                return;
            case JsonValueKind.Number:
                builder.Append(NumberToHostString(value.GetRawText()));
                return;
            case JsonValueKind.True:
                builder.Append("true");
                return;
            case JsonValueKind.False:
                builder.Append("false");
                return;
            case JsonValueKind.Null:
                builder.Append("null");
                return;
            default:
                throw new ArgumentException(
                    $"Cannot render a config property value of kind {value.ValueKind} nested inside an array or object.",
                    nameof(value));
        }
    }

    /// <summary>
    ///     Append <paramref name="value" /> to <paramref name="builder" /> as a quoted JSON string, using
    ///     the host's escaping policy.
    /// </summary>
    /// <remarks>
    ///     The host escapes exactly the two characters JSON requires (<c>"</c> and <c>\</c>) plus the C0
    ///     control range, preferring the short forms where they exist and otherwise <c>\u00XX</c> with
    ///     uppercase hex digits; everything else it emits as raw UTF-8. This was measured by giving a real
    ///     process a runtimeconfig.json holding each of 291 sampled code points and recording what
    ///     <c>AppContext.GetData</c> returned, so DEL, Latin-1, U+2028, U+2029 and astral-plane characters
    ///     are all known to survive unescaped. <c>Test/host-escape-sweep.json</c> holds those observations
    ///     and the test suite replays every one of them against this method.
    /// </remarks>
    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');

        // Iterating chars rather than runes is deliberate: a surrogate pair is copied through as its two
        // halves, which reassemble into the same pair in the result, and no escaping decision depends on
        // the astral code point they denote.
        foreach (var c in value)
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    // Note JSON has no short form for U+000B; the host duly writes it as backslash-u-000B.
                    if (c < ' ')
                        builder.Append("\\u").Append(((int) c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);

                    break;
            }

        builder.Append('"');
    }

    /// <summary>
    ///     Render an entire "configProperties" object the way the .NET host renders it before handing it to
    ///     the runtime: the resulting keys and values are exactly what managed code will observe through
    ///     <c>AppContext.GetData</c>.
    /// </summary>
    /// <param name="properties">
    ///     The parsed "configProperties" object, e.g. <see cref="RuntimeOptions.ConfigProperties" />. A
    ///     null value (the file had no "configProperties" section at all) renders as an empty dictionary,
    ///     because a file which sets no properties and a file with an empty "configProperties" object are
    ///     indistinguishable to the runtime.
    /// </param>
    /// <returns>An ordinal-keyed dictionary of the host's property bag entries.</returns>
    /// <remarks>
    ///     A name containing an embedded NUL is registered under only the part before it, for the reason
    ///     given on <see cref="TruncateAtNul" />. Two names which become equal once truncated therefore
    ///     collide, and the later one in the file wins — measured, in both declaration orders. Iterating
    ///     the parsed object preserves document order, so assigning in that order reproduces it.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ToHostStrings(
        IReadOnlyDictionary<string, JsonElement>? properties)
    {
        // Ordinal, because that is how the runtime compares them: AppContext.Setup builds its store as a
        // plain Dictionary<string, object?>, whose default comparer for string keys is ordinal.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (properties == null) return result;

        foreach (var property in properties)
            result[TruncateAtNul(property.Key)] = ToHostString(property.Value);

        return result;
    }
}

/// <summary>
///     The contents of the "runtimeOptions" key in a runtimeconfig.json file.
/// </summary>
public record RuntimeOptions
{
    private readonly string _tfm = null!;

    /// <summary>
    ///     Target framework moniker, such as "net9.0". Never null: a file spelling it <c>null</c> is
    ///     rejected when it is parsed.
    /// </summary>
    [JsonPropertyName("tfm")]
    public required string Tfm
    {
        get => _tfm;
        init => _tfm = NonNullableMember.OrThrow(value, "tfm");
    }

    /// <summary>
    ///     The .NET runtime which this executable expects.
    ///     This is optional, because you can instead specify multiple Frameworks, in which case any of the frameworks
    ///     is acceptable (according to Claude; the MS docs are impenetrable as ever).
    /// </summary>
    [JsonPropertyName("framework")]
    public RuntimeConfigFramework? Framework { get; init; }

    private readonly IReadOnlyList<RuntimeConfigFramework>? _frameworks;
    private readonly IReadOnlyList<RuntimeConfigFramework>? _includedFrameworks;

    /// <summary>
    ///     Any of these runtimes by itself would be enough to run this executable.
    ///     It's much more normal to see a single `framework` instead of this.
    ///     The list may be absent, but none of its entries is ever null: a file spelling an entry
    ///     <c>null</c> is rejected when it is parsed, and what is stored is an unmodifiable copy, so
    ///     nothing can put a null there afterwards either.
    /// </summary>
    [JsonPropertyName("frameworks")]
    public IReadOnlyList<RuntimeConfigFramework>? Frameworks
    {
        get => _frameworks;
        init => _frameworks = NonNullableMember.ElementsOrThrow<RuntimeConfigFramework>(value, "frameworks");
    }

    /// <summary>
    ///     This is a self-contained executable which has these framework entirely contained next to it.
    ///     The list may be absent, but none of its entries is ever null: a file spelling an entry
    ///     <c>null</c> is rejected when it is parsed, and what is stored is an unmodifiable copy, so
    ///     nothing can put a null there afterwards either.
    /// </summary>
    [JsonPropertyName("includedFrameworks")]
    public IReadOnlyList<RuntimeConfigFramework>? IncludedFrameworks
    {
        get => _includedFrameworks;
        init => _includedFrameworks =
            NonNullableMember.ElementsOrThrow<RuntimeConfigFramework>(value, "includedFrameworks");
    }

    /// <summary>
    ///     This application advertises that it's fine with running under this roll-forward.
    /// </summary>
    [JsonPropertyName("rollForward")]
    public RollForward? RollForward { get; init; }

    /// <summary>
    ///     Runtime knobs this application wants set, such as
    ///     "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization". The .NET host merges these
    ///     into the property bag it hands the runtime at startup, where managed code reads them back through
    ///     <c>System.AppContext</c>; null means the file had no "configProperties" section.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Values are exposed as raw <see cref="JsonElement" />s, i.e. as what the file actually says,
    ///         because the host's own rendering is a lossy projection of them: it stringifies every value,
    ///         so a consumer who only ever sees the projection cannot tell the string "42" from the number
    ///         42. Use <see cref="ConfigProperties.ToHostStrings" /> to obtain that projection when you want
    ///         what the runtime will actually observe.
    ///     </para>
    ///     <para>
    ///         Note that this record's compiler-generated equality compares this property by reference, as it
    ///         already does for <see cref="Frameworks" /> and <see cref="IncludedFrameworks" />. Two
    ///         separately-parsed <see cref="RuntimeConfig" />s with identical "configProperties" therefore
    ///         compare unequal. Compare <see cref="ConfigProperties.ToHostStrings" /> of each instead;
    ///         <see cref="JsonElement" /> has no value equality of its own, so there is no comparer which
    ///         would make the record's own equality do the right thing here.
    ///     </para>
    /// </remarks>
    [JsonPropertyName("configProperties")]
    public IReadOnlyDictionary<string, JsonElement>? ConfigProperties { get; init; }
}

/// <summary>
///     The contents of a runtimeconfig.json file.
///     Note that this record doesn't capture everything: for example, "additionalProbingPaths" might be present in the
///     file, but is not represented in this type.
/// </summary>
public record RuntimeConfig
{
    private readonly RuntimeOptions _runtimeOptions = null!;

    /// <summary>
    ///     The contents of the file. Never null: a file spelling it <c>null</c> is rejected when it is
    ///     parsed, exactly as a file omitting it is.
    /// </summary>
    /// <remarks>
    ///     This is the one place where refusing a null is a policy of ours rather than the format's. The
    ///     real host (Microsoft.NETCore.App 9.0.0) rejects a file with no "runtimeOptions" member
    ///     ("Invalid runtimeconfig.json"), but treats <c>"runtimeOptions":null</c> as an empty options
    ///     object, which is fatal for a framework-dependent app ("did not specify a framework") and
    ///     harmless for a self-contained one. Since an empty options object is not
    ///     representable here either — <see cref="RuntimeOptions.Tfm" /> is itself non-nullable — the two
    ///     spellings of "this file says nothing" are treated alike rather than one of them being handed
    ///     back as a null.
    /// </remarks>
    [JsonPropertyName("runtimeOptions")]
    public required RuntimeOptions RuntimeOptions
    {
        get => _runtimeOptions;
        init => _runtimeOptions = NonNullableMember.OrThrow(value, "runtimeOptions");
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RuntimeConfig))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}
