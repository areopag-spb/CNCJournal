using System.Text.RegularExpressions;
using remeLog.Infrastructure;

namespace remeLog.Core.Tests;

/// <summary>
/// P3.4: живой вердикт ИИ неизменяем. Без БД проверяем сам SQL записи анализа:
/// единственный UPDATE ai_day_reviews охраняется «AiAnalyzedAt IS NULL», а при уже
/// существующем вердикте оценка идёт только в историю ai_day_reeval.
/// </summary>
public class SaveAiAnalysisGuardTests
{
    private static readonly string Sql = Database.SaveAiAnalysisSql;

    [Fact]
    public void ExactlyOneUpdate_AndItIsGuardedByNoLiveVerdict()
    {
        var updates = Regex.Matches(Sql, @"UPDATE\s+ai_day_reviews", RegexOptions.IgnoreCase);
        Assert.Single(updates);

        // Охрана стоит в WHERE этого UPDATE.
        var tail = Sql[updates[0].Index..];
        var where = Regex.Match(tail, @"WHERE[^;]*;", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(where.Success);
        Assert.Contains("AiAnalyzedAt IS NULL", where.Value);
    }

    [Fact]
    public void ExistingVerdict_GoesToReEvalHistoryOnly()
    {
        var insert = Sql.IndexOf("INSERT INTO ai_day_reeval", StringComparison.OrdinalIgnoreCase);
        var update = Sql.IndexOf("UPDATE ai_day_reviews", StringComparison.OrdinalIgnoreCase);
        var guard = Sql.IndexOf("AiAnalyzedAt IS NOT NULL", StringComparison.OrdinalIgnoreCase);

        Assert.True(guard >= 0 && insert > guard, "INSERT в историю должен идти под проверкой «вердикт уже есть»");
        Assert.True(update > insert, "UPDATE живых полей — только в ветке «вердикта ещё нет»");
    }

    [Fact]
    public void ReEvalHistory_DoesNotTouchLiveColumns()
    {
        var insert = Sql[Sql.IndexOf("INSERT INTO ai_day_reeval", StringComparison.OrdinalIgnoreCase)..];
        insert = insert[..insert.IndexOf("ELSE", StringComparison.OrdinalIgnoreCase)];

        Assert.DoesNotContain("ai_day_reviews", insert, StringComparison.OrdinalIgnoreCase);
    }
}
