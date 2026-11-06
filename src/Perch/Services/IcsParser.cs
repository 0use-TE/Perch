using System.Globalization;

namespace Perch.Services;

public sealed record CalEvent(string Title, DateTime Start, DateTime End, bool AllDay, string Location);

// A small iCalendar (.ics) reader: events, time zones, all-day events and the common repeat rules
// (daily, weekly with days, monthly, yearly; interval, count, until, exceptions).
// Not covered: edited single occurrences of a repeating event (RECURRENCE-ID) and the rarer BYSETPOS-style rules.
public static class IcsParser
{
    private sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    public static List<CalEvent> Parse(string text, DateTime from, DateTime to)
    {
        var result = new List<CalEvent>();
        List<Prop>? current = null;
        foreach (var line in Unfold(text))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = []; continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) Expand(current, from, to, result);
                current = null;
                continue;
            }
            if (current == null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon].Split(';');
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in head.Skip(1))
            {
                var eq = part.IndexOf('=');
                if (eq > 0) parameters[part[..eq]] = part[(eq + 1)..].Trim('"');
            }
            current.Add(new Prop(head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]));
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var builder = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            if (raw.StartsWith(' ') || raw.StartsWith('\t')) { builder.Append(raw.AsSpan(1)); continue; }
            if (builder.Length > 0) yield return builder.ToString();
            builder.Clear().Append(raw);
        }
        if (builder.Length > 0) yield return builder.ToString();
    }

    private static string Unescape(string value) => value.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static void Expand(List<Prop> props, DateTime from, DateTime to, List<CalEvent> output)
    {
        Prop? Find(string name) => props.FirstOrDefault(p => p.Name == name);
        if (Find("STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true) return;
        if (Find("RECURRENCE-ID") != null) return;
        if (Find("DTSTART") is not { } startProp || !TryDate(startProp, out var start, out var allDay)) return;

        TimeSpan length;
        if (Find("DTEND") is { } endProp && TryDate(endProp, out var end, out _)) length = end - start;
        else if (Find("DURATION") is { } durationProp && TryDuration(durationProp.Value, out var parsed)) length = parsed;
        else length = allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
        if (length < TimeSpan.Zero) length = TimeSpan.Zero;

        var title = Unescape(Find("SUMMARY")?.Value ?? "");
        if (title.Length == 0) title = "(no title)";
        var location = Unescape(Find("LOCATION")?.Value ?? "");

        var skipped = new HashSet<DateTime>();
        foreach (var exception in props.Where(p => p.Name == "EXDATE"))
            foreach (var value in exception.Value.Split(','))
                if (TryDate(new Prop("EXDATE", exception.Params, value), out var gone, out _)) skipped.Add(gone);

        void Add(DateTime occurrenceStart)
        {
            if (skipped.Contains(occurrenceStart)) return;
            var occurrenceEnd = occurrenceStart + length;
            if (occurrenceEnd < from && occurrenceStart < from) return;
            if (occurrenceStart > to) return;
            output.Add(new CalEvent(title, occurrenceStart, occurrenceEnd, allDay, location));
        }

        if (Find("RRULE") is not { } rule) { Add(start); return; }
        foreach (var occurrence in Occurrences(start, rule.Value, to)) Add(occurrence);
    }

    private static IEnumerable<DateTime> Occurrences(DateTime start, string rule, DateTime limit)
    {
        var parts = rule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var frequency = parts.GetValueOrDefault("FREQ")?.ToUpperInvariant() ?? "";
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        var count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        DateTime? until = parts.TryGetValue("UNTIL", out var u) && TryDate(new Prop("UNTIL", [], u), out var parsedUntil, out _) ? parsedUntil.AddDays(u.Length == 8 ? 1 : 0) : null;
        var emitted = 0;

        bool Continue(DateTime value) => value <= limit && (until == null || value <= until) && emitted < count;

        switch (frequency)
        {
            case "DAILY":
                for (var d = start; Continue(d) && emitted < 5000; d = d.AddDays(interval)) { emitted++; yield return d; }
                break;
            case "WEEKLY":
                var days = (parts.GetValueOrDefault("BYDAY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => code.Trim()[^2..].ToUpperInvariant())
                    .Select(code => Array.IndexOf(["SU", "MO", "TU", "WE", "TH", "FR", "SA"], code)).Where(index => index >= 0).Distinct().Order().ToList();
                if (days.Count == 0) days.Add((int)start.DayOfWeek);
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)); // Monday of the first week
                for (var week = 0; week < 600 && weekStart.AddDays(week * 7 * interval) <= limit; week++)
                    foreach (var day in days.OrderBy(d => (d + 6) % 7))
                    {
                        var occurrence = weekStart.AddDays(week * 7 * interval + (day + 6) % 7).Add(start.TimeOfDay);
                        if (occurrence < start) continue;
                        if (!Continue(occurrence)) yield break;
                        emitted++;
                        yield return occurrence;
                    }
                break;
            case "MONTHLY":
                for (var m = 0; m < 600; m++)
                {
                    var occurrence = start.AddMonths(m * interval);
                    if (!Continue(occurrence)) yield break;
                    emitted++;
                    yield return occurrence;
                }
                break;
            case "YEARLY":
                for (var y = 0; y < 100; y++)
                {
                    var occurrence = start.AddYears(y * interval);
                    if (!Continue(occurrence)) yield break;
                    emitted++;
                    yield return occurrence;
                }
                break;
            default:
                yield return start;
                break;
        }
    }

    private static bool TryDate(Prop prop, out DateTime value, out bool allDay)
    {
        value = default; allDay = false;
        var text = prop.Value.Trim();
        if (text.Length == 8 || prop.Params.GetValueOrDefault("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true)
        {
            allDay = true;
            return DateTime.TryParseExact(text[..Math.Min(8, text.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }
        if (text.EndsWith('Z'))
        {
            if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) return false;
            value = utc.ToLocalTime();
            return true;
        }
        if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return false;
        value = wall;
        if (prop.Params.GetValueOrDefault("TZID") is { Length: > 0 } id)
        {
            try { value = TimeZoneInfo.ConvertTime(wall, TimeZoneInfo.FindSystemTimeZoneById(id), TimeZoneInfo.Local); }
            catch (Exception) { /* unknown zone name: treat as local time */ }
        }
        return true;
    }

    // PT1H30M, P1D, P1W ...
    private static bool TryDuration(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        text = text.Trim();
        if (!text.StartsWith('P')) return false;
        var number = "";
        var inTime = false;
        foreach (var ch in text[1..])
        {
            if (ch == 'T') { inTime = true; continue; }
            if (char.IsDigit(ch)) { number += ch; continue; }
            if (!int.TryParse(number, out var n)) return false;
            number = "";
            value += ch switch
            {
                'W' => TimeSpan.FromDays(7 * n),
                'D' => TimeSpan.FromDays(n),
                'H' when inTime => TimeSpan.FromHours(n),
                'M' when inTime => TimeSpan.FromMinutes(n),
                'S' when inTime => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
        }
        return true;
    }
}
