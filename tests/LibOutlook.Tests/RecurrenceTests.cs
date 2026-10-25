using System.Globalization;

namespace LibOutlook.Tests;

/// <summary>
/// Recurring appointments as Outlook stores them (fixtures/recurrence.pst, made by Outlook in the lab's Pacific zone:
/// daily, weekly, every-n-weeks, monthly on the 31st, last Friday, first weekday every three months, yearly on a date and
/// on the fourth Thursday, an all-day series, a deleted and two moved occurrences, the change to standard time on
/// 1 November 2026), against Outlook's own expansion of that calendar (fixtures/recurrence.occurrences.txt: every
/// occurrence starting in 2026 or 2027, local time).
/// </summary>
[TestClass]
public class RecurrenceTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);   // 1 January 2026, 00:00 PST
    private static readonly DateTimeOffset Until = new(2028, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void The_occurrences_are_the_ones_Outlook_lists()
    {
        using var pst = PstFile.Open(Fixtures.Path("recurrence.pst"));
        var calendar = pst.TopFolder.FindFolder("Calendar")!;
        ushort Id(uint lid) => pst.NamedProperties.Find(PstNamedProperties.AppointmentPropertySet, lid)!.PropertyId;
        var start = Id(0x820D);
        var end = Id(0x820E);
        var location = Id(0x8208);
        var allDay = Id(0x8215);

        var ours = new List<string>();
        var series = 0;
        foreach (var item in calendar.Messages)
        {
            var place = item.Properties.GetString(location) ?? "";
            var whole = item.Properties.GetBoolean(allDay) == true ? "allday" : "timed";
            if (item.Recurrence is { } recurrence)
            {
                series++;
                Assert.IsTrue(recurrence.CanExpand, $"'{item.Subject}' is a Gregorian pattern");
                Assert.AreEqual("Pacific Standard Time", recurrence.TimeZone?.Name, $"'{item.Subject}' keeps its zone");
                foreach (var occurrence in recurrence.Occurrences(Until))
                {
                    if (occurrence.Start >= From)
                    {
                        ours.Add(Line(occurrence.Start, occurrence.End, whole, occurrence.Exception?.Subject ?? item.Subject, occurrence.Exception?.Location ?? place));
                    }
                }
            }
            else
            {
                ours.Add(Line(Utc(item.Properties.GetDateTime(start)!.Value), Utc(item.Properties.GetDateTime(end)!.Value), whole, item.Subject, place));
            }
        }

        Assert.AreEqual(9, series, "nine series and one single appointment");
        ours.Sort(StringComparer.Ordinal);
        var outlook = File.ReadAllLines(Fixtures.Path("recurrence.occurrences.txt")).Where(l => l.Length > 0).Order(StringComparer.Ordinal).ToList();
        var missing = outlook.Except(ours).ToList();
        var extra = ours.Except(outlook).ToList();
        TestContext.WriteLine($"{ours.Count} occurrences listed, Outlook {outlook.Count}");
        Assert.IsTrue(missing.Count == 0 && extra.Count == 0, $"Outlook lists, we do not:\n{string.Join("\n", missing)}\nwe list, Outlook does not:\n{string.Join("\n", extra)}");
        CollectionAssert.AreEqual(outlook, ours);
    }

    [TestMethod]
    public void A_series_reads_as_Outlook_wrote_it()
    {
        using var pst = PstFile.Open(Fixtures.Path("recurrence.pst"));
        var calendar = pst.TopFolder.FindFolder("Calendar")!;
        var weekly = calendar.Messages.Single(m => m.Subject == "Weekly sync").Recurrence!;
        Assert.AreEqual(RecurrenceFrequency.Weekly, weekly.Frequency);
        Assert.AreEqual(RecurrencePatternType.Week, weekly.PatternType);
        Assert.AreEqual(1u, weekly.Period);
        Assert.AreEqual(0x04 | 0x10, weekly.DaysOfWeek, "Tuesday and Thursday");
        Assert.AreEqual(RecurrenceEnd.AfterCount, weekly.EndType);
        Assert.AreEqual(20, weekly.OccurrenceCount);
        Assert.AreEqual(new DateTime(2026, 9, 1), weekly.StartDate);
        Assert.AreEqual(new DateTime(2026, 11, 5), weekly.EndDate);
        Assert.AreEqual(600, weekly.StartTimeOffset, "10:00");
        Assert.AreEqual(630, weekly.EndTimeOffset);
        CollectionAssert.AreEqual(new[] { new DateTime(2026, 9, 17), new DateTime(2026, 10, 13) }, weekly.DeletedInstanceDates.ToArray(), "the deleted one and the moved one's original date");
        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 13) }, weekly.ModifiedInstanceDates.ToArray());
        var moved = weekly.Exceptions.Single();
        Assert.AreEqual((new DateTime(2026, 10, 13, 10, 0, 0), new DateTime(2026, 10, 13, 11, 0, 0), new DateTime(2026, 10, 13, 11, 45, 0)), (moved.OriginalStart, moved.Start, moved.End));
        Assert.AreEqual(("Weekly sync (moved)", "Room 9"), (moved.Subject, moved.Location));

        var endless = calendar.Messages.Single(m => m.Subject == "Last Friday").Recurrence!;
        Assert.AreEqual(RecurrencePatternType.MonthNth, endless.PatternType);
        Assert.AreEqual((0x20, 5), (endless.DaysOfWeek, endless.Instance), "the last Friday");
        Assert.AreEqual(RecurrenceEnd.Never, endless.EndType);
        Assert.IsNull(endless.EndDate);
        Assert.AreEqual(6, endless.Occurrences(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)).Count, "June to November 2026");
        Assert.AreEqual(3, endless.Occurrences(Until, limit: 3).Count, "at most as many as asked for");

        // the one-off is no series
        Assert.IsNull(calendar.Messages.Single(m => m.Subject == "One-off").Recurrence);
    }

    [TestMethod]
    public void The_zone_converts_on_both_sides_of_a_change()
    {
        using var pst = PstFile.Open(Fixtures.Path("recurrence.pst"));
        var zone = pst.TopFolder.FindFolder("Calendar")!.Messages.Single(m => m.Subject == "Weekly sync").Recurrence!.TimeZone!;
        Assert.AreEqual(new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero), zone.ToUtc(new DateTime(2026, 10, 30, 10, 0, 0)), "daylight time, UTC-7");
        Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero), zone.ToUtc(new DateTime(2026, 11, 2, 10, 0, 0)), "standard time, UTC-8");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 9, 17, 0, 0, TimeSpan.Zero), zone.ToUtc(new DateTime(2026, 3, 9, 10, 0, 0)), "daylight again from 8 March 2026");
        Assert.AreEqual(new DateTime(2026, 11, 2, 10, 0, 0), zone.ToLocal(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero)));
        Assert.AreEqual(new DateTime(2026, 10, 30, 10, 0, 0), zone.ToLocal(new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero)));
    }

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));

    private static string Line(DateTimeOffset start, DateTimeOffset end, string whole, string subject, string location)
        => string.Join('|', start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), whole, subject, location);
}
