// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel.Primitives;

#pragma warning disable OPENAI001

Console.OutputEncoding = Encoding.UTF8;

JsonSerializerOptions jsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

AppSettings settings = configuration.Get<AppSettings>() ?? new AppSettings();
List<ModelTarget> targets = ResolveTargets(settings.AzureOpenAI);
List<BenchmarkTask> tasks = LoadTasks(settings.Benchmark.RequestsFile);

if (targets.Count == 0)
{
    throw new InvalidOperationException("AzureOpenAI:Targets または AzureOpenAI:Deployments を 1 つ以上設定してください。");
}

if (tasks.Count == 0)
{
    throw new InvalidOperationException($"計測タスクが見つかりません: {settings.Benchmark.RequestsFile}");
}

Directory.CreateDirectory(settings.Benchmark.ResultsDirectory);

Console.WriteLine("Azure OpenAI response time checker");
Console.WriteLine($"Targets: {targets.Count}, Tasks: {tasks.Count}, WarmupRuns: {settings.Benchmark.WarmupRuns}, MeasurementRunsPerRequest: {settings.Benchmark.MeasurementRunsPerRequest}");
Console.WriteLine();

DefaultAzureCredential credential = CreateCredential(settings.AzureOpenAI.TenantId);
BearerTokenPolicy tokenPolicy = new(credential, settings.AzureOpenAI.TokenScope);

List<MeasurementResult> results = [];

foreach (ModelTarget target in targets)
{
    Uri endpoint = NormalizeAzureOpenAIEndpoint(target.Endpoint ?? settings.AzureOpenAI.Endpoint);
    ChatClient client = new(
        model: target.Deployment,
        authenticationPolicy: tokenPolicy,
        options: new OpenAIClientOptions { Endpoint = endpoint });

    Console.WriteLine($"[{target.Name}] deployment={target.Deployment}, endpoint={endpoint}");

    foreach (BenchmarkTask task in tasks)
    {
        for (int run = 1; run <= settings.Benchmark.WarmupRuns; run++)
        {
            MeasurementResult result = await MeasureAsync(client, target, task, run, isWarmup: true, settings.Benchmark);
            results.Add(result);
            PrintResult(result);
            await DelayIfNeeded(settings.Benchmark.DelayBetweenRequestsMs);
        }

        for (int run = 1; run <= settings.Benchmark.MeasurementRunsPerRequest; run++)
        {
            MeasurementResult result = await MeasureAsync(client, target, task, run, isWarmup: false, settings.Benchmark);
            results.Add(result);
            PrintResult(result);
            await DelayIfNeeded(settings.Benchmark.DelayBetweenRequestsMs);
        }
    }

    Console.WriteLine();
}

PrintSummary(results);

string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
string csvPath = Path.Combine(settings.Benchmark.ResultsDirectory, $"response-times-{timestamp}.csv");
string jsonPath = Path.Combine(settings.Benchmark.ResultsDirectory, $"response-times-{timestamp}.json");

await File.WriteAllTextAsync(csvPath, ToCsv(results), Encoding.UTF8);
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, jsonOptions), Encoding.UTF8);

Console.WriteLine();
Console.WriteLine($"CSV:  {csvPath}");
Console.WriteLine($"JSON: {jsonPath}");

static async Task<MeasurementResult> MeasureAsync(ChatClient client, ModelTarget target, BenchmarkTask task, int run, bool isWarmup, BenchmarkSettings settings)
{
    List<ChatMessage> messages =
    [
        new SystemChatMessage(task.SystemPrompt),
        new UserChatMessage(task.UserPrompt)
    ];

    ChatCompletionOptions options = new();
    int maxOutputTokenCount = task.MaxOutputTokenCount ?? settings.MaxOutputTokenCount;
    if (maxOutputTokenCount > 0)
    {
        options.MaxOutputTokenCount = maxOutputTokenCount;
    }

    Stopwatch stopwatch = Stopwatch.StartNew();

    try
    {
        ChatCompletion completion = await client.CompleteChatAsync(messages, options);
        stopwatch.Stop();

        string responseText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

        return new MeasurementResult(
            TargetName: target.Name,
            Deployment: target.Deployment,
            TaskId: task.Id,
            TaskName: task.Name,
            Run: run,
            IsWarmup: isWarmup,
            Succeeded: true,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ResponseCharacterCount: responseText.Length,
            FinishReason: completion.FinishReason.ToString(),
            Error: null);
    }
    catch (Exception ex)
    {
        stopwatch.Stop();

        return new MeasurementResult(
            TargetName: target.Name,
            Deployment: target.Deployment,
            TaskId: task.Id,
            TaskName: task.Name,
            Run: run,
            IsWarmup: isWarmup,
            Succeeded: false,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ResponseCharacterCount: 0,
            FinishReason: null,
            Error: ex.Message);
    }
}

static List<ModelTarget> ResolveTargets(AzureOpenAISettings settings)
{
    if (settings.Targets.Count > 0)
    {
        return settings.Targets
            .Select((target, index) => target with
            {
                Name = string.IsNullOrWhiteSpace(target.Name) ? target.Deployment : target.Name,
                Endpoint = string.IsNullOrWhiteSpace(target.Endpoint) ? settings.Endpoint : target.Endpoint
            })
            .Where(target => !string.IsNullOrWhiteSpace(target.Deployment))
            .ToList();
    }

    return settings.Deployments
        .Where(deployment => !string.IsNullOrWhiteSpace(deployment))
        .Select(deployment => new ModelTarget
        {
            Name = deployment,
            Deployment = deployment,
            Endpoint = settings.Endpoint
        })
        .ToList();
}

