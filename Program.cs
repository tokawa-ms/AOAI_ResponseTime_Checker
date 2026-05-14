// コンソールとファイル出力で日本語が文字化けしないよう、UTF-8 を明示する。
using System.Text;
using System.Text.Json;
using AOAI.ResponseTime.Checker;
using Microsoft.Extensions.Configuration;

Console.OutputEncoding = Encoding.UTF8;

// 設定ファイルとリクエスト定義の JSON は、大文字小文字の揺れを許容して読み込む。
JsonSerializerOptions jsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

// appsettings.json を必須設定、appsettings.local.json / 環境変数 / コマンドライン引数を上書き設定として読み込む。
IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

AppSettings settings = configuration.Get<AppSettings>() ?? new AppSettings();
List<ModelTarget> targets = ResponseTimeChecker.ResolveTargets(settings.AzureOpenAI);
List<BenchmarkTask> tasks = LoadTasks(settings.Benchmark.RequestsFile, jsonOptions);
int maxParallelRequestsPerModel = Math.Max(1, settings.Benchmark.MaxParallelRequestsPerModel);
BenchmarkLogLevel logLevel = ResponseTimeChecker.ParseLogLevel(settings.Benchmark.LogLevel);

// 複数リクエストを並列実行してもログの行が混ざらないよう、全 checker で同じロックを共有する。
object consoleLock = new();

if (targets.Count == 0)
{
    throw new InvalidOperationException("AzureOpenAI:Targets または AzureOpenAI:Deployments を 1 つ以上設定してください。");
}

if (tasks.Count == 0)
{
    throw new InvalidOperationException($"計測タスクが見つかりません: {settings.Benchmark.RequestsFile}");
}

Console.WriteLine("Azure OpenAI response time checker");
Console.WriteLine($"Targets: {targets.Count}, Tasks: {tasks.Count}, WarmupRuns: {settings.Benchmark.WarmupRuns}, MeasurementRunsPerRequest: {settings.Benchmark.MeasurementRunsPerRequest}, MaxParallelRequestsPerModel: {maxParallelRequestsPerModel}, LogLevel: {logLevel}");
PrintAuthenticationInfo(settings.AzureOpenAI);
Console.WriteLine();

// Azure CLI / Visual Studio / Managed Identity など、DefaultAzureCredential が対応する認証を利用する。
var credential = ResponseTimeChecker.CreateDefaultCredential(settings.AzureOpenAI.TenantId);

List<MeasurementResult> results = [];

foreach (ModelTarget target in targets)
{
    // target ごとに endpoint / deployment / reasoning effort が異なる可能性があるため、checker は target 単位で作成する。
    ResponseTimeChecker checker = ResponseTimeChecker.Create(
        target,
        settings.Benchmark,
        settings.AzureOpenAI.Endpoint,
        credential,
        settings.AzureOpenAI.TokenScope,
        logLevel,
        consoleLock);

    Console.WriteLine($"[{target.Name}] deployment={target.Deployment}, reasoningEffort={ResponseTimeChecker.FormatReasoningEffort(target.ReasoningEffort)}, endpoint={checker.Endpoint}");

    // warmup はモデルや接続の初回遅延を本測定から分離するために実行し、サマリー集計からは除外する。
    results.AddRange(await checker.RunMeasurementPhaseAsync(tasks, isWarmup: true, runCount: settings.Benchmark.WarmupRuns));
    results.AddRange(await checker.RunMeasurementPhaseAsync(tasks, isWarmup: false, runCount: settings.Benchmark.MeasurementRunsPerRequest));

    Console.WriteLine();
}

BenchmarkReporter.PrintSummary(results);

(string csvPath, string jsonPath) = await BenchmarkReporter.WriteResultsAsync(results, settings.Benchmark.ResultsDirectory);

Console.WriteLine();
Console.WriteLine($"CSV:  {csvPath}");
Console.WriteLine($"JSON: {jsonPath}");

static void PrintAuthenticationInfo(AzureOpenAISettings settings)
{
    // TenantId が未指定の場合は DefaultAzureCredential の既定テナントに委ねる。
    string tenantDisplay = string.IsNullOrWhiteSpace(settings.TenantId)
        ? "not specified; DefaultAzureCredential will use the signed-in/default tenant"
        : settings.TenantId;

    Console.WriteLine($"Auth tenant: {tenantDisplay}");
}

static List<BenchmarkTask> LoadTasks(string requestsFile, JsonSerializerOptions jsonOptions)
{
    // dotnet run / 発行済み exe のどちらでも読めるよう、実行ディレクトリとカレントディレクトリの両方を確認する。
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
