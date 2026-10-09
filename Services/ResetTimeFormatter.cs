using System;
using System.Globalization;

namespace AIUsageChecker.Services;

internal static class ResetTimeFormatter
{
    internal static string Format(DateTime localResetTime, DateTime localNow,
        bool alwaysShowDate = false, string suffix = "リセット")
    {
        var format = alwaysShowDate || localResetTime.Date != localNow.Date
            ? "MM/dd HH:mm"
            : "HH:mm";
        return $"{localResetTime.ToString(format, CultureInfo.InvariantCulture)} {suffix}";
    }

    internal static string Format(string? resetTime, DateTime localNow, string suffix = "リセット")
    {
        if (string.IsNullOrWhiteSpace(resetTime)) return "常時更新";

        // 取得したISO日時をローカル日時へ変換し、表示形式だけを整える。
        if (DateTimeOffset.TryParseExact(resetTime.Trim(),
            ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var resetTimeWithOffset))
            return Format(resetTimeWithOffset.LocalDateTime, localNow, suffix: suffix);

        return resetTime;
    }
}
