using System.ClientModel.Primitives;
using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using OpenAI;
using OpenAI.Chat;

#pragma warning disable OPENAI001

namespace AOAI.ResponseTime.Checker;

/// <summary>
/// Azure OpenAI の単一 model target に対して、応答時間の計測を実行する再利用可能なクラス。
/// 1 インスタンス = 1 model target。複数 target を計測したい場合は target ごとに生成する。
/// </summary>
public sealed class ResponseTimeChecker
{
    private readonly ChatClient _client;
    private readonly ModelTarget _target;
    private readonly BenchmarkSettings _settings;
    private readonly BenchmarkLogLevel _logLevel;
    private readonly object _consoleLock;

    /// <summary>この checker が計測する model target。</summary>
    public ModelTarget Target => _target;

    /// <summary>OpenAI SDK の呼び出し先として正規化済みの Azure OpenAI endpoint。</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// 既存の <see cref="ChatClient"/> を直接受け取る低レベルコンストラクタ。
    /// テスト時のモック差し替えや、独自に構築したクライアントを使いたい場合に利用する。
    /// </summary>
    public ResponseTimeChecker(
        ChatClient client,
        ModelTarget target,
        BenchmarkSettings settings,
        Uri endpoint,
        BenchmarkLogLevel logLevel = BenchmarkLogLevel.Basic,
        object? consoleLock = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logLevel = logLevel;
        _consoleLock = consoleLock ?? new object();
    }

    /// <summary>
    /// 認証情報と target / endpoint から <see cref="ChatClient"/> を構築するファクトリ。
    /// </summary>
    public static ResponseTimeChecker Create(
        ModelTarget target,
        BenchmarkSettings settings,
        string defaultEndpoint,
        TokenCredential credential,
        string tokenScope,
        BenchmarkLogLevel logLevel = BenchmarkLogLevel.Basic,
        object? consoleLock = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credential);

        // Azure OpenAI の Chat Completions API は /openai/v1/ を基準 URIとして使う。
        Uri endpoint = NormalizeAzureOpenAIEndpoint(target.Endpoint ?? defaultEndpoint);
        OpenAIClientOptions clientOptions = new() { Endpoint = endpoint };

        object effectiveLock = consoleLock ?? new object();
        if (logLevel >= BenchmarkLogLevel.Rest)
        {
            // REST ログは SDK の pipeline policy として差し込み、通常の呼び出し経路を変えずに観測する。
            clientOptions.AddPolicy(new ConsoleRestLoggingPolicy(effectiveLock), PipelinePosition.BeforeTransport);
        }

        BearerTokenPolicy tokenPolicy = new(credential, tokenScope);
        ChatClient client = new(
            model: target.Deployment,
            authenticationPolicy: tokenPolicy,
            options: clientOptions);

