using System;

namespace AIUsageChecker.Models;

/// <summary>
/// Codex / GPT等の利用枠リセット権（リセットチケット）情報
/// </summary>
public class ResetCreditInfo
{
    public string Id { get; set; } = "";
    public string ResetType { get; set; } = "";
    public string Status { get; set; } = "available";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime? GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// 表示用の有効期限テキスト (例: "10/04 10:46")
    /// </summary>
    public string FormattedExpiresAt => ExpiresAt.HasValue
        ? $"{ExpiresAt.Value.ToLocalTime():MM/dd HH:mm}"
        : "--";

    /// <summary>
    /// 残り時間テキスト (例: "残り 9日", "残り 5時間")
    /// </summary>
    public string RemainingTimeText
    {
        get
        {
            if (!ExpiresAt.HasValue) return "";
            var localExpires = ExpiresAt.Value.ToLocalTime();
            var diff = localExpires - DateTime.Now;
            if (diff.TotalDays >= 1)
                return $"残り {(int)diff.TotalDays}日";
            if (diff.TotalHours >= 1)
                return $"残り {(int)diff.TotalHours}時間";
            if (diff.TotalMinutes > 0)
                return $"残り {(int)diff.TotalMinutes}分";
            return "期限切れ";
        }
    }

    /// <summary>
    /// 有効期限が間近（3日以内）かどうかの判定（黄色アクセント用）
    /// </summary>
    public bool IsUrgent
    {
        get
        {
            if (!ExpiresAt.HasValue) return false;
            var diff = ExpiresAt.Value.ToLocalTime() - DateTime.Now;
            return diff.TotalDays <= 3 && diff.TotalMinutes > 0;
        }
    }
}
