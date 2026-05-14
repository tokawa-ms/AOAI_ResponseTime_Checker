// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
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
int maxParallelRequestsPerModel = Math.Max(1, settings.Benchmark.MaxParallelRequestsPerModel);
BenchmarkLogLevel logLevel = ParseLogLevel(settings.Benchmark.LogLevel);
object consoleLock = new();

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
Console.WriteLine($"Targets: {targets.Count}, Tasks: {tasks.Count}, WarmupRuns: {settings.Benchmark.WarmupRuns}, MeasurementRunsPerRequest: {settings.Benchmark.MeasurementRunsPerRequest}, MaxParallelRequestsPerModel: {maxParallelRequestsPerModel}, LogLevel: {logLevel}");
PrintAuthenticationInfo(settings.AzureOpenAI);
Console.WriteLine();

DefaultAzureCredential credential = CreateCredential(settings.AzureOpenAI.TenantId);
BearerTokenPolicy tokenPolicy = new(credential, settings.AzureOpenAI.TokenScope);

List<MeasurementResult> results = [];

foreach (ModelTarget target in targets)
{
    Uri endpoint = NormalizeAzureOpenAIEndpoint(target.Endpoint ?? settings.AzureOpenAI.Endpoint);
    OpenAIClientOptions clientOptions = new() { Endpoint = endpoint };
    if (ShouldLog(logLevel, BenchmarkLogLevel.Rest))
    {
        clientOptions.AddPolicy(new ConsoleRestLoggingPolicy(consoleLock), PipelinePosition.BeforeTransport);
    }

    ChatClient client = new(
        model: target.Deployment,
        authenticationPolicy: tokenPolicy,
        options: clientOptions);

    Console.WriteLine($"[{target.Name}] deployment={target.Deployment}, reasoningEffort={FormatReasoningEffort(target.ReasoningEffort)}, endpoint={endpoint}");

    results.AddRange(await RunMeasurementPhaseAsync(
        client,
        target,
        tasks,
        isWarmup: true,
        runCount: settings.Benchmark.WarmupRuns,
        settings.Benchmark,
        maxParallelRequestsPerModel,
        logLevel,
        consoleLock));

    results.AddRange(await RunMeasurementPhaseAsync(
        client,
        target,
        tasks,
        isWarmup: false,
        runCount: settings.Benchmark.MeasurementRunsPerRequest,
        settings.Benchmark,
        maxParallelRequestsPerModel,
        logLevel,
        consoleLock));

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

static async Task<List<MeasurementResult>> RunMeasurementPhaseAsync(
    ChatClient client,
    ModelTarget target,
    IReadOnlyList<BenchmarkTask> tasks,
    bool isWarmup,
    int runCount,
    BenchmarkSettings settings,
    int maxDegreeOfParallelism,
    BenchmarkLogLevel logLevel,
    object consoleLock)
{
    if (runCount <= 0)
    {
        return [];
    }

    List<MeasurementWorkItem> workItems = [];
    int sequence = 0;
    foreach (BenchmarkTask task in tasks)
    {
        for (int run = 1; run <= runCount; run++)
        {
            workItems.Add(new MeasurementWorkItem(sequence++, task, run, isWarmup));
        }
    }

    List<OrderedMeasurementResult> phaseResults = [];
    object resultsLock = new();

    await Parallel.ForEachAsync(
        workItems,
        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
        async (workItem, _) =>
        {
            MeasurementResult result = await MeasureAsync(client, target, workItem.Task, workItem.Run, workItem.IsWarmup, settings, logLevel, consoleLock);

            lock (resultsLock)
            {
                phaseResults.Add(new OrderedMeasurementResult(workItem.Sequence, result));
            }

            PrintResult(result, consoleLock);
            await DelayIfNeeded(settings.DelayBetweenRequestsMs);
        });

    return phaseResults
        .OrderBy(result => result.Sequence)
        .Select(result => result.MeasurementResult)
        .ToList();
}

static async Task<MeasurementResult> MeasureAsync(
    ChatClient client,
    ModelTarget target,
    BenchmarkTask task,
    int run,
    bool isWarmup,
    BenchmarkSettings settings,
    BenchmarkLogLevel logLevel,
    object consoleLock)
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
    ApplyReasoningEffort(options, target.ReasoningEffort);

    Stopwatch stopwatch = Stopwatch.StartNew();

    try
    {
        using CancellationTokenSource? timeout = settings.RequestTimeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds))
            : null;

        ChatCompletion completion = await client.CompleteChatAsync(messages, options, timeout?.Token ?? CancellationToken.None);
        stopwatch.Stop();

        string responseText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
        string finishReason = completion.FinishReason.ToString();
        string? diagnosticError = responseText.Length == 0 && completion.FinishReason == ChatFinishReason.Length
            ? $"No assistant response text was returned because the request reached MaxOutputTokenCount={maxOutputTokenCount}. For reasoning models, this limit includes internal reasoning tokens; increase maxOutputTokenCount or lower reasoning effort."
            : null;

        if (ShouldLog(logLevel, BenchmarkLogLevel.Prompts))
        {
            PrintPromptIoLog(target, task, run, isWarmup, responseText, diagnosticError, consoleLock);
        }

        return new MeasurementResult(
            TargetName: target.Name,
            Deployment: target.Deployment,
            ReasoningEffort: FormatReasoningEffort(target.ReasoningEffort),
            TaskId: task.Id,
            TaskName: task.Name,
            Run: run,
            IsWarmup: isWarmup,
            Succeeded: diagnosticError is null,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ResponseCharacterCount: responseText.Length,
            FinishReason: finishReason,
            Error: diagnosticError);
    }
    catch (OperationCanceledException ex) when (settings.RequestTimeoutSeconds > 0)
    {
        stopwatch.Stop();
        string error = $"Request timed out after {settings.RequestTimeoutSeconds} seconds. {FormatError(ex)}";
        if (ShouldLog(logLevel, BenchmarkLogLevel.Prompts))
        {
            PrintPromptIoLog(target, task, run, isWarmup, responseText: null, error, consoleLock);
        }

        return new MeasurementResult(
            TargetName: target.Name,
            Deployment: target.Deployment,
            ReasoningEffort: FormatReasoningEffort(target.ReasoningEffort),
            TaskId: task.Id,
            TaskName: task.Name,
            Run: run,
            IsWarmup: isWarmup,
            Succeeded: false,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ResponseCharacterCount: 0,
            FinishReason: null,
            Error: error);
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        string error = FormatError(ex);
        if (ShouldLog(logLevel, BenchmarkLogLevel.Prompts))
        {
            PrintPromptIoLog(target, task, run, isWarmup, responseText: null, error, consoleLock);
        }

        return new MeasurementResult(
            TargetName: target.Name,
            Deployment: target.Deployment,
            ReasoningEffort: FormatReasoningEffort(target.ReasoningEffort),
            TaskId: task.Id,
            TaskName: task.Name,
            Run: run,
            IsWarmup: isWarmup,
            Succeeded: false,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ResponseCharacterCount: 0,
            FinishReason: null,
            Error: error);
    }
}