        return new ResponseTimeChecker(client, target, settings, endpoint, logLevel, effectiveLock);
    }

    /// <summary>
    /// 単一タスクを 1 回計測する。
    /// </summary>
    public async Task<MeasurementResult> MeasureAsync(
        BenchmarkTask task,
        int run,
        bool isWarmup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        // requests.json の 1 タスクを Chat Completions の system / user message に変換する。
        List<ChatMessage> messages =
        [
            new SystemChatMessage(task.SystemPrompt),
            new UserChatMessage(task.UserPrompt)
        ];

        ChatCompletionOptions options = new();
        int maxOutputTokenCount = task.MaxOutputTokenCount ?? _settings.MaxOutputTokenCount;
        if (maxOutputTokenCount > 0)
        {
            // reasoning model では内部 reasoning token もこの上限に含まれる点に注意する。
            options.MaxOutputTokenCount = maxOutputTokenCount;
        }
        ApplyReasoningEffort(options, _target.ReasoningEffort);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            // 設定されたタイムアウトと呼び出し元の cancellation のどちらでも中断できるよう linked token を使う。
            using CancellationTokenSource? timeout = _settings.RequestTimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds))
                : null;

            using CancellationTokenSource? linked = timeout is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

            CancellationToken effectiveToken = linked?.Token ?? cancellationToken;

            ChatCompletion completion = await _client.CompleteChatAsync(messages, options, effectiveToken).ConfigureAwait(false);
            stopwatch.Stop();

            string responseText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            string finishReason = completion.FinishReason.ToString();
            // 出力長で打ち切られて本文が空のケースは、単なる成功扱いにせず設定改善の手がかりを残す。
            string? diagnosticError = responseText.Length == 0 && completion.FinishReason == ChatFinishReason.Length
                ? $"No assistant response text was returned because the request reached MaxOutputTokenCount={maxOutputTokenCount}. For reasoning models, this limit includes internal reasoning tokens; increase maxOutputTokenCount or lower reasoning effort."
                : null;

            if (_logLevel >= BenchmarkLogLevel.Prompts)
            {
                PrintPromptIoLog(task, run, isWarmup, responseText, diagnosticError);
            }

            return new MeasurementResult(
                TargetName: _target.Name,
                Deployment: _target.Deployment,
                ReasoningEffort: FormatReasoningEffort(_target.ReasoningEffort),
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
        catch (OperationCanceledException ex) when (_settings.RequestTimeoutSeconds > 0 && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            string error = $"Request timed out after {_settings.RequestTimeoutSeconds} seconds. {FormatError(ex)}";
            if (_logLevel >= BenchmarkLogLevel.Prompts)
            {
                PrintPromptIoLog(task, run, isWarmup, responseText: null, error);
            }

            return BuildFailureResult(task, run, isWarmup, stopwatch, error);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            string error = FormatError(ex);
            if (_logLevel >= BenchmarkLogLevel.Prompts)
            {
                PrintPromptIoLog(task, run, isWarmup, responseText: null, error);
            }

            return BuildFailureResult(task, run, isWarmup, stopwatch, error);
        }
    }

    /// <summary>
    /// 与えられたタスク群に対し、warmup または本測定の 1 フェーズを並列に実行する。
    /// </summary>
    public async Task<List<MeasurementResult>> RunMeasurementPhaseAsync(
        IReadOnlyList<BenchmarkTask> tasks,
        bool isWarmup,
        int runCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        if (runCount <= 0)
        {
            return [];
        }

        int maxDegreeOfParallelism = Math.Max(1, _settings.MaxParallelRequestsPerModel);

        // タスクごとの繰り返し実行を平坦な work item に展開し、あとで元の順序に戻せるよう連番を持たせる。
        List<(int Sequence, BenchmarkTask Task, int Run)> workItems = [];
        int sequence = 0;
        foreach (BenchmarkTask task in tasks)
        {
            for (int run = 1; run <= runCount; run++)
            {
                workItems.Add((sequence++, task, run));
            }
        }

        List<(int Sequence, MeasurementResult Result)> phaseResults = [];
        object resultsLock = new();

        // Parallel.ForEachAsync で target 内のリクエストを並列化する。結果リストへの追加だけは lock で保護する。
        await Parallel.ForEachAsync(
            workItems,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken },
            async (workItem, ct) =>
            {
                MeasurementResult result = await MeasureAsync(workItem.Task, workItem.Run, isWarmup, ct).ConfigureAwait(false);

                lock (resultsLock)
                {
                    phaseResults.Add((workItem.Sequence, result));
                }

                PrintResult(result);
                await DelayIfNeeded(_settings.DelayBetweenRequestsMs, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        // 並列実行の完了順ではなく、requests.json と run 番号の順に戻して返す。
        return phaseResults
            .OrderBy(item => item.Sequence)
            .Select(item => item.Result)
            .ToList();
    }

    /// <summary>
    /// 例外やタイムアウトを、成功時と同じ列構造を持つ失敗結果へ変換する。
    /// </summary>
    private MeasurementResult BuildFailureResult(BenchmarkTask task, int run, bool isWarmup, Stopwatch stopwatch, string error) =>
        new(
            TargetName: _target.Name,
            Deployment: _target.Deployment,
            ReasoningEffort: FormatReasoningEffort(_target.ReasoningEffort),
            TaskId: task.Id,
            TaskName: task.Name,
            Run: run,
            IsWarmup: isWarmup,
            Succeeded: false,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ResponseCharacterCount: 0,
            FinishReason: null,
            Error: error);

    /// <summary>
    /// 1 リクエスト分の計測結果を、並列実行中でも読みやすい 1 行ログとして表示する。
    /// </summary>
    private void PrintResult(MeasurementResult result)
    {
        string phase = result.IsWarmup ? "warmup" : "measure";
        string status = result.Succeeded ? "OK" : "NG";
        lock (_consoleLock)
        {
            Console.WriteLine($"  {phase,-7} {result.TaskId,-8} run={result.Run} {status} {result.ElapsedMilliseconds,8:N0} ms {result.Error}");
        }
    }

    /// <summary>
    /// Prompts 以上のログレベルで、プロンプトと応答またはエラーを詳細表示する。
    /// </summary>
    private void PrintPromptIoLog(BenchmarkTask task, int run, bool isWarmup, string? responseText, string? error)
    {
        string phase = isWarmup ? "warmup" : "measure";
        lock (_consoleLock)
        {
            Console.WriteLine();
            Console.WriteLine($"[Prompt I/O] target={_target.Name}, task={task.Id}, run={run}, phase={phase}");
            Console.WriteLine("System prompt:");
            Console.WriteLine(task.SystemPrompt);
            Console.WriteLine("User prompt:");
            Console.WriteLine(task.UserPrompt);
            Console.WriteLine(error is null ? "Assistant response:" : "Error:");
            Console.WriteLine(error ?? responseText ?? string.Empty);
            Console.WriteLine();
        }
    }

    /// <summary>
    /// レート制限やサービス負荷を考慮して、リクエスト間に任意の待機を入れる。
    /// </summary>
    private static async Task DelayIfNeeded(int delayBetweenRequestsMs, CancellationToken cancellationToken)
    {
        if (delayBetweenRequestsMs > 0)
        {
            await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------ static helpers (再利用可能) ------------

    /// <summary>
    /// Azure OpenAI endpoint を OpenAI SDK が期待する /openai/v1/ 付きの URI に正規化する。
    /// </summary>
    public static Uri NormalizeAzureOpenAIEndpoint(string endpoint)
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

    /// <summary>
    /// TenantId の指定有無に応じて DefaultAzureCredential を構築する。
    /// </summary>
    public static DefaultAzureCredential CreateDefaultCredential(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return new DefaultAzureCredential();
        }

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = tenantId });
    }

    /// <summary>
    /// appsettings の Targets / Deployments から、実際に計測する target 一覧を解決する。
    /// </summary>
    public static List<ModelTarget> ResolveTargets(AzureOpenAISettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Targets.Count > 0)
        {
            // Targets 形式では target ごとの endpoint と reasoning effort を補完・正規化してから利用する。
            return settings.Targets
                .Select(target => target with
                {
                    Name = string.IsNullOrWhiteSpace(target.Name) ? target.Deployment : target.Name,
                    Endpoint = string.IsNullOrWhiteSpace(target.Endpoint) ? settings.Endpoint : target.Endpoint,
                    ReasoningEffort = NormalizeReasoningEffort(target.ReasoningEffort)
                })
                .Where(target => !string.IsNullOrWhiteSpace(target.Deployment))
                .ToList();
        }

        // Deployments 形式は古い設定との互換用。各 deployment を同名の target として扱う。
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

    /// <summary>
    /// reasoning effort の文字列を SDK に渡せる値へ正規化する。
    /// </summary>
    public static string? NormalizeReasoningEffort(string? reasoningEffort)
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

    /// <summary>
    /// 指定されている場合だけ ChatCompletionOptions に reasoning effort を設定する。
    /// </summary>
    public static void ApplyReasoningEffort(ChatCompletionOptions options, string? reasoningEffort)
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

    /// <summary>
    /// 結果出力用に reasoning effort の未指定値を default として表示する。
    /// </summary>
    public static string FormatReasoningEffort(string? reasoningEffort) =>
        string.IsNullOrWhiteSpace(reasoningEffort) ? "default" : reasoningEffort;

    /// <summary>
    /// 設定値からログレベルを解釈し、不正値の場合は利用可能な値を含めて例外にする。
    /// </summary>
    public static BenchmarkLogLevel ParseLogLevel(string? value)
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

    /// <summary>
    /// 例外メッセージを 1 行に整形し、よくある tenant mismatch には対処ヒントを付与する。
    /// </summary>
    public static string FormatError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string message = ex.Message.ReplaceLineEndings(" ").Trim();
        if (message.Contains("Tenant provided in token does not match resource token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Token tenant", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Hint: set AzureOpenAI:TenantId to the Azure OpenAI resource tenant ID, or sign in with az login --tenant <resource-tenant-id>.";
        }

        return message;
    }
}
