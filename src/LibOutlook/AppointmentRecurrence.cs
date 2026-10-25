using System.Buffers.Binary;
using System.Text;
using LibOutlook.Ltp;

namespace LibOutlook;

/// <summary>How often a series recurs ([MS-OXOCAL] 2.2.1.44.1, RecurFrequency).</summary>
public enum RecurrenceFrequency
{
    /// <summary>Every so many days.</summary>
    Daily = 0x200A,

    /// <summary>Every so many weeks.</summary>
    Weekly = 0x200B,

    /// <summary>Every so many months.</summary>
    Monthly = 0x200C,

    /// <summary>Every year (a period of 12 months).</summary>
    Yearly = 0x200D,
}

/// <summary>What a series' pattern is (PatternType): which days of a valid day, week or month it falls on.</summary>
public enum RecurrencePatternType
{
    /// <summary>Every Period minutes (whole days).</summary>
    Day = 0x0000,

    /// <summary>The days of a mask, every Period weeks.</summary>
    Week = 0x0001,

    /// <summary>A day of the month (the month's last when it is shorter), every Period months.</summary>
    Month = 0x0002,

    /// <summary>The first to fourth, or last, of the days of a mask, every Period months.</summary>
    MonthNth = 0x0003,

    /// <summary>The month's last day, every Period months.</summary>
    MonthEnd = 0x0004,

    /// <summary>A Hijri-calendar month pattern.</summary>
    HjMonth = 0x000A,

    /// <summary>A Hijri-calendar nth-day pattern.</summary>
    HjMonthNth = 0x000B,

    /// <summary>A Hijri-calendar month-end pattern.</summary>
    HjMonthEnd = 0x000C,
}

/// <summary>How a series ends (EndType).</summary>
public enum RecurrenceEnd
{
    /// <summary>The series ends on a date.</summary>
    AfterDate = 0x2021,

    /// <summary>The series ends after a number of occurrences.</summary>
    AfterCount = 0x2022,

    /// <summary>The series has no end.</summary>
    Never = 0x2023,
}

/// <summary>
/// A modified occurrence of a series ([MS-OXOCAL] 2.2.1.44.2 ExceptionInfo, with ExtendedException's Unicode strings):
/// local times in the series' zone, and what the occurrence has of its own.
/// </summary>
/// <param name="OriginalStart">When the occurrence was to start, as the pattern gives it.</param>
/// <param name="Start">When it starts now.</param>
/// <param name="End">When it ends now.</param>
/// <param name="Subject">Its own subject, or null when it keeps the series'.</param>
/// <param name="Location">Its own location, or null when it keeps the series'.</param>
/// <param name="BusyStatus">Its own free/busy status (0 free, 1 tentative, 2 busy, 3 out of office, 4 working elsewhere), or null.</param>
/// <param name="HasOwnBody">Whether its body is its own (ARO_EXCEPTIONAL_BODY): in its exception attachment's embedded message.</param>
/// <param name="OverrideFlags">The ExceptionInfo OverrideFlags: which of the series' values the occurrence has of its own.</param>
public sealed record RecurrenceException(DateTime OriginalStart, DateTime Start, DateTime End, string? Subject, string? Location, int? BusyStatus, bool HasOwnBody, int OverrideFlags);

/// <summary>An occurrence of a series: when it starts and ends (UTC), and its exception when it was changed.</summary>
/// <param name="OriginalStart">When the pattern has it start, local to the series' zone: what tells occurrences apart.</param>
/// <param name="Start">When it starts.</param>
/// <param name="End">When it ends.</param>
/// <param name="Exception">What it has of its own, for a modified occurrence; null for one as the series has it.</param>
public sealed record AppointmentOccurrence(DateTime OriginalStart, DateTimeOffset Start, DateTimeOffset End, RecurrenceException? Exception);

/// <summary>
/// A recurring appointment's series ([MS-OXOCAL] 2.2.1.44 PidLidAppointmentRecur, the AppointmentRecurrencePattern
/// structure): the pattern, its first and last dates, the occurrences deleted and modified, and the local times each
/// occurrence starts and ends at, in the zone the appointment keeps for it (<see cref="TimeZone"/>). <see cref="Occurrences"/>
/// lists the occurrences the way Outlook does: every pattern date not deleted, at the series' times, plus each modified
/// one at its own; a month shorter than a monthly pattern's day gets the occurrence on its last day, as Outlook does.
/// Gregorian patterns only: a Hijri one (<see cref="CanExpand"/> false) lists nothing.
/// </summary>
public sealed class AppointmentRecurrence
{
    private const uint NoEndDate = 0x5AE980DF;
    private static readonly DateTime Epoch = new(1601, 1, 1);

