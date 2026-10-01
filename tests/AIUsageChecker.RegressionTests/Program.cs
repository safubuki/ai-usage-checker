using AIUsageChecker.RegressionTests;

var tests = UsageFetcherRecoveryTests.GetCases().Concat(GrokQuotaClientTests.GetCases())
    .Concat(ClaudeQuotaClientTests.GetCases())
    .Concat(CardDragDropTests.GetCases())
    .Append(new TestCase("詳細画面とカードドラッグの操作・配置", () =>
    {
        DetailViewChecks.Run();
        return Task.CompletedTask;
    })).ToArray();
int failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine($"成功: {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"失敗: {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"回帰検証: {tests.Length - failures}/{tests.Length} 件成功");
return failures == 0 ? 0 : 1;
