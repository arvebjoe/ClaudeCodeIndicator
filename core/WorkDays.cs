using System;

namespace ClaudeCodeIndicator;

// Spreads the monthly usage budget evenly over the month's work days, so the
// matrix can show green for "within today's pace" and orange for "ahead of it".
// A work day is Monday–Friday that isn't a Norwegian public holiday. Dec 24 and
// Dec 31 also count as days off, since most offices close then.
public static class WorkDays
{
    // Budget available through the end of `today`: limit × (work days so far,
    // including today) / (work days in the month). The budget resets on the 1st.
    public static decimal AllowanceToDate(decimal limit, DateOnly today)
    {
        int total = 0, soFar = 0;
        int days = DateTime.DaysInMonth(today.Year, today.Month);
        for (int day = 1; day <= days; day++)
        {
            if (!IsWorkDay(new DateOnly(today.Year, today.Month, day))) continue;
            total++;
            if (day <= today.Day) soFar++;
        }
        return total == 0 ? limit : limit * soFar / total;
    }

    public static bool IsWorkDay(DateOnly d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(d);

    private static bool IsHoliday(DateOnly d)
    {
        if ((d.Month, d.Day) is (1, 1) or (5, 1) or (5, 17) or (12, 24) or (12, 25) or (12, 26) or (12, 31))
            return true;
        // Easter-based: Maundy Thursday, Good Friday, Easter Monday, Ascension Day, Whit Monday.
        int offset = d.DayNumber - EasterSunday(d.Year).DayNumber;
        return offset is -3 or -2 or 1 or 39 or 50;
    }

    // Anonymous Gregorian algorithm (Meeus/Jones/Butcher).
    private static DateOnly EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int n = h + l - 7 * m + 114;
        return new DateOnly(y, n / 31, n % 31 + 1);
    }
}