List<BenchmarkTask> LoadTasks(string requestsFile)
{
    string path = Path.IsPathRooted(requestsFile)
        ? requestsFile
        : Path.Combine(AppContext.BaseDirectory, requestsFile);

    if (!File.Exists(path))
    {
        path = Path.Combine(Directory.GetCurrentDirectory(), requestsFile);
    }

    if (!File.Exists(path))
    {
        throw new FileNotFoundException("リクエスト定義ファイルが見つかりません。", requestsFile);
    }

    string json = File.ReadAllText(path, Encoding.UTF8);
    return JsonSerializer.Deserialize<List<BenchmarkTask>>(json, jsonOptions) ?? [];
}

static Uri NormalizeAzureOpenAIEndpoint(string endpoint)
{
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        throw new InvalidOperationException("AzureOpenAI:Endpoint を設定してください。");
    }

    string normalized = endpoint.Trim().TrimEnd('/');

    if (!normalized.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
    {
        normalized += "/openai/v1";
    }

    return new Uri(normalized + "/");
}

static DefaultAzureCredential CreateCredential(string? tenantId)
{
    if (string.IsNullOrWhiteSpace(tenantId))
    {
        return new DefaultAzureCredential();
    }

    return new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId });
}

static async Task DelayIfNeeded(int delayBetweenRequestsMs)
{
    if (delayBetweenRequestsMs > 0)
    {
        await Task.Delay(delayBetweenRequestsMs);
    }
}

static void PrintResult(MeasurementResult result)
{
    string phase = result.IsWarmup ? "warmup" : "measure";
    string status = result.Succeeded ? "OK" : "NG";
    Console.WriteLine($"  {phase,-7} {result.TaskId,-8} run={result.Run} {status} {result.ElapsedMilliseconds,8:N0} ms {result.Error}");
}

static void PrintSummary(IEnumerable<MeasurementResult> results)
{
    MeasurementResult[] measured = results
        .Where(result => !result.IsWarmup && result.Succeeded)
        .ToArray();

    Console.WriteLine("Summary");
    Console.WriteLine("Target                         Avg(ms)   Min(ms)   Max(ms)   Runs");
    Console.WriteLine(new string('-', 68));

    foreach (var group in measured.GroupBy(result => result.TargetName).OrderBy(group => group.Key))
    {
        Console.WriteLine($"{group.Key,-30} {group.Average(result => result.ElapsedMilliseconds),8:N0} {group.Min(result => result.ElapsedMilliseconds),8:N0} {group.Max(result => result.ElapsedMilliseconds),8:N0} {group.Count(),6}");
    }

    Console.WriteLine();
    Console.WriteLine("Per task average");
    Console.WriteLine("Target                         Task       Avg(ms)   Runs");
    Console.WriteLine(new string('-', 62));

    foreach (var group in measured.GroupBy(result => new { result.TargetName, result.TaskId }).OrderBy(group => group.Key.TargetName).ThenBy(group => group.Key.TaskId))
    {
        Console.WriteLine($"{group.Key.TargetName,-30} {group.Key.TaskId,-8} {group.Average(result => result.ElapsedMilliseconds),8:N0} {group.Count(),6}");
    }

    MeasurementResult[] failed = results.Where(result => !result.IsWarmup && !result.Succeeded).ToArray();
    if (failed.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Failed measurement runs: {failed.Length}");
    }
}

static string ToCsv(IEnumerable<MeasurementResult> results)
{
    StringBuilder builder = new();
    builder.AppendLine("targetName,deployment,taskId,taskName,run,isWarmup,succeeded,elapsedMilliseconds,responseCharacterCount,finishReason,error");

    foreach (MeasurementResult result in results)
    {
        builder.AppendLine(string.Join(',',
            Csv(result.TargetName),
            Csv(result.Deployment),
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

static string Csv(string? value)
{
    if (string.IsNullOrEmpty(value))
    {
        return string.Empty;
    }

    return $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed record AppSettings
{
    public AzureOpenAISettings AzureOpenAI { get; init; } = new();
    public BenchmarkSettings Benchmark { get; init; } = new();
}

public sealed record AzureOpenAISettings
{
    public string Endpoint { get; init; } = string.Empty;
    public string TokenScope { get; init; } = "https://cognitiveservices.azure.com/.default";
    public string? TenantId { get; init; }
    public List<string> Deployments { get; init; } = [];
    public List<ModelTarget> Targets { get; init; } = [];
}

public sealed record ModelTarget
{
    public string Name { get; init; } = string.Empty;
    public string Deployment { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
}

public sealed record BenchmarkSettings
{
    public string RequestsFile { get; init; } = "requests.json";
    public int WarmupRuns { get; init; }
    public int MeasurementRunsPerRequest { get; init; } = 1;
    public int MaxOutputTokenCount { get; init; } = 512;
    public int DelayBetweenRequestsMs { get; init; } = 250;
    public string ResultsDirectory { get; init; } = "results";
}

public sealed record BenchmarkTask
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = "You are a helpful assistant.";
    public string UserPrompt { get; init; } = string.Empty;
    public int? MaxOutputTokenCount { get; init; }
}

public sealed record MeasurementResult(
    string TargetName,
    string Deployment,
    string TaskId,
    string TaskName,
    int Run,
    bool IsWarmup,
    bool Succeeded,
    double ElapsedMilliseconds,
    int ResponseCharacterCount,
    string? FinishReason,
    string? Error);
