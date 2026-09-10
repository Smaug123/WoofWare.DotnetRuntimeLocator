using System;
using System.Diagnostics;

namespace WoofWare.DotnetRuntimeLocator;

/// <summary>
///     A framework version as hostfxr understands one: a simplified SemVer 2.0 version, being
///     <c>major.minor.patch</c> followed by an optional prerelease label and an optional build label.
/// </summary>
/// <remarks>
///     <para>
///         This mirrors hostfxr's <c>fx_ver_t</c> (<c>src/native/corehost/hostmisc/fx_ver.c</c>), which is
///         the type every framework-selection decision in <c>fx_resolver.cpp</c> is made against.
///     </para>
///     <para>
///         Two deliberate departures from hostfxr, both places where hostfxr relies on C undefined or
///         wrapping behaviour:
///     </para>
///     <list type="bullet">
///         <item>
///             A numeric component which does not fit in an <see cref="int" /> is a parse failure here.
///             hostfxr feeds it to <c>strtoul</c> without checking for overflow, so <c>4294967296.0.0</c>
///             gives it a major of 0 and a large value gives it -1, which its own <c>is_empty()</c> then
///             reads as "no version at all".
///         </item>
///         <item>
///             Numeric prerelease identifiers are compared at full width. hostfxr truncates them to
///             <c>unsigned</c>, so two identifiers above 2^32 which differ can compare equal there.
///         </item>
///     </list>
/// </remarks>
internal sealed class FxVersion : IComparable<FxVersion>, IComparable, IEquatable<FxVersion>
{
    /// <summary>
    ///     hostfxr's <c>try_stou</c> copies the digits into a 32-character buffer and refuses anything which
    ///     does not fit.
    /// </summary>
    private const int MaxNumericLength = 31;

    private FxVersion(int major, int minor, int patch, string pre, string build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Pre = pre;
        Build = build;
    }

    /// <summary>The major component.</summary>
    public int Major { get; }

    /// <summary>The minor component.</summary>
    public int Minor { get; }

    /// <summary>The patch component.</summary>
    public int Patch { get; }

    /// <summary>
    ///     The prerelease label including its leading '-', or the empty string when there is none.
    ///     The leading '-' is part of the stored value, exactly as in hostfxr.
    /// </summary>
    public string Pre { get; }

    /// <summary>
    ///     The build label including its leading '+', or the empty string when there is none.
    ///     Build metadata takes no part in ordering or equality; see <see cref="CompareTo(FxVersion)" />.
    /// </summary>
    public string Build { get; }

    /// <summary>Whether this version carries a prerelease label, and so is not a released version.</summary>
    public bool IsPrerelease => Pre.Length > 0;

    /// <summary>
    ///     Order two versions by SemVer 2.0 precedence, as hostfxr's <c>c_fx_ver_compare</c> does:
    ///     major, then minor, then patch numerically; a release version above any prerelease of the same
    ///     three components; and two prereleases by their dot-separated identifiers left to right, where a
    ///     numeric identifier compares numerically and ranks below an alphanumeric one, and a version which
    ///     runs out of identifiers first ranks lower. Build metadata is ignored throughout.
    /// </summary>
    public int CompareTo(FxVersion? other)
    {
        if (other is null) return 1;

        if (Major != other.Major) return Major > other.Major ? 1 : -1;
        if (Minor != other.Minor) return Minor > other.Minor ? 1 : -1;
        if (Patch != other.Patch) return Patch > other.Patch ? 1 : -1;

        if (!IsPrerelease || !other.IsPrerelease)
            // Release outranks prerelease; two releases are equal.
            return IsPrerelease ? -1 : other.IsPrerelease ? 1 : 0;

        return ComparePrerelease(Pre, other.Pre);
    }

