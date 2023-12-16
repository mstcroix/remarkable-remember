using System;
using System.Globalization;

namespace ReMarkableRemember.Helper;

public static class DateTimeExtensions
{
    public static DateTime ToDateTime(this String value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }

    public static String FromDateTime(this DateTime value)
    {
        return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }
}
