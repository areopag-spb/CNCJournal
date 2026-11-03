using AiService.Services;

namespace AiService.Tests;

public class LogRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aisvc-retention-" + Guid.NewGuid().ToString("N"));

    public LogRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative, DateTime lastWrite)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    [Fact]
    public void DeleteOlderThan_RemovesOnlyOldFilesByPattern()
    {
        var now = new DateTime(2026, 11, 3);
        var oldLog = Touch("logs/2026-01-01.log", now.AddDays(-200));
        var freshLog = Touch("logs/2026-11-01.log", now.AddDays(-2));
        var oldOther = Touch("logs/readme.txt", now.AddDays(-400));

        var removed = LogRetentionService.DeleteOlderThan(Path.Combine(_root, "logs"), "*.log", now.AddDays(-180), recursive: false);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(freshLog));
        Assert.True(File.Exists(oldOther));
    }

    [Fact]
    public void DeleteOlderThan_Recursive_And_EmptyMonthDirsRemoved()
    {
        var now = new DateTime(2026, 11, 3);
        Touch("request_logs/2026-01/a.json", now.AddDays(-300));
        Touch("request_logs/2026-01/b.json", now.AddDays(-299));
        var kept = Touch("request_logs/2026-11/c.json", now.AddDays(-1));
        var dir = Path.Combine(_root, "request_logs");

        var removed = LogRetentionService.DeleteOlderThan(dir, "*.json", now.AddDays(-180), recursive: true);
        LogRetentionService.RemoveEmptySubdirectories(dir);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(Path.Combine(dir, "2026-01")));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void MissingDirectory_IsNoOp()
    {
        Assert.Equal(0, LogRetentionService.DeleteOlderThan(Path.Combine(_root, "none"), "*.log", DateTime.Now, recursive: true));
        LogRetentionService.RemoveEmptySubdirectories(Path.Combine(_root, "none"));
    }
}
