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
                    UsageCheckCommand = "/status",
                    StatusMessage = "利用状況を確認中..."
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "5時間制限",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
                    ResetTimeText = "取得中..."
                },
                SecondaryLimit = new UsageLimitInfo
                {
                    Title = "週次制限",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
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
                    StatusMessage = "利用状況を確認中..."
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "契約状況",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
                    ResetTimeText = "取得中..."
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
                    UsageCheckCommand = "/usage (agy status)",
                    StatusMessage = "利用状況を確認中..."
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "5時間制限",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
                    ResetTimeText = "取得中..."
                },
                SecondaryLimit = new UsageLimitInfo
                {
                    Title = "週次制限",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
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
                    UsageCheckCommand = "/usage",
                    StatusMessage = "利用状況を確認中..."
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "週次制限",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
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
                    StatusMessage = "利用状況を確認中..."
                },
                PrimaryLimit = new UsageLimitInfo
                {
                    Title = "プレミアム要求",
                    RemainingPercent = 0.0,
                    CustomDisplayPercentText = "--",
                    LimitDescription = "取得中...",
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

        foreach (var item in ordered)
        {
            item.UpdateStatusAndCheckRecovery();
        }

        return new ObservableCollection<AiUsageItem>(ordered);
    }
}
