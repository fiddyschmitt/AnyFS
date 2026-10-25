using System.Buffers.Binary;
using System.Text;

namespace LibOutlook;

/// <summary>
/// A time zone as Outlook keeps one with an appointment ([MS-OXOCAL] 2.2.1.39 PidLidTimeZoneStruct, 2.2.1.41 TZDEFINITION):
/// a bias from UTC and, where the zone keeps daylight saving time, the yearly dates it starts and ends, one rule per span
/// of years. It converts the local times a recurrence is written in to UTC as Outlook does: by the rules the file holds,
/// whatever the system's own zone database says, on any platform.
/// </summary>
public sealed class PstTimeZone
{
    private readonly List<Rule> _rules;

    private PstTimeZone(string? name, List<Rule> rules)
    {
        Name = name;
        _rules = rules;
    }

    /// <summary>The zone's Windows name ("Pacific Standard Time"), when its definition gives one.</summary>
    public string? Name { get; }

    /// <summary>The zone in a TZDEFINITION (PidLidAppointmentTimeZoneDefinitionRecur and its kin); null when the bytes are none.</summary>
    public static PstTimeZone? FromDefinition(ReadOnlySpan<byte> data)
    {
        // bMajorVersion 2, bMinorVersion, cbHeader (Reserved, cchKeyName, KeyName, cRules), then the 66-byte rules
        if (data.Length < 10 || data[0] != 0x02)
        {
            return null;
        }

        int header = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (8 + nameLength * 2 + 2 > data.Length || 4 + header > data.Length)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(data.Slice(8, nameLength * 2));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[(8 + nameLength * 2)..]);
        var rules = new List<Rule>(count);
        for (int i = 0, at = 4 + header; i < count && at + 66 <= data.Length; i++, at += 66)
        {
            // TZRule: versions, reserved, flags, wYear, 14 unused bytes, lBias, lStandardBias, lDaylightBias, stStandardDate, stDaylightDate
            var rule = data.Slice(at, 66);
            rules.Add(new Rule(
                BinaryPrimitives.ReadUInt16LittleEndian(rule[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(rule[22..]),
                BinaryPrimitives.ReadInt32LittleEndian(rule[26..]),
                BinaryPrimitives.ReadInt32LittleEndian(rule[30..]),
                SystemTime.Read(rule[34..]),
                SystemTime.Read(rule[50..])));
        }

        return rules.Count == 0 ? null : new PstTimeZone(name.Length == 0 ? null : name, [.. rules.OrderBy(r => r.Year)]);
    }

    /// <summary>The zone in a PidLidTimeZoneStruct (one rule for every year); null when the bytes are too few.</summary>
    public static PstTimeZone? FromStruct(ReadOnlySpan<byte> data)
    {
        // lBias, lStandardBias, lDaylightBias, wStandardYear, stStandardDate, wDaylightYear, stDaylightDate: 48 bytes
        if (data.Length < 48)
        {
            return null;
        }

        return new PstTimeZone(null,
        [
            new Rule(0, BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data[4..]), BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
                SystemTime.Read(data[14..]), SystemTime.Read(data[32..])),
        ]);
    }

    /// <summary>A zone a fixed offset from UTC, with no daylight saving time.</summary>
    public static PstTimeZone Fixed(TimeSpan offsetFromUtc) => new(null, [new Rule(0, -(int)offsetFromUtc.TotalMinutes, 0, 0, default, default)]);

    /// <summary>The UTC instant of a local time in this zone; a time the change to daylight time skips is read as daylight time.</summary>
    public DateTimeOffset ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var rule = RuleFor(local.Year);
        var bias = rule.Bias + (rule.IsDaylight(local) ? rule.DaylightBias : rule.StandardBias);
        return new DateTimeOffset(local.AddMinutes(bias), TimeSpan.Zero);
    }

    /// <summary>The local time of a UTC instant in this zone.</summary>
    public DateTime ToLocal(DateTimeOffset utc)
    {
        var instant = utc.UtcDateTime;
        var rule = RuleFor(instant.Year);
        var daylight = DateTime.SpecifyKind(instant.AddMinutes(-(rule.Bias + rule.DaylightBias)), DateTimeKind.Unspecified);
        return rule.IsDaylight(daylight) ? daylight : DateTime.SpecifyKind(instant.AddMinutes(-(rule.Bias + rule.StandardBias)), DateTimeKind.Unspecified);
    }

    /// <summary>The rule for a year: the last whose year is at or before it ([MS-OXOCAL] 2.2.1.41.1), else the first.</summary>
    private Rule RuleFor(int year)
    {
        var chosen = _rules[0];
        foreach (var rule in _rules)
        {
            if (rule.Year <= year)
            {
                chosen = rule;
            }
        }

        return chosen;
    }

    /// <summary>A rule: UTC = local + Bias + StandardBias in standard time, + DaylightBias in daylight time (minutes).</summary>
    private readonly record struct Rule(int Year, int Bias, int StandardBias, int DaylightBias, SystemTime StandardDate, SystemTime DaylightDate)
    {
        public bool IsDaylight(DateTime local)
        {
            if (StandardDate.Month == 0 || DaylightDate.Month == 0)
            {
                return false;
            }

            // the change to daylight time is written in standard time, the change back in daylight time
            if (DaylightDate.In(local.Year) is not { } start || StandardDate.In(local.Year) is not { } end)
            {
                return false;
            }

            return start < end ? local >= start && local < end : local >= start || local < end;
        }
    }

    /// <summary>A SYSTEMTIME as a transition rule: with no year, the wDay-th (5 the last) wDayOfWeek of wMonth at wHour:wMinute, every year.</summary>
    private readonly record struct SystemTime(int Year, int Month, int DayOfWeek, int Day, int Hour, int Minute, int Second)
    {
        public static SystemTime Read(ReadOnlySpan<byte> data) => new(
            BinaryPrimitives.ReadUInt16LittleEndian(data), BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]));

        /// <summary>When the transition falls in a year; null when it does not fall in that year or is no date.</summary>
        public DateTime? In(int year)
        {
            if (Month is < 1 or > 12 || Hour > 23 || Minute > 59 || Second > 59)
            {
                return null;
            }

            if (Year != 0)
            {
                // an absolute date, that year only
                return Year == year && Day >= 1 && Day <= DateTime.DaysInMonth(year, Month) ? new DateTime(year, Month, Day, Hour, Minute, Second) : null;
            }

            if (Day is < 1 or > 5 || DayOfWeek > 6)
            {
                return null;
            }

            var first = new DateTime(year, Month, 1);
            var day = 1 + ((DayOfWeek - (int)first.DayOfWeek + 7) % 7) + (Day - 1) * 7;
            while (day > DateTime.DaysInMonth(year, Month))
            {
                day -= 7;
            }

            return new DateTime(year, Month, day, Hour, Minute, Second);
        }
    }
}