    private AppointmentRecurrence()
    {
    }

    /// <summary>How often the series recurs.</summary>
    public RecurrenceFrequency Frequency { get; private init; }

    /// <summary>What the pattern is.</summary>
    public RecurrencePatternType PatternType { get; private init; }

    /// <summary>CalendarType: 0 the default, 1 and up the CAL_ values.</summary>
    public int CalendarType { get; private init; }

    /// <summary>The first day, week or month of the series, in minutes from 1601: what makes a day, week or month valid.</summary>
    public uint FirstDateTime { get; private init; }

    /// <summary>Minutes (whole days) for a daily pattern, weeks for a weekly one, months for a monthly one (12 a year).</summary>
    public uint Period { get; private init; }

    /// <summary>The days of the week of a Week or MonthNth pattern: bit 0 Sunday to bit 6 Saturday.</summary>
    public int DaysOfWeek { get; private init; }

    /// <summary>The day of the month of a Month pattern.</summary>
    public int DayOfMonth { get; private init; }

    /// <summary>Which of the days a MonthNth pattern falls on: 1 to 4, 5 the last.</summary>
    public int Instance { get; private init; }

    /// <summary>How the series ends.</summary>
    public RecurrenceEnd EndType { get; private init; }

    /// <summary>How many occurrences the series has, when it ends after a number of them.</summary>
    public int OccurrenceCount { get; private init; }

    /// <summary>The day a week starts on, for a weekly pattern.</summary>
    public DayOfWeek FirstDayOfWeek { get; private init; }

    /// <summary>The dates (midnight, local) of occurrences deleted, and of modified ones as the pattern had them.</summary>
    public IReadOnlyList<DateTime> DeletedInstanceDates { get; private init; } = [];

    /// <summary>The dates (midnight, local) modified occurrences fall on.</summary>
    public IReadOnlyList<DateTime> ModifiedInstanceDates { get; private init; } = [];

    /// <summary>The date (midnight, local) of the first occurrence.</summary>
    public DateTime StartDate { get; private init; }

    /// <summary>The date (midnight, local) of the last occurrence; null for a series without end.</summary>
    public DateTime? EndDate { get; private init; }

    /// <summary>Minutes from midnight at which each occurrence starts.</summary>
    public int StartTimeOffset { get; private init; }

    /// <summary>Minutes from midnight at which each occurrence ends (beyond a day for one that runs over midnight).</summary>
    public int EndTimeOffset { get; private init; }

    /// <summary>The modified occurrences.</summary>
    public IReadOnlyList<RecurrenceException> Exceptions { get; private init; } = [];

    /// <summary>The zone the local times are in; null when the appointment keeps none (they are then read as UTC).</summary>
    public PstTimeZone? TimeZone { get; internal set; }

    /// <summary>Whether <see cref="Occurrences"/> can list the series: a Gregorian pattern of a known kind.</summary>
    public bool CanExpand => PatternType is RecurrencePatternType.Day or RecurrencePatternType.Week or RecurrencePatternType.Month or RecurrencePatternType.MonthNth or RecurrencePatternType.MonthEnd
        && CalendarType is 0x0 or 0x1 or 0x2 or 0x3 or 0x4 or 0x5 or 0x7 or 0x9 or 0xA or 0xB or 0xC
        && Period > 0;

