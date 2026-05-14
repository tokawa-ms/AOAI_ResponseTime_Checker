using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AOAI.ResponseTime.Checker;

/// <summary>
/// 計測結果のサマリー表示や CSV / JSON への書き出しを行うヘルパー。
/// </summary>
public static class BenchmarkReporter
{
    // 結果ファイルは人間が直接確認することも多いため、JSON は整形して出力する。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// 本測定の成功結果だけを対象に、target 別および task 別の平均応答時間を表示する。
    /// </summary>
    public static void PrintSummary(IEnumerable<MeasurementResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        MeasurementResult[] all = results.ToArray();
        // warmup は初回接続やモデル側準備の影響を含むため、性能比較の集計から除外する。
        MeasurementResult[] measured = all
            .Where(result => !result.IsWarmup && result.Succeeded)
            .ToArray();

        Console.WriteLine("Summary");
        Console.WriteLine("Target                         Avg(ms)   Min(ms)   Max(ms)   Runs");
        Console.WriteLine(new string('-', 68));

        foreach (var group in measured.GroupBy(result => result.TargetName).OrderBy(group => group.Key))
        {
            Console.WriteLine($"{group.Key,-30} {group.Average(r => r.ElapsedMilliseconds),8:N0} {group.Min(r => r.ElapsedMilliseconds),8:N0} {group.Max(r => r.ElapsedMilliseconds),8:N0} {group.Count(),6}");
        }

        Console.WriteLine();
        Console.WriteLine("Per task average");
        Console.WriteLine("Target                         Task       Avg(ms)   Runs");
        Console.WriteLine(new string('-', 62));

        foreach (var group in measured
                     .GroupBy(result => new { result.TargetName, result.TaskId })
                     .OrderBy(group => group.Key.TargetName)
                     .ThenBy(group => group.Key.TaskId))
        {
            Console.WriteLine($"{group.Key.TargetName,-30} {group.Key.TaskId,-8} {group.Average(r => r.ElapsedMilliseconds),8:N0} {group.Count(),6}");
        }

        // 失敗回数だけは最後に明示し、CSV / JSON 側で詳細を追えるようにする。
        MeasurementResult[] failed = all.Where(result => !result.IsWarmup && !result.Succeeded).ToArray();
        if (failed.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Failed measurement runs: {failed.Length}");
        }
    }

    /// <summary>
    /// 計測結果を CSV 形式へ変換する。
    /// </summary>
    public static string ToCsv(IEnumerable<MeasurementResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        StringBuilder builder = new();
        builder.AppendLine("targetName,deployment,reasoningEffort,taskId,taskName,run,isWarmup,succeeded,elapsedMilliseconds,responseCharacterCount,finishReason,error");

        foreach (MeasurementResult result in results)
        {
            builder.AppendLine(string.Join(',',
                Csv(result.TargetName),
                Csv(result.Deployment),
                Csv(result.ReasoningEffort),
                Csv(result.TaskId),
                Csv(result.TaskName),
                result.Run.ToString(CultureInfo.InvariantCulture),
                result.IsWarmup.ToString(CultureInfo.InvariantCulture),
                result.Succeeded.ToString(CultureInfo.InvariantCulture),
                result.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                result.ResponseCharacterCount.ToString(CultureInfo.InvariantCulture),
                Csv(result.FinishReason),
                Csv(result.Error)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 計測結果を JSON 形式へ変換する。
    /// </summary>
    public static string ToJson(IEnumerable<MeasurementResult> results) =>
        JsonSerializer.Serialize(results, JsonOptions);

    /// <summary>
    /// 現在時刻を含むファイル名で CSV と JSON の両方を書き出す。
    /// </summary>
    public static async Task<(string CsvPath, string JsonPath)> WriteResultsAsync(
        IEnumerable<MeasurementResult> results,
        string resultsDirectory,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsDirectory);

        Directory.CreateDirectory(resultsDirectory);

        MeasurementResult[] snapshot = results.ToArray();
        string stamp = (timestamp ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string csvPath = Path.Combine(resultsDirectory, $"response-times-{stamp}.csv");
        string jsonPath = Path.Combine(resultsDirectory, $"response-times-{stamp}.json");

        await File.WriteAllTextAsync(csvPath, ToCsv(snapshot), Encoding.UTF8).ConfigureAwait(false);
        await File.WriteAllTextAsync(jsonPath, ToJson(snapshot), Encoding.UTF8).ConfigureAwait(false);

        return (csvPath, jsonPath);
    }

    /// <summary>
    /// CSV の 1 フィールドとして安全に出力できるよう、値をダブルクォートで囲みエスケープする。
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
