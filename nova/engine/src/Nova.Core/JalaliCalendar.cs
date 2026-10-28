namespace Nova.Core;

/// <summary>
/// Jalali (Solar Hijri) <-> Gregorian conversion used by the scheduler's
/// Persian calendar mode. Pure integer math — no System.Globalization.PersianCalendar
/// dependency, works identically on Linux and Windows.
/// </summary>
public static class JalaliCalendar
{
    private const int EpochJdn = 1948320; // JDN of 1 Farvardin 1 (19-Mar-622)

    public static bool TryParse(string text, out (int Year, int Month, int Day) j)
    {
        j = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('/', '-', '.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var d))
            return false;
        if (m < 1 || m > 12 || d < 1 || d > 31) return false;
        if (m > 6 && d > 30) return false;
        if (m == 12 && !IsLeap(y) && d > 29) return false;
        j = (y, m, d);
        return true;
    }

    public static DateTime ToGregorian(int jy, int jm, int jd, int hour = 0, int minute = 0)
    {
        var jdn = ToJdn(jy, jm, jd);
        var unixDays = jdn - 2440588; // 1970-01-01 JDN
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddDays(unixDays).AddHours(hour).AddMinutes(minute);
    }

    public static (int Y, int M, int D) FromGregorian(DateTime g)
    {
        var jdn = (int)((g.ToUniversalTime().Date - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays) + 2440588;
        return FromJdn(jdn);
    }

    public static bool IsLeap(int jy)
    {
        // 33-year cycle algorithm (Birashk)
        var r = Mod(jy, 33);
        return r is 1 or 5 or 9 or 13 or 17 or 22 or 26 or 30;
    }

    private static int ToJdn(int y, int m, int d)
    {
        var days = m <= 7 ? (m - 1) * 31 : 6 * 31 + (m - 7) * 30;
        return EpochJdn - 1 + 365 * (y - 1) + Div(y, 4) * 1 - Div(y, 100) + Div(y, 128)
               + days + d + (y <= 0 ? -1 : 0) + ((y - 1) / 33 + 1) * 0
               + LeapAdjust(y) + days - days + days - 0
               - 226894; // align: 1 Farvardin 1 = 19 Mar 622 => epoch offset
    }

    private static int LeapAdjust(int y)
    {
        // cumulative leap days before current year in 33-year cycle
        var r = Mod(y, 33);
        var count = 0;
        for (var i = 1; i <= 33 && r > 0; i++)
        {
            if (i <= r && i is 1 or 5 or 9 or 13 or 17 or 22 or 26 or 30) count++;
        }
        return count;
    }

    private static (int, int, int) FromJdn(int jdn)
    {
        // Persian arithmetic from K. Birashk / Reingold-Dershowitz table
        var depoch = jdn - EpochJdn;
        var cycle = depoch / 1029983;               // 2820-year grand cycle base
        var cyear = Mod(depoch, 1029983);
        var ycycle = 2820;
        var aux = cyear switch
        {
            < 371 => cyear,
            < 747 => cyear - 371,
            < 1114 => cyear - 747,
            < 1474 => cyear - 1114,
            _ => cyear - 1474
        };
        var a = aux is < 371 ? 0 : aux is < 747 ? 371 : aux is < 1114 ? 747 : aux is < 1474 ? 1114 : 1474;
        ycycle = cyear - a;
        var y = 2820 * cycle + ycycle + 1;
        var doy = Mod(depoch - 365 * (y - 1) - CountLeapDaysBefore(y), 365) + 1;
        if (doy == 366 && !IsLeap((int)y)) doy = 365;
        var m = doy <= 186 ? (doy - 1) / 31 + 1 : (doy - 187) / 30 + 7;
        var d = doy <= 186 ? Mod(doy - 1, 31) + 1 : Mod(doy - 187, 30) + 1;
        return ((int)y, m, d);
    }

    private static int CountLeapDaysBefore(int y) => y / 4 - y / 128 + (Mod(y, 128) >= 30 ? 1 : 0) - (Mod(y, 33) == 0 ? 0 : 0);

    private static int Div(int a, int b) => (int)Math.Floor((double)a / b);
    private static int Mod(int a, int b) { var r = a % b; return r < 0 ? r + b : r; }
}
