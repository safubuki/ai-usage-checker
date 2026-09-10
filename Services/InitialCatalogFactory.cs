using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AIUsageChecker.Models;

namespace AIUsageChecker.Services;

/// <summary>
/// アプリケーション起動時の初期AIサービス一覧を生成・並べ替えするファクトリ
/// </summary>
public static class InitialCatalogFactory
{
    public static ObservableCollection<AiUsageItem> CreateInitialServices(List<string>? displayOrder)
    {
        var services = new List<AiUsageItem>
        {
            // 1. GPT (OpenAI / Codex)
            new()
            {
                ServiceType = AiServiceType.GPT,
                DisplayName = "GPT",
                SubTitle = "OpenAI / Codex",
                IconGlyph = "⚡",
                CliInfo = new CliInfo
                {
                    Name = "Codex",
                    CommandName = "codex",
                    PackageName = "@openai/codex",
                    UsageCheckCommand = "/status"
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "5時間制限",
                    RemainingPercent = 0.0,
                    LimitDescription = "5h limit",
                    ResetTimeText = "取得中..."
                },
                SecondaryLimit = new UsageLimitInfo
                {
                    Title = "週次制限",
                    RemainingPercent = 52.0,
                    LimitDescription = "Weekly limit",
                    ResetTimeText = "取得中..."
                }
            },

            // 2. Claude (Anthropic)
            new()
            {
                ServiceType = AiServiceType.Claude,
                DisplayName = "Claude",
                SubTitle = "Anthropic",
                IconGlyph = "✦",
                CliInfo = new CliInfo
                {
                    Name = "Claude",
                    CommandName = "claude",
                    PackageName = "@anthropic-ai/claude-code",
                    UsageCheckCommand = "/usage (/cost, /stats)",
                    IsSubscribed = false,
                    IsInstalled = false,
                    StatusMessage = "未契約 (プラン未加入)"
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "契約状況",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "Anthropic Claude 未契約",
                    ResetTimeText = "未契約"
                },
                SecondaryLimit = null
            },

            // 3. Gemini (Google DeepMind)
            new()
            {
                ServiceType = AiServiceType.Gemini,
                DisplayName = "Gemini",
                SubTitle = "Google DeepMind",
                IconGlyph = "✧",
                CliInfo = new CliInfo
                {
                    Name = "Gemini",
                    CommandName = "gemini",
                    PackageName = "@google/gemini-cli",
                    UsageCheckCommand = "/usage (agy status)"
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "5時間制限",
                    RemainingPercent = 81.0,
                    LimitDescription = "Five Hour Limit Remaining",
                    ResetTimeText = $"{DateTime.Now.AddHours(4).AddMinutes(5):HH:mm} リセット"
                },
                SecondaryLimit = new UsageLimitInfo
                {
                    Title = "週次制限",
                    RemainingPercent = 74.0,
                    LimitDescription = "Weekly Limit Remaining",
                    ResetTimeText = "取得中..."
                }
            },

            // 4. Grok (xAI / SuperGrok)
            new()
            {
                ServiceType = AiServiceType.Grok,
                DisplayName = "Grok",
                SubTitle = "xAI / SuperGrok",
                IconGlyph = "𝕏",
                CliInfo = new CliInfo
                {
                    Name = "Grok",
                    CommandName = "grok",
                    PackageName = "grok",
                    UsageCheckCommand = "/usage"
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "週次制限",
                    RemainingPercent = 100.0,
                    LimitDescription = "Weekly limit (SuperGrok)",
                    ResetTimeText = "取得中..."
                },
                SecondaryLimit = null
            },

            // 5. Copilot (GitHub / Copilot Pro)
            new()
            {
                ServiceType = AiServiceType.Copilot,
                DisplayName = "Copilot",
                SubTitle = "GitHub / Copilot Pro",
                IconGlyph = "🐙",
                CliInfo = new CliInfo
                {
                    Name = "Copilot",
                    CommandName = "copilot",
                    PackageName = "@github/copilot",
                    UsageCheckCommand = "/usage (/limits)",
                    IsSubscribed = true,
                    StatusMessage = "プラン: Copilot Pro (稼働中)"
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "プレミアム要求",
                    RemainingPercent = 98.0,
                    LimitDescription = "2% 使用済み",
                    ResetTimeText = "取得中..."
                },
                SecondaryLimit = null
            }
        };

        // 設定で保存された表示順序に従って並べ替え
        var orderList = displayOrder ?? new List<string>();
        var ordered = new List<AiUsageItem>();

        foreach (var name in orderList)
        {
            var match = services.FirstOrDefault(x => x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null && !ordered.Contains(match))
            {
                ordered.Add(match);
            }
        }

        foreach (var item in services)
        {
            if (!ordered.Contains(item))
            {
                ordered.Add(item);
            }
        }

        return new ObservableCollection<AiUsageItem>(ordered);
    }
}
