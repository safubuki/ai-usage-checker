using System.Collections.Generic;

namespace AIUsageChecker.Models;

public class AppSettings
{
    public List<string> DisplayOrder { get; set; } = new()
    {
        "GPT",
        "Claude",
        "Gemini",
        "Grok",
        "Copilot"
    };

    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double DockedTop { get; set; } = -1;
    public bool IsAlwaysOnTop { get; set; } = true;
    public int AutoRefreshMinutes { get; set; } = 5;
    public bool IsDocked { get; set; } = false;
}
