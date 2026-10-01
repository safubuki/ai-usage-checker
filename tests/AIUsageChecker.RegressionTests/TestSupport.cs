using System.IO;

namespace AIUsageChecker.RegressionTests;

internal sealed record TestCase(string Name, Func<Task> Run);

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} (期待: {expected}, 実際: {actual})");
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIUsageChecker.RegressionTests");
    public string Path { get; }

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string FilePath(string filename) => System.IO.Path.Combine(Path, filename);

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var allowedPrefix = System.IO.Path.GetFullPath(_parent) + System.IO.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("テスト用一時ディレクトリの範囲外です");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