    /// <summary>
    ///     Order against another <see cref="FxVersion" />.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="obj" /> is not an <see cref="FxVersion" />.</exception>
    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            FxVersion other => CompareTo(other),
            _ => throw new ArgumentException($"Cannot compare an FxVersion with a {obj.GetType()}", nameof(obj))
        };
    }

    /// <summary>
    ///     Whether these versions have the same precedence. Build metadata is ignored, so <c>1.0.0+a</c> and
    ///     <c>1.0.0+b</c> are equal here, matching hostfxr's <c>operator==</c>.
    /// </summary>
    public bool Equals(FxVersion? other)
    {
        return other is not null && CompareTo(other) == 0;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is FxVersion other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Consistent with Equals: build metadata is excluded.
        return HashCode.Combine(Major, Minor, Patch, Pre);
    }

    /// <summary>
    ///     Compare two prerelease labels, each including its leading '-' and each known to be non-empty.
    /// </summary>
    private static int ComparePrerelease(string a, string b)
    {
        Debug.Assert(a[0] == '-', "prerelease label must retain its leading '-'");
        Debug.Assert(b[0] == '-', "prerelease label must retain its leading '-'");

        var aIds = a.Substring(1).Split('.');
        var bIds = b.Substring(1).Split('.');

        for (var i = 0; i < aIds.Length && i < bIds.Length; i++)
        {
            var cmp = CompareIdentifier(aIds[i], bIds[i]);
            if (cmp != 0) return cmp;
        }

        // "A larger set of pre-release fields has a higher precedence than a smaller set, if all of the
        // preceding identifiers are equal." https://semver.org/#spec-item-11
        if (aIds.Length == bIds.Length) return 0;
        return aIds.Length > bIds.Length ? 1 : -1;
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = IsNumericIdentifier(a);
        var bNumeric = IsNumericIdentifier(b);

        if (aNumeric && bNumeric)
        {
            // Parsing forbids leading zeros on a numeric identifier, so for digit strings the numeric
            // order is the shorter-first order, then ordinal. That avoids hostfxr's fixed-width truncation.
            if (a.Length != b.Length) return a.Length > b.Length ? 1 : -1;
            return string.CompareOrdinal(a, b);
        }

        // "Numeric identifiers always have lower precedence than alphanumeric identifiers."
        if (aNumeric || bNumeric) return bNumeric ? 1 : -1;

        // hostfxr compares the shared prefix with strncmp and breaks a tie on length, which is what
        // CompareOrdinal does for two strings where one is a prefix of the other.
        var cmp = string.CompareOrdinal(a, b);
        return cmp == 0 ? 0 : cmp > 0 ? 1 : -1;
    }

    /// <summary>
    ///     Whether <paramref name="id" /> is a numeric identifier for ordering purposes, as hostfxr's
    ///     <c>try_stou</c> decides it.
    /// </summary>
    private static bool IsNumericIdentifier(string id)
    {
        if (id.Length == 0 || id.Length > MaxNumericLength) return false;

        foreach (var c in id)
            if (c is < '0' or > '9')
                return false;

        return true;
    }

    /// <summary>
    ///     Parse a version string as hostfxr's <c>fx_ver_t::parse</c> does.
    /// </summary>
    /// <param name="s">The version string, for example "9.0.5" or "11.0.0-preview.7.26381.103".</param>
    /// <returns>The parsed version, or null when <paramref name="s" /> is not one hostfxr would accept.</returns>
    public static FxVersion? ParseOrNull(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;

        var majDot = s.IndexOf('.');
        if (majDot < 0) return null;
        if (!TryParseComponent(s, 0, majDot, out var major)) return null;

        var minStart = majDot + 1;
        var minDot = s.IndexOf('.', minStart);
        if (minDot < 0) return null;
        if (!TryParseComponent(s, minStart, minDot - minStart, out var minor)) return null;

        var patStart = minDot + 1;
        var patEnd = IndexOfNonNumeric(s, patStart);

        if (patEnd < 0)
        {
            // The whole remainder is the patch, so there is no prerelease or build label.
            if (!TryParseComponent(s, patStart, s.Length - patStart, out var wholePatch)) return null;

            return new FxVersion(major, minor, wholePatch, "", "");
        }

        if (!TryParseComponent(s, patStart, patEnd - patStart, out var patch)) return null;

        var preStart = patEnd;
        var buildStart = s.IndexOf('+', preStart);
        var preLength = buildStart >= 0 ? buildStart - preStart : s.Length - preStart;

        if (preLength > 0)
        {
            if (s[preStart] != '-') return null;
            if (!ValidDotSeparatedIdentifiers(s, preStart + 1, preLength - 1, false)) return null;
        }

        var buildLength = buildStart >= 0 ? s.Length - buildStart : 0;
        if (buildLength > 0 && !ValidDotSeparatedIdentifiers(s, buildStart + 1, buildLength - 1, true))
            return null;

        return new FxVersion(
            major,
            minor,
            patch,
            s.Substring(preStart, preLength),
            buildStart >= 0 ? s.Substring(buildStart, buildLength) : "");
    }

    /// <summary>
    ///     Parse a version string as hostfxr's <c>fx_ver_t::parse</c> does, throwing when it would fail.
    /// </summary>
    /// <param name="s">The version string.</param>
    /// <param name="describeSubject">
    ///     What the version belongs to, to name it in the exception; for example
    ///     "framework 'Microsoft.NETCore.App'".
    /// </param>
    /// <exception cref="FormatException"><paramref name="s" /> is not a version hostfxr would accept.</exception>
    public static FxVersion Parse(string? s, string describeSubject)
    {
        var version = ParseOrNull(s);
        if (version is not null) return version;

        throw new FormatException(
            $"The version of {describeSubject} was '{s}', which is not a version the .NET host can parse. "
            + "It must be major.minor.patch, optionally followed by '-' and a prerelease label and '+' and a "
            + "build label, with no leading zeros on any numeric part.");
    }

    /// <summary>
    ///     Parse one of the three numeric components: a non-empty run of digits with no leading zero, whose
    ///     value fits in an <see cref="int" />. That bound is tighter than hostfxr's
    ///     <see cref="MaxNumericLength" /> buffer, so the length is not checked separately here.
    /// </summary>
    private static bool TryParseComponent(string s, int start, int length, out int value)
    {
        value = 0;
        if (length <= 0) return false;

        for (var i = start; i < start + length; i++)
            if (s[i] is < '0' or > '9')
                return false;

        // https://semver.org/#spec-item-2: version numbers must not have leading zeros.
        if (length > 1 && s[start] == '0') return false;

        return int.TryParse(s.AsSpan(start, length), out value);
    }

    /// <summary>
    ///     The index of the first character at or after <paramref name="start" /> which is not a digit, or -1
    ///     if there is none.
    /// </summary>
    private static int IndexOfNonNumeric(string s, int start)
    {
        for (var i = start; i < s.Length; i++)
            if (s[i] is < '0' or > '9')
                return i;

        return -1;
    }

    /// <summary>
    ///     Whether the <paramref name="length" /> characters from <paramref name="start" /> are a valid
    ///     dot-separated identifier list.
    /// </summary>
    /// <param name="s">The string holding the identifier list.</param>
    /// <param name="start">Index at which the identifier list begins.</param>
    /// <param name="length">Length of the identifier list.</param>
    /// <param name="buildMeta">
    ///     Whether this is build metadata, where a numeric identifier may have leading zeros.
    /// </param>
    private static bool ValidDotSeparatedIdentifiers(string s, int start, int length, bool buildMeta)
    {
        var idStart = start;
        for (var i = start; i <= start + length; i++)
            if (i == start + length || s[i] == '.')
            {
                if (!ValidIdentifier(s, idStart, i - idStart, buildMeta)) return false;

                idStart = i + 1;
            }

        return true;
    }

    /// <summary>
    ///     Whether one dot-separated identifier is valid: non-empty, drawn from [0-9A-Za-z-], and -- outside
    ///     build metadata -- not a numeric identifier padded with leading zeros.
    /// </summary>
    private static bool ValidIdentifier(string s, int start, int length, bool buildMeta)
    {
        if (length == 0) return false;

        var allNumeric = true;
        for (var i = start; i < start + length; i++)
        {
            var c = s[i];
            var isDigit = c is >= '0' and <= '9';
            if (!isDigit) allNumeric = false;

            // https://semver.org/#spec-item-9 and #spec-item-10: ASCII alphanumerics and hyphens only.
            if (!isDigit && c is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not '-') return false;
        }

        // https://semver.org/#spec-item-9: numeric prerelease identifiers must not be padded with zeros.
        if (!buildMeta && length > 1 && s[start] == '0' && allNumeric) return false;

        return true;
    }

    /// <summary>The version as hostfxr would render it, which round-trips through <see cref="ParseOrNull" />.</summary>
    public override string ToString()
    {
        return $"{Major}.{Minor}.{Patch}{Pre}{Build}";
    }

    public static bool operator <(FxVersion a, FxVersion b)
    {
        return a.CompareTo(b) < 0;
    }

    public static bool operator >(FxVersion a, FxVersion b)
    {
        return a.CompareTo(b) > 0;
    }

    public static bool operator <=(FxVersion a, FxVersion b)
    {
        return a.CompareTo(b) <= 0;
    }

    public static bool operator >=(FxVersion a, FxVersion b)
    {
        return a.CompareTo(b) >= 0;
    }

    public static bool operator ==(FxVersion? a, FxVersion? b)
    {
        return a is null ? b is null : a.Equals(b);
    }

    public static bool operator !=(FxVersion? a, FxVersion? b)
    {
        return !(a == b);
    }
}