static void PrintAuthenticationInfo(AzureOpenAISettings settings)
{
    string tenantDisplay = string.IsNullOrWhiteSpace(settings.TenantId)
        ? "not specified; DefaultAzureCredential will use the signed-in/default tenant"
        : settings.TenantId;

    Console.WriteLine($"Auth tenant: {tenantDisplay}");
}

static string FormatError(Exception ex)
{
    string message = ex.Message.ReplaceLineEndings(" ").Trim();
    if (message.Contains("Tenant provided in token does not match resource token", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Token tenant", StringComparison.OrdinalIgnoreCase))
    {
        return $"{message} Hint: set AzureOpenAI:TenantId to the Azure OpenAI resource tenant ID, or sign in with az login --tenant <resource-tenant-id>.";
    }

    return message;
}

static List<ModelTarget> ResolveTargets(AzureOpenAISettings settings)
{
    if (settings.Targets.Count > 0)
    {
        return settings.Targets
            .Select((target, index) => target with
            {
                Name = string.IsNullOrWhiteSpace(target.Name) ? target.Deployment : target.Name,
                Endpoint = string.IsNullOrWhiteSpace(target.Endpoint) ? settings.Endpoint : target.Endpoint,
                ReasoningEffort = NormalizeReasoningEffort(target.ReasoningEffort)
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

static string? NormalizeReasoningEffort(string? reasoningEffort)
{
    if (string.IsNullOrWhiteSpace(reasoningEffort))
    {
        return null;
    }

    string normalized = reasoningEffort.Trim().ToLowerInvariant();
    return normalized switch
    {
        "low" or "medium" or "high" => normalized,
        _ => throw new InvalidOperationException("AzureOpenAI:Targets[].ReasoningEffort must be one of: low, medium, high.")
    };
}

static void ApplyReasoningEffort(ChatCompletionOptions options, string? reasoningEffort)
{
    if (string.IsNullOrWhiteSpace(reasoningEffort))
    {
        return;
    }

    options.ReasoningEffortLevel = reasoningEffort switch
    {
        "low" => ChatReasoningEffortLevel.Low,
        "medium" => ChatReasoningEffortLevel.Medium,
        "high" => ChatReasoningEffortLevel.High,
        _ => throw new InvalidOperationException("AzureOpenAI:Targets[].ReasoningEffort must be one of: low, medium, high.")
    };
}

static string FormatReasoningEffort(string? reasoningEffort) => string.IsNullOrWhiteSpace(reasoningEffort) ? "default" : reasoningEffort;

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

static void PrintResult(MeasurementResult result, object consoleLock)
{
    string phase = result.IsWarmup ? "warmup" : "measure";
    string status = result.Succeeded ? "OK" : "NG";
    lock (consoleLock)
    {
        Console.WriteLine($"  {phase,-7} {result.TaskId,-8} run={result.Run} {status} {result.ElapsedMilliseconds,8:N0} ms {result.Error}");
    }
}

static void PrintPromptIoLog(ModelTarget target, BenchmarkTask task, int run, bool isWarmup, string? responseText, string? error, object consoleLock)
{
    string phase = isWarmup ? "warmup" : "measure";
    lock (consoleLock)
    {
        Console.WriteLine();
        Console.WriteLine($"[Prompt I/O] target={target.Name}, task={task.Id}, run={run}, phase={phase}");
        Console.WriteLine("System prompt:");
        Console.WriteLine(task.SystemPrompt);
        Console.WriteLine("User prompt:");
        Console.WriteLine(task.UserPrompt);
        Console.WriteLine(error is null ? "Assistant response:" : "Error:");
        Console.WriteLine(error ?? responseText ?? string.Empty);
        Console.WriteLine();
    }
}

static bool ShouldLog(BenchmarkLogLevel configuredLevel, BenchmarkLogLevel requiredLevel) => configuredLevel >= requiredLevel;

static BenchmarkLogLevel ParseLogLevel(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return BenchmarkLogLevel.Basic;
    }

    if (Enum.TryParse(value, ignoreCase: true, out BenchmarkLogLevel logLevel))
    {
        return logLevel;
    }

    string allowedValues = string.Join(", ", Enum.GetNames<BenchmarkLogLevel>());
    throw new InvalidOperationException($"Benchmark:LogLevel must be one of: {allowedValues}. Current value: '{value}'.");
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
    public string? ReasoningEffort { get; init; }
}

public sealed record BenchmarkSettings
{
    public string RequestsFile { get; init; } = "requests.json";
    public int WarmupRuns { get; init; }
    public int MeasurementRunsPerRequest { get; init; } = 1;
    public int MaxParallelRequestsPerModel { get; init; } = 1;
    public int MaxOutputTokenCount { get; init; } = 512;
    public int RequestTimeoutSeconds { get; init; } = 180;
    public int DelayBetweenRequestsMs { get; init; } = 250;
    public string ResultsDirectory { get; init; } = "results";
    public string? LogLevel { get; init; } = nameof(BenchmarkLogLevel.Basic);
}

public enum BenchmarkLogLevel
{
    Basic = 0,
    Prompts = 1,
    Rest = 2
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
    string ReasoningEffort,
    string TaskId,
    string TaskName,
    int Run,
    bool IsWarmup,
    bool Succeeded,
    double ElapsedMilliseconds,
    int ResponseCharacterCount,
    string? FinishReason,
    string? Error);

public sealed record MeasurementWorkItem(int Sequence, BenchmarkTask Task, int Run, bool IsWarmup);

public sealed record OrderedMeasurementResult(int Sequence, MeasurementResult MeasurementResult);

sealed class ConsoleRestLoggingPolicy(object consoleLock) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PrintRequest(message);
        ProcessNext(message, pipeline, currentIndex);
        if (message.Response is not null)
        {
            message.Response.BufferContent(message.CancellationToken);
        }
        PrintResponse(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PrintRequest(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        if (message.Response is not null)
        {
            await message.Response.BufferContentAsync(message.CancellationToken).ConfigureAwait(false);
        }
        PrintResponse(message);
    }

    private void PrintRequest(PipelineMessage message)
    {
        lock (consoleLock)
        {
            Console.WriteLine();
            Console.WriteLine("[REST request]");
            Console.WriteLine($"{message.Request.Method} {message.Request.Uri}");
            Console.WriteLine("Headers:");
            foreach (KeyValuePair<string, string> header in message.Request.Headers)
            {
                string value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "<redacted>" : header.Value;
                Console.WriteLine($"{header.Key}: {value}");
            }
            Console.WriteLine("Body:");
            Console.WriteLine(ReadRequestBody(message.Request.Content, message.CancellationToken));
            Console.WriteLine();
        }
    }

    private void PrintResponse(PipelineMessage message)
    {
        lock (consoleLock)
        {
            Console.WriteLine();
            Console.WriteLine("[REST response]");
            if (message.Response is null)
            {
                Console.WriteLine("No response was received.");
                Console.WriteLine();
                return;
            }

            Console.WriteLine($"Status: {message.Response.Status} {message.Response.ReasonPhrase}");
            Console.WriteLine("Headers:");
            foreach (KeyValuePair<string, string> header in message.Response.Headers)
            {
                Console.WriteLine($"{header.Key}: {header.Value}");
            }
            Console.WriteLine("Body:");
            Console.WriteLine(FormatBody(message.Response.Content.ToString()));
            Console.WriteLine();
        }
    }

    private static string ReadRequestBody(BinaryContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return string.Empty;
        }

        using MemoryStream stream = new();
        content.WriteTo(stream, cancellationToken);
        return FormatBody(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string FormatBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
