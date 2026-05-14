namespace AOAI.ResponseTime.Checker;

/// <summary>
/// アプリケーション全体の設定ルート。
/// appsettings.json のトップレベル構造と対応する。
/// </summary>
public sealed record AppSettings
{
    /// <summary>Azure OpenAI の接続先、認証、計測対象モデルに関する設定。</summary>
    public AzureOpenAISettings AzureOpenAI { get; init; } = new();

    /// <summary>ベンチマークの回数、並列度、出力先などに関する設定。</summary>
    public BenchmarkSettings Benchmark { get; init; } = new();
}

/// <summary>
/// Azure OpenAI リソースと deployment の設定。
/// Targets が指定されている場合は Targets を優先し、古い Deployments 形式も後方互換として扱う。
/// </summary>
public sealed record AzureOpenAISettings
{
    /// <summary>既定の Azure OpenAI エンドポイント。末尾の /openai/v1 は自動補完される。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Azure OpenAI の AAD トークンを取得するためのスコープ。</summary>
    public string TokenScope { get; init; } = "https://cognitiveservices.azure.com/.default";

    /// <summary>リソースが存在する Entra ID テナント。未指定時は DefaultAzureCredential の既定テナントを使う。</summary>
    public string? TenantId { get; init; }

    /// <summary>旧形式の deployment 名一覧。Targets が空の場合のみ利用される。</summary>
    public List<string> Deployments { get; init; } = [];

    /// <summary>deployment ごとに名前、endpoint、reasoning effort を指定できる新形式の計測対象一覧。</summary>
    public List<ModelTarget> Targets { get; init; } = [];
}

/// <summary>
/// 1 つの計測対象モデルを表す設定。
/// 同じ Azure OpenAI リソース内の複数 deployment や、別 endpoint の比較に利用する。
/// </summary>
public sealed record ModelTarget
{
    /// <summary>結果表示や CSV / JSON に出力する任意の表示名。未指定時は Deployment を使う。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Azure OpenAI に作成済みの deployment 名。</summary>
    public string Deployment { get; init; } = string.Empty;

    /// <summary>target 個別の endpoint。未指定時は AzureOpenAI.Endpoint を使う。</summary>
    public string? Endpoint { get; init; }

    /// <summary>reasoning model 向けの推論努力レベル。low / medium / high のいずれかを指定する。</summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>
/// 応答時間ベンチマークの実行条件と出力設定。
/// </summary>
public sealed record BenchmarkSettings
{
    /// <summary>計測タスクを定義した JSON ファイルへのパス。</summary>
    public string RequestsFile { get; init; } = "requests.json";

    /// <summary>本測定前に実行するウォームアップ回数。サマリー集計からは除外される。</summary>
    public int WarmupRuns { get; init; }

    /// <summary>各リクエスト定義を本測定で繰り返す回数。</summary>
    public int MeasurementRunsPerRequest { get; init; } = 1;

    /// <summary>1 つの model target に対して同時に投げる最大リクエスト数。</summary>
    public int MaxParallelRequestsPerModel { get; init; } = 1;

    /// <summary>モデル応答の最大出力トークン数。タスク側の値があればそちらを優先する。</summary>
    public int MaxOutputTokenCount { get; init; } = 512;

    /// <summary>1 リクエストのタイムアウト秒数。0 以下の場合はタイムアウトを設定しない。</summary>
    public int RequestTimeoutSeconds { get; init; } = 180;

    /// <summary>同一 target 内のリクエスト完了後に挟む待機時間ミリ秒。</summary>
    public int DelayBetweenRequestsMs { get; init; } = 250;

    /// <summary>CSV / JSON の計測結果を書き出すディレクトリ。</summary>
    public string ResultsDirectory { get; init; } = "results";

    /// <summary>ログ出力の詳細度。Basic / Prompts / Rest を指定できる。</summary>
    public string? LogLevel { get; init; } = nameof(BenchmarkLogLevel.Basic);
}

/// <summary>
/// 実行時ログの詳細度。
/// 値が大きいほど出力が増える。
/// </summary>
public enum BenchmarkLogLevel
{
    /// <summary>結果行とサマリーのみを出力する。</summary>
    Basic = 0,

    /// <summary>Basic に加えてプロンプトと応答本文を出力する。</summary>
    Prompts = 1,

    /// <summary>Prompts に加えて REST の要求・応答を出力する。</summary>
    Rest = 2
}

/// <summary>
/// requests.json に定義する 1 つの計測タスク。
/// system / user prompt と出力トークン上限を task 単位で指定できる。
/// </summary>
public sealed record BenchmarkTask
{
    /// <summary>結果の識別に使う短い ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>人間が読むためのタスク名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Chat Completions に渡す system prompt。</summary>
    public string SystemPrompt { get; init; } = "You are a helpful assistant.";

    /// <summary>Chat Completions に渡す user prompt。</summary>
    public string UserPrompt { get; init; } = string.Empty;

    /// <summary>このタスクだけに適用する最大出力トークン数。未指定時は BenchmarkSettings の値を使う。</summary>
    public int? MaxOutputTokenCount { get; init; }
}

/// <summary>
/// 1 回のリクエスト計測結果。
/// warmup と本測定の両方を同じ形式で保持し、出力時に IsWarmup で区別する。
/// </summary>
public sealed record MeasurementResult(
    /// <summary>計測対象の表示名。</summary>
    string TargetName,

    /// <summary>呼び出した Azure OpenAI deployment 名。</summary>
    string Deployment,

    /// <summary>適用した reasoning effort。未指定時は default。</summary>
    string ReasoningEffort,

    /// <summary>計測タスクの ID。</summary>
    string TaskId,

    /// <summary>計測タスクの名称。</summary>
    string TaskName,

    /// <summary>同一タスク内の実行回番号。</summary>
    int Run,

    /// <summary>warmup 実行かどうか。</summary>
    bool IsWarmup,

    /// <summary>リクエストが成功として扱われたかどうか。</summary>
    bool Succeeded,

    /// <summary>リクエスト開始から応答または失敗までの経過時間ミリ秒。</summary>
    double ElapsedMilliseconds,

    /// <summary>応答本文の文字数。失敗時は 0。</summary>
    int ResponseCharacterCount,

    /// <summary>OpenAI SDK が返した終了理由。</summary>
    string? FinishReason,

    /// <summary>失敗または診断情報のメッセージ。成功時は null。</summary>
    string? Error);
