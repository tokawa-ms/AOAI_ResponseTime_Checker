# AOAI Response Time Checker

.NET 8 と NuGet `OpenAI` ライブラリを使い、Azure OpenAI の複数 deployment に同じリクエストを送信して平均応答時間を計測するサンプルです。認証は `DefaultAzureCredential` を使います。

## 前提

- .NET 8 SDK
- Azure OpenAI Service の endpoint と deployment
- 実行ユーザーまたはマネージド ID に Azure OpenAI への Microsoft Entra ID ベースのアクセス権
- ローカル実行時は `az login` または Visual Studio / VS Code の Azure サインイン

## 設定

[appsettings.json](appsettings.json) は GitHub に push するサンプル設定です。実際の endpoint や deployment 名は、Git 管理しない `appsettings.local.json` に書きます。

まず [appsettings.json](appsettings.json) を `appsettings.local.json` にコピーし、`AzureOpenAI` を編集します。

```powershell
Copy-Item .\appsettings.json .\appsettings.local.json
```

`appsettings.local.json` は [.gitignore](.gitignore) で除外済みです。実行時は `appsettings.json` の後に `appsettings.local.json` が読み込まれるため、ローカル設定がサンプル設定を上書きします。

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource-name>.openai.azure.com/",
    "TenantId": "<azure-openai-resource-tenant-id>",
    "Targets": [
      { "Name": "gpt-4.1-mini", "Deployment": "<deployment-name-1>" },
      {
        "Name": "gpt-5-mini-low",
        "Deployment": "<deployment-name-2>",
        "ReasoningEffort": "low"
      },
      {
        "Name": "gpt-5-mini-medium",
        "Deployment": "<deployment-name-2>",
        "ReasoningEffort": "medium"
      }
    ]
  }
}
```

`TenantId` は Azure OpenAI リソースが存在する Microsoft Entra テナント ID です。複数テナントにサインインしている環境では、これを空のままにすると別テナントのトークンが使われ、`Tenant provided in token does not match resource token` で失敗することがあります。

ローカルの Azure CLI 認証を使う場合は、リソースのテナントを明示してサインインすることもできます。

```powershell
az login --tenant <azure-openai-resource-tenant-id>
az account set --subscription <subscription-id-or-name>
```

同一リージョンの別リソースを比べたい場合は、target ごとに `Endpoint` を指定できます。

```json
{
  "Name": "eastus-gpt-4.1",
  "Deployment": "<deployment-name>",
  "Endpoint": "https://<another-resource>.openai.azure.com/"
}
```

reasoning model の `reasoning_effort` を比較したい場合は、同じ `Deployment` を複数の target として登録し、`ReasoningEffort` に `low`、`medium`、または `high` を指定します。未指定の場合はサービス側の既定値を使います。

コマンドラインや環境変数でも上書きできます。

```powershell
dotnet run --project .\AOAI.ResponseTime.Checker.csproj -- AzureOpenAI:Endpoint=https://<your-resource-name>.openai.azure.com/
```

## リクエスト定義

[requests.json](requests.json) は JSON 配列です。タスクを追加すると、そのまま計測対象が増えます。

```json
{
  "id": "task-01",
  "name": "Short fact",
  "systemPrompt": "You are a concise assistant. Answer in Japanese.",
  "userPrompt": "Azure OpenAI Service を一文で説明してください。"
}
```

現在は短いものから長いものまで 10 個のタスクを入れています。出力上限は通常 `Benchmark:MaxOutputTokenCount` を使います。reasoning model では内部推論トークンもこの上限を消費するため、サンプル設定では `32768` にしています。タスクごとに個別調整したい場合だけ、タスクへ `maxOutputTokenCount` を追加してください。

## 実行

```powershell
dotnet restore
dotnet run --project .\AOAI.ResponseTime.Checker.csproj
```

結果はコンソールに summary と task 別平均が表示され、`results` フォルダーに CSV と JSON が出力されます。

## ログレベル

`Benchmark:LogLevel` でコンソールへの詳細ログを切り替えられます。

```json
{
  "Benchmark": {
    "LogLevel": "Basic"
  }
}
```

- `Basic`: 従来どおり、実行状況とサマリーを表示します。
- `Prompts`: `Basic` に加えて、送信した system / user prompt と、モデルから返った assistant response またはエラーを表示します。
- `Rest`: `Prompts` に加えて、OpenAI クライアントの HTTP pipeline で見える REST API のメソッド、URL、ヘッダー、リクエスト body、HTTP 応答コード、レスポンス body を表示します。`Authorization` ヘッダーのトークンは表示しません。

未指定または空文字の場合は `Basic` として扱います。

コマンドラインで一時的に変更することもできます。

```powershell
dotnet run --project .\AOAI.ResponseTime.Checker.csproj -- Benchmark:LogLevel=Rest
```

## 計測仕様

- `WarmupRuns` は平均計算から除外します。
- `MeasurementRunsPerRequest` を増やすと、タスクごとに複数回測定して平均できます。
- `MaxParallelRequestsPerModel` は同一 model target 内で同時に実行する API リクエスト数の上限です。`1` の場合は従来どおり直列実行です。
- `MaxOutputTokenCount` は 1 回の API 呼び出しで生成できる出力トークン数の上限です。reasoning model では、画面に表示される本文だけでなく内部推論トークンも含まれます。
- `RequestTimeoutSeconds` は 1 回の API 呼び出しのタイムアウト秒数です。`0` 以下にするとタイムアウトを設定しません。
- 失敗した本測定は CSV/JSON に記録されますが、平均値からは除外します。
- endpoint は `https://<resource>.openai.azure.com/` と `https://<resource>.openai.azure.com/openai/v1/` のどちらでも指定できます。
- 同一 model target では、まずウォームアップ実行を `MaxParallelRequestsPerModel` の範囲で並列実行し、すべて完了してから本測定を同じ上限で並列実行します。

## 注意

平均応答時間は、モデルやプロンプト長だけでなく、出力トークン数、ネットワーク、認証トークン取得、サービス側の混雑、レート制限にも影響されます。厳密な比較では、実行順序のランダム化、複数回実行、P50/P95、同時実行条件の固定も検討してください。