    /// <summary>Reads an AppointmentRecurrencePattern; a structure that does not read is a <see cref="PstException"/>.</summary>
    /// <param name="data">The PidLidAppointmentRecur bytes.</param>
    /// <param name="codePage">The code page of the exceptions' non-Unicode strings, used where no Unicode copy follows.</param>
    public static AppointmentRecurrence Parse(ReadOnlySpan<byte> data, int codePage = 1252)
    {
        var r = new Reader(data);
        var readerVersion = r.U16();
        r.U16();
        if (readerVersion != 0x3004)
        {
            throw new PstException($"a recurrence pattern of version 0x{readerVersion:X4}, not 0x3004");
        }

        var frequency = (RecurrenceFrequency)r.U16();
        var patternType = (RecurrencePatternType)r.U16();
        var calendarType = r.U16();
        var firstDateTime = r.U32();
        var period = r.U32();
        r.U32(); // SlidingFlag: tasks only
        int days = 0, dayOfMonth = 0, instance = 0;
        switch (patternType)
        {
            case RecurrencePatternType.Day:
                break;
            case RecurrencePatternType.Week:
                days = (int)r.U32();
                break;
            case RecurrencePatternType.Month or RecurrencePatternType.MonthEnd or RecurrencePatternType.HjMonth or RecurrencePatternType.HjMonthEnd:
                dayOfMonth = (int)r.U32();
                break;
            case RecurrencePatternType.MonthNth or RecurrencePatternType.HjMonthNth:
                days = (int)r.U32();
                instance = (int)r.U32();
                break;
            default:
                throw new PstException($"a recurrence pattern of type 0x{(int)patternType:X4}");
        }

        var endType = r.U32();
        var occurrenceCount = (int)r.U32();
        var firstDayOfWeek = r.U32();
        var deleted = r.Dates((int)r.U32());
        var modified = r.Dates((int)r.U32());
        var startDate = r.U32();
        var endDate = r.U32();

        // the appointment's part: its times and its modified occurrences
        r.U32(); // ReaderVersion2
        var writerVersion2 = r.U32();
        var startOffset = (int)r.U32();
        var endOffset = (int)r.U32();
        int count = r.U16();
        var infos = new List<(uint Start, uint End, uint Original, int Flags, string? Subject, string? Location, int? Busy)>(count);
        for (var i = 0; i < count; i++)
        {
            var start = r.U32();
            var end = r.U32();
            var original = r.U32();
            int flags = r.U16();
            string? subject = null, location = null;
            int? busy = null;
            if ((flags & 0x0001) != 0)
            {
                r.U16();
                subject = PropertyValues.DecodeString8(r.Bytes(r.U16()).ToArray(), codePage);
            }

            if ((flags & 0x0002) != 0)
            {
                r.U32(); // MeetingType
            }

            if ((flags & 0x0004) != 0)
            {
                r.U32(); // ReminderDelta
            }

            if ((flags & 0x0008) != 0)
            {
                r.U32(); // ReminderSet
            }

            if ((flags & 0x0010) != 0)
            {
                r.U16();
                location = PropertyValues.DecodeString8(r.Bytes(r.U16()).ToArray(), codePage);
            }

            if ((flags & 0x0020) != 0)
            {
                busy = (int)r.U32();
            }

            if ((flags & 0x0040) != 0)
            {
                r.U32(); // Attachment
            }

            if ((flags & 0x0080) != 0)
            {
                r.U32(); // SubType
            }

            if ((flags & 0x0100) != 0)
            {
                r.U32(); // AppointmentColor
            }

            infos.Add((start, end, original, flags, subject, location, busy));
        }

        // the Unicode copies of the exceptions' subjects and locations, where the writer gave them
        if (r.Remaining >= 4)
        {
            r.Skip((int)r.U32()); // ReservedBlock1
            try
            {
                for (var i = 0; i < infos.Count; i++)
                {
                    if (writerVersion2 >= 0x3009)
                    {
                        r.Skip((int)r.U32()); // ChangeHighlight
                    }

                    r.Skip((int)r.U32()); // ReservedBlockEE1
                    var info = infos[i];
                    if ((info.Flags & 0x0011) == 0)
                    {
                        continue;
                    }

                    r.U32();
                    r.U32();
                    r.U32();
                    if ((info.Flags & 0x0001) != 0)
                    {
                        info.Subject = Encoding.Unicode.GetString(r.Bytes(r.U16() * 2));
                    }

                    if ((info.Flags & 0x0010) != 0)
                    {
                        info.Location = Encoding.Unicode.GetString(r.Bytes(r.U16() * 2));
                    }

                    r.Skip((int)r.U32()); // ReservedBlockEE2
                    infos[i] = info;
                }
            }
            catch (PstException)
            {
                // the non-Unicode strings stand
            }
        }

        return new AppointmentRecurrence
        {
            Frequency = frequency,
            PatternType = patternType,
            CalendarType = calendarType,
            FirstDateTime = firstDateTime,
            Period = period,
            DaysOfWeek = days & 0x7F,
            DayOfMonth = dayOfMonth,
            Instance = instance,
            EndType = endType switch { 0x2021 => RecurrenceEnd.AfterDate, 0x2022 => RecurrenceEnd.AfterCount, _ => RecurrenceEnd.Never },
            OccurrenceCount = occurrenceCount,
            FirstDayOfWeek = (DayOfWeek)(firstDayOfWeek % 7),
            DeletedInstanceDates = deleted,
            ModifiedInstanceDates = modified,
            StartDate = Minutes(startDate),
            EndDate = endDate == NoEndDate || endType is not (0x2021 or 0x2022) ? null : Minutes(endDate),
            StartTimeOffset = startOffset,
            EndTimeOffset = endOffset,
            Exceptions = [.. infos.Select(i => new RecurrenceException(Minutes(i.Original), Minutes(i.Start), Minutes(i.End), i.Subject, i.Location, i.Busy, (i.Flags & 0x0200) != 0, i.Flags))],
        };
    }

