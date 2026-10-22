using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AnyFS.Sources.ThreeDX;

/// <summary>Which of day and month comes first in a date written with numbers only (03/07/2018).</summary>
internal enum DateOrder
{
    DayFirst,
    MonthFirst,
}

/// <summary>
/// 3DX's dates (spec 7.6). A document's and a file's <c>originated</c> and <c>modified</c> come written for a locale, not as
/// ISO 8601: Mount3DX found them day-first on a server in Australia ("03/07/2018" for 3 July), right when read with the PC's
/// own date format and transposed when read as American. AnyFS runs with invariant globalization, so the PC's order is read
/// from its settings here (Windows' short date format; elsewhere LC_TIME, LC_ALL or LANG), or taken from the source's
/// setting. ISO 8601 and the other unambiguous forms parse as such; a time without a zone is taken as UTC, as Mount3DX
/// served it; a value whose numbers rule the order out (a month past 12) is read the other way round.
/// </summary>
internal static partial class ThreeDXDates
{
    public const string ThisComputer = "This computer's";
    public const string DayFirst = "Day first (31/12/2026)";
    public const string MonthFirst = "Month first (12/31/2026)";

    // 03/07/2018, 3.7.2018, 2018/07/03, 03-07-18, with an optional time (10:12, 10:12:33, 10:12:33.123, AM/PM) and zone
    [GeneratedRegex(@"^\s*(?<a>\d{1,4})[/.\-](?<b>\d{1,2})[/.\-](?<c>\d{1,4})(?:(?:\s+|T|,\s*)(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2})(?:[.,](?<f>\d{1,7}))?)?\s*(?<ampm>[AaPp]\.?\s?[Mm]\.?)?)?\s*(?<zone>Z|UTC|GMT|[+-]\d{2}:?\d{2})?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Numeric();

    /// <summary>The order a choice names; <see cref="ThisComputer"/> (or anything else) is the PC's own.</summary>
    public static DateOrder Resolve(string? choice) => (choice ?? "").Trim() switch
    {
        var c when c.Equals(DayFirst, StringComparison.OrdinalIgnoreCase) || c.StartsWith("Day", StringComparison.OrdinalIgnoreCase) => DateOrder.DayFirst,
        var c when c.Equals(MonthFirst, StringComparison.OrdinalIgnoreCase) || c.StartsWith("Month", StringComparison.OrdinalIgnoreCase) => DateOrder.MonthFirst,
        _ => MachineOrder(),
    };

    /// <summary>The order the PC writes dates in: Windows' short date format for the person, or the locale elsewhere.</summary>
    public static DateOrder MachineOrder()
    {
        if (OperatingSystem.IsWindows() && WindowsShortDate() is { Length: > 0 } pattern)
        {
            return OrderOfPattern(pattern);
        }

        var locale = new[] { "LC_ALL", "LC_TIME", "LANG" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return OrderOfLocale(locale);
    }

    /// <summary>The order of a .NET-style date pattern (d/MM/yyyy, M/d/yyyy): day before month or not; year-first patterns read numbers day-first.</summary>
    internal static DateOrder OrderOfPattern(string pattern)
    {
        var day = pattern.IndexOf('d', StringComparison.Ordinal);
        var month = pattern.IndexOf('M', StringComparison.Ordinal);
        var year = pattern.IndexOf('y', StringComparison.Ordinal);
        if (day < 0 || month < 0)
        {
            return DateOrder.MonthFirst;
        }

        if (year >= 0 && year < day && year < month)
        {
            return DateOrder.DayFirst;
        }

        return day < month ? DateOrder.DayFirst : DateOrder.MonthFirst;
    }

    /// <summary>The order a POSIX locale (en_AU.UTF-8) writes dates in: the United States' and a few others' month first, the rest day first; none (C, POSIX) month first.</summary>
    internal static DateOrder OrderOfLocale(string? locale)
    {
        var name = (locale ?? "").Split('.', '@')[0].Trim();
        if (name.Length == 0 || name is "C" or "POSIX")
        {
            return DateOrder.MonthFirst;
        }

        return name.Replace('-', '_') switch
        {
            "en_US" or "en_PH" or "en_AS" or "en_GU" or "en_MP" or "en_PR" or "en_UM" or "en_VI" or "es_US" or "fil_PH" or "en_CA" or "en_FM" or "en_MH" => DateOrder.MonthFirst,
            _ => DateOrder.DayFirst,
        };
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsShortDate()
    {
        try
        {
            using var international = Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
            return international?.GetValue("sShortDate") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>A 3DX date, or null when there is none or it cannot be read (or is before 1601, which no file system shows).</summary>
    public static DateTimeOffset? Parse(string? value, DateOrder order)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parsed = Numeric().Match(value) is { Success: true } match ? FromNumbers(match, order) : FromText(value);
        return parsed is { } date && date.UtcDateTime.Year >= 1601 ? date : null;
    }

    private static DateTimeOffset? FromNumbers(Match match, DateOrder order)
    {
        var a = match.Groups["a"].Value;
        var b = int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture);
        var c = match.Groups["c"].Value;
        int year, month, day;
        if (a.Length == 4)
        {
            // 2018/07/03: year first, then month and day
            (year, month, day) = (int.Parse(a, CultureInfo.InvariantCulture), b, int.Parse(c, CultureInfo.InvariantCulture));
        }
        else
        {
            var first = int.Parse(a, CultureInfo.InvariantCulture);
            year = int.Parse(c, CultureInfo.InvariantCulture);
            if (c.Length <= 2)
            {
                year += year < 50 ? 2000 : 1900;
            }

            (day, month) = order == DateOrder.DayFirst ? (first, b) : (b, first);
            if (month > 12 && day <= 12)
            {
                // the numbers rule the order out: it was written the other way round
                (day, month) = (month, day);
            }
        }

        if (month is < 1 or > 12 || day < 1 || year is < 1 or > 9999 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var hour = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var second = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        var ticks = match.Groups["f"].Success ? long.Parse(match.Groups["f"].Value.PadRight(7, '0'), CultureInfo.InvariantCulture) : 0;
        if (match.Groups["ampm"].Success)
        {
            var pm = char.ToUpperInvariant(match.Groups["ampm"].Value[0]) == 'P';
            if (hour is < 1 or > 12)
            {
                return null;
            }

            hour = hour % 12 + (pm ? 12 : 0);
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var offset = TimeSpan.Zero;
        var zone = match.Groups["zone"].Value;
        if (zone.Length > 0 && (zone[0] == '+' || zone[0] == '-'))
        {
            var digits = zone[1..].Replace(":", "", StringComparison.Ordinal);
            offset = new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture), int.Parse(digits[2..], CultureInfo.InvariantCulture), 0);
            if (zone[0] == '-')
            {
                offset = -offset;
            }
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, offset).AddTicks(ticks);
    }

    /// <summary>The unambiguous forms: ISO 8601, RFC 1123, a month by name ("Jul 3, 2018 10:12:33 AM"); a time without a zone as UTC.</summary>
    private static DateTimeOffset? FromText(string value)
        => DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
}