    /// <summary>
    /// The series' occurrences in order of their start, up to <paramref name="untilUtc"/> (exclusive) and at most
    /// <paramref name="limit"/> of them: each pattern date not deleted at the series' times, and each modified
    /// occurrence at its own. Empty when the pattern cannot be listed (<see cref="CanExpand"/>).
    /// </summary>
    public IReadOnlyList<AppointmentOccurrence> Occurrences(DateTimeOffset untilUtc, int limit = 10_000)
    {
        if (!CanExpand || limit <= 0)
        {
            return [];
        }

        var bound = new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        untilUtc = untilUtc > bound ? bound : untilUtc;
        var zone = TimeZone ?? PstTimeZone.Fixed(TimeSpan.Zero);
        var through = zone.ToLocal(untilUtc).Date;
        var deleted = new HashSet<DateTime>(DeletedInstanceDates);
        var list = new List<AppointmentOccurrence>();
        foreach (var date in PatternDates(through))
        {
            if (deleted.Contains(date))
            {
                continue;
            }

            var start = zone.ToUtc(date.AddMinutes(StartTimeOffset));
            if (start >= untilUtc)
            {
                break;
            }

            list.Add(new AppointmentOccurrence(date.AddMinutes(StartTimeOffset), start, zone.ToUtc(date.AddMinutes(EndTimeOffset)), null));
            if (list.Count >= limit)
            {
                break;
            }
        }

        foreach (var exception in Exceptions)
        {
            var start = zone.ToUtc(exception.Start);
            if (start < untilUtc)
            {
                list.Add(new AppointmentOccurrence(exception.OriginalStart, start, zone.ToUtc(exception.End), exception));
            }
        }

        return [.. list.OrderBy(o => o.Start).ThenBy(o => o.OriginalStart).Take(limit)];
    }

    /// <summary>
    /// The occurrence the pattern starts at <paramref name="originalStart"/> (local to the series' zone), as it is now:
    /// moved and changed when it was modified; null when the series has none there, or it was deleted.
    /// </summary>
    public AppointmentOccurrence? Occurrence(DateTime originalStart)
    {
        if (!CanExpand)
        {
            return null;
        }

        var zone = TimeZone ?? PstTimeZone.Fixed(TimeSpan.Zero);
        if (Exceptions.FirstOrDefault(e => e.OriginalStart == originalStart) is { } exception)
        {
            return new AppointmentOccurrence(exception.OriginalStart, zone.ToUtc(exception.Start), zone.ToUtc(exception.End), exception);
        }

        var date = originalStart.Date;
        if (originalStart != date.AddMinutes(StartTimeOffset) || DeletedInstanceDates.Contains(date) || date < StartDate)
        {
            return null;
        }

        return PatternDates(date).LastOrDefault() == date
            ? new AppointmentOccurrence(originalStart, zone.ToUtc(originalStart), zone.ToUtc(date.AddMinutes(EndTimeOffset)), null)
            : null;
    }

    /// <summary>
    /// The dates (midnight, local) the pattern falls on from its first date through the earlier of its last and
    /// <paramref name="through"/>, deleted ones included ([MS-OXOCAL] 2.2.1.44.1.2: a day, week or month is valid when it
    /// lies a whole number of periods from FirstDateTime; the pattern's days are taken within each valid one).
    /// </summary>
    public IEnumerable<DateTime> PatternDates(DateTime through)
    {
        if (!CanExpand)
        {
            yield break;
        }

        var last = EndDate is { } end && end < through ? end : through;
        switch (PatternType)
        {
            case RecurrencePatternType.Day:
                {
                    // Period is in minutes, whole days
                    var step = Math.Max(1440L, Period);
                    var offset = Mod(MinutesOf(StartDate) - FirstDateTime, step);
                    for (var date = offset == 0 ? StartDate : StartDate.AddMinutes(step - offset); date <= last; date = date.AddMinutes(step))
                    {
                        yield return date;
                    }

                    break;
                }

            case RecurrencePatternType.Week:
                {
                    var step = 10080L * Period;
                    var week = StartDate.AddDays(-Mod((int)StartDate.DayOfWeek - (int)FirstDayOfWeek, 7));
                    var offset = Mod(MinutesOf(week) - FirstDateTime, step);
                    for (week = offset == 0 ? week : week.AddMinutes(step - offset); week <= last; week = week.AddMinutes(step))
                    {
                        for (var i = 0; i < 7; i++)
                        {
                            var date = week.AddDays(i);
                            if (date >= StartDate && date <= last && (DaysOfWeek & (1 << (int)date.DayOfWeek)) != 0)
                            {
                                yield return date;
                            }
                        }
                    }

                    break;
                }

            default:
                {
                    // months counted from January 1601; a valid one lies a whole number of periods from FirstDateTime's
                    var first = Epoch.AddMinutes(FirstDateTime);
                    var firstMonth = MonthIndex(first);
                    var month = MonthIndex(StartDate);
                    var offset = Mod(month - firstMonth, Period);
                    for (month = offset == 0 ? month : month + (int)(Period - offset); month / 12 + 1601 <= Math.Min(last.Year, 9998); month += (int)Period)
                    {
                        var year = month / 12 + 1601;
                        var monthOfYear = month % 12 + 1;
                        var days = DateTime.DaysInMonth(year, monthOfYear);
                        DateTime? date = PatternType switch
                        {
                            RecurrencePatternType.Month => new DateTime(year, monthOfYear, Math.Clamp(DayOfMonth, 1, days)),
                            RecurrencePatternType.MonthEnd => new DateTime(year, monthOfYear, days),
                            _ => Nth(year, monthOfYear, DaysOfWeek, Instance),
                        };
                        if (date is { } d && d >= StartDate && d <= last)
                        {
                            yield return d;
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>The first to fourth (or with 5 the last) day of a month whose weekday is in the mask.</summary>
    private static DateTime? Nth(int year, int month, int mask, int instance)
    {
        var matches = new List<DateTime>(5);
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            var date = new DateTime(year, month, day);
            if ((mask & (1 << (int)date.DayOfWeek)) != 0)
            {
                matches.Add(date);
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        return instance >= 5 ? matches[^1] : instance >= 1 && instance <= matches.Count ? matches[instance - 1] : null;
    }

    private static DateTime Minutes(uint minutes) => Epoch.AddMinutes(minutes);

    private static long MinutesOf(DateTime date) => (long)(date - Epoch).TotalMinutes;

    private static int MonthIndex(DateTime date) => (date.Year - 1601) * 12 + date.Month - 1;

    private static long Mod(long value, long by) => (value % by + by) % by;

    /// <summary>Little-endian reads that say so when the structure ends early.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public readonly int Remaining => _data.Length - _at;

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || _at + count > _data.Length)
            {
                throw new PstException($"the recurrence pattern ends at byte {_data.Length}, {count} more were to be read at {_at}");
            }

            var span = _data.Slice(_at, count);
            _at += count;
            return span;
        }

        public void Skip(int count) => Bytes(count);

        public List<DateTime> Dates(int count)
        {
            if (count is < 0 or > 100_000)
            {
                throw new PstException($"a recurrence pattern listing {count} dates");
            }

            var dates = new List<DateTime>(count);
            for (var i = 0; i < count; i++)
            {
                dates.Add(Minutes(U32()));
            }

            return dates;
        }
    }
}
