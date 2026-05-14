# AOAI Response Time Checker

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Azure OpenAI](https://img.shields.io/badge/Azure%20OpenAI-Ready-0078D4?logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/azure/ai-services/openai/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![PRs welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](#contributing)

Azure OpenAI Service の複数 deployment / endpoint に同じリクエストを送信し、応答時間を比較する .NET 8 コンソールツールです。認証は API キーではなく `DefaultAzureCredential` を使うため、ローカル開発者、Azure CLI、Visual Studio / VS Code、マネージド ID などの Microsoft Entra ID 認証で実行できます。

## Features

- 複数の Azure OpenAI deployment を同じタスクセットで比較
- target ごとの endpoint 指定に対応し、別リソース間の比較も可能
- reasoning model 向けに `low` / `medium` / `high` の reasoning effort を指定可能
- warmup と本測定を分離し、warmup は平均値から除外
- target 内のリクエストを `MaxParallelRequestsPerModel` で並列実行
- コンソールサマリー、CSV、JSON の結果出力
- `Basic` / `Prompts` / `Rest` のログレベル切り替え
- 設定ファイル、環境変数、コマンドライン引数による上書き

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Azure OpenAI Service の endpoint と deployment
- 実行ユーザーまたはマネージド ID への Azure OpenAI の Microsoft Entra ID ベースのアクセス権
- ローカル実行時は Azure CLI、Visual Studio、または VS Code での Azure サインイン

## Quick Start

```powershell
git clone <this-repository-url>
cd AOAI_ResponseTime_Checker

Copy-Item .\appsettings.json .\appsettings.local.json
notepad .\appsettings.local.json

dotnet restore .\AOAI_ResponseTime_Checker.sln
dotnet run --project .\AOAI.ResponseTime.Checker.csproj
```

`appsettings.local.json` に実際の endpoint、tenant ID、deployment 名を設定してから実行してください。このファイルは [.gitignore](.gitignore) で除外されています。

## Configuration

[appsettings.json](appsettings.json) はサンプル設定です。実運用の値は `appsettings.local.json` に書くことを推奨します。実行時は次の順に設定が読み込まれ、後の値が前の値を上書きします。

1. `appsettings.json`
2. `appsettings.local.json`
3. 環境変数
4. コマンドライン引数

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<your-resource-name>.openai.azure.com/",
    "TokenScope": "https://cognitiveservices.azure.com/.default",
    "TenantId": "<azure-openai-resource-tenant-id>",
    "Deployments": [],
    "Targets": [
      {
        "Name": "gpt-4.1-mini",
        "Deployment": "<deployment-name-1>"
      },
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
  },
  "Benchmark": {
    "RequestsFile": "requests.json",
    "WarmupRuns": 1,
    "MeasurementRunsPerRequest": 1,
    "MaxParallelRequestsPerModel": 10,
    "MaxOutputTokenCount": 32768,
    "RequestTimeoutSeconds": 180,
    "DelayBetweenRequestsMs": 250,
    "ResultsDirectory": "results",
    "LogLevel": "Basic"
  }
}
```

### AzureOpenAI

| Key           | Description                                                                                                                                         |
| ------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Endpoint`    | 既定の Azure OpenAI endpoint。`https://<resource>.openai.azure.com/` と `https://<resource>.openai.azure.com/openai/v1/` のどちらでも指定できます。 |
| `TokenScope`  | Azure OpenAI 用の AAD トークンスコープ。通常は `https://cognitiveservices.azure.com/.default` のままで問題ありません。                              |
| `TenantId`    | Azure OpenAI リソースが存在する Microsoft Entra テナント ID。複数テナントにサインインしている環境では指定を推奨します。                             |
| `Targets`     | 推奨形式の計測対象一覧。表示名、deployment、個別 endpoint、reasoning effort を指定できます。                                                        |
| `Deployments` | 旧形式の deployment 名配列。`Targets` が空の場合だけ、各 deployment を同名 target として扱います。                                                  |

target ごとに別 endpoint を使う場合は、`Targets[].Endpoint` を指定します。

```json
{
  "Name": "eastus-gpt-4.1",
  "Deployment": "<deployment-name>",
  "Endpoint": "https://<another-resource>.openai.azure.com/"
}
```

reasoning model の設定差を比較したい場合は、同じ `Deployment` を複数 target として登録し、`ReasoningEffort` に `low`、`medium`、または `high` を指定します。未指定の場合、結果上は `default` と表示され、サービス側の既定値が使われます。

### Benchmark

| Key                           | Description                                                                           |
| ----------------------------- | ------------------------------------------------------------------------------------- |
| `RequestsFile`                | 計測タスクを定義した JSON ファイル。                                                  |
| `WarmupRuns`                  | 本測定前のウォームアップ回数。サマリー平均からは除外されます。                        |
| `MeasurementRunsPerRequest`   | 各タスクを本測定で繰り返す回数。                                                      |
| `MaxParallelRequestsPerModel` | 1 つの target 内で同時に実行する最大リクエスト数。                                    |
| `MaxOutputTokenCount`         | 1 回の API 呼び出しの最大出力トークン数。タスク側の値がある場合はそちらを優先します。 |
| `RequestTimeoutSeconds`       | 1 回の API 呼び出しのタイムアウト秒数。`0` 以下ではタイムアウトを設定しません。       |
| `DelayBetweenRequestsMs`      | 各 API 呼び出し後に待機するミリ秒。`0` 以下では待機しません。                         |
| `ResultsDirectory`            | CSV / JSON の出力先ディレクトリ。                                                     |
| `LogLevel`                    | `Basic`、`Prompts`、`Rest` のいずれか。                                               |

コマンドライン引数で一時的に上書きすることもできます。

```powershell
dotnet run --project .\AOAI.ResponseTime.Checker.csproj -- Benchmark:LogLevel=Rest Benchmark:MaxParallelRequestsPerModel=1
```

## Authentication

このツールは `DefaultAzureCredential` を使います。ローカルで Azure CLI 認証を使う場合は、Azure OpenAI リソースのテナントを明示してサインインできます。

```powershell
az login --tenant <azure-openai-resource-tenant-id>
az account set --subscription <subscription-id-or-name>
```

`Tenant provided in token does not match resource token` または tenant mismatch 系のエラーが出る場合は、`AzureOpenAI:TenantId` を Azure OpenAI リソース側のテナント ID に設定してください。

## Request File

[requests.json](requests.json) は JSON 配列です。タスクを追加すると、そのまま計測対象が増えます。

```json
{
  "id": "task-01",
  "name": "Short fact",
  "systemPrompt": "You are a concise assistant. Answer in Japanese.",
  "userPrompt": "Azure OpenAI Service を一文で説明してください。"
}
```

タスクごとに `maxOutputTokenCount` を指定すると、そのタスクだけ `Benchmark:MaxOutputTokenCount` を上書きします。

```json
{
  "id": "task-long-01",
  "name": "Long planning prompt",
  "systemPrompt": "You are an Azure AI performance specialist. Answer in Japanese.",
  "userPrompt": "...",
  "maxOutputTokenCount": 8192
}
```

サンプルの [requests.json](requests.json) には、短い事実確認から長い計画作成まで 10 個のタスクを含めています。reasoning model では内部推論トークンも出力上限を消費するため、長いタスクや reasoning effort を指定する場合は `MaxOutputTokenCount` を大きめに設定してください。

## Output

実行すると、コンソールに target 別 summary と task 別平均が表示されます。詳細結果は `results` ディレクトリにタイムスタンプ付きで保存されます。

```text
results/
  response-times-YYYYMMDD-HHMMSS.csv
  response-times-YYYYMMDD-HHMMSS.json
```

CSV / JSON には次の列またはプロパティが出力されます。

| Field                    | Description                                       |
| ------------------------ | ------------------------------------------------- |
| `targetName`             | 計測対象の表示名。                                |
| `deployment`             | 呼び出した deployment 名。                        |
| `reasoningEffort`        | 適用した reasoning effort。未指定時は `default`。 |
| `taskId` / `taskName`    | 計測タスクの ID と名称。                          |
| `run`                    | 同一タスク内の実行回番号。                        |
| `isWarmup`               | warmup 実行かどうか。                             |
| `succeeded`              | 成功として扱われたかどうか。                      |
| `elapsedMilliseconds`    | リクエスト開始から応答または失敗までの経過時間。  |
| `responseCharacterCount` | 応答本文の文字数。失敗時は `0`。                  |
| `finishReason`           | OpenAI SDK が返した終了理由。                     |
| `error`                  | 失敗または診断情報。                              |

失敗した本測定は CSV / JSON に記録されますが、コンソールの平均値からは除外されます。

## Log Levels

| Level     | Description                                                                                                                                                                                            |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Basic`   | 実行状況、1 リクエストごとの結果、サマリーを表示します。未指定または空文字の場合の既定値です。                                                                                                         |
| `Prompts` | `Basic` に加えて、送信した system / user prompt と assistant response またはエラーを表示します。                                                                                                       |
| `Rest`    | `Prompts` に加えて、OpenAI クライアントの HTTP pipeline で見える REST メソッド、URL、ヘッダー、body、HTTP 応答コード、レスポンス body を表示します。`Authorization` ヘッダーのトークンは表示しません。 |

`Rest` は詳細な検証に便利ですが、プロンプトやレスポンス本文がコンソールに出ます。機密情報を含むタスクでは使用しないでください。

## Measurement Notes

- 同一 target では、warmup フェーズを完了してから本測定フェーズを実行します。
- 各フェーズでは、`requests.json` のタスクと run 回数を展開し、`MaxParallelRequestsPerModel` の範囲で並列実行します。
- 結果リストは、並列実行の完了順ではなく、タスク定義順と run 番号順に戻して保存します。
- 平均応答時間は、モデル、プロンプト長、出力トークン数、ネットワーク、認証トークン取得、サービス側混雑、レート制限、同時実行条件に影響されます。
- 厳密な比較が必要な場合は、複数回実行、P50 / P95、実行順序のランダム化、リージョンと同時実行条件の固定も検討してください。

## Project Structure

```text
AOAI_ResponseTime_Checker/
  Program.cs                    # エントリポイント、設定読み込み、実行制御
  ResponseTimeChecker.cs         # Azure OpenAI 呼び出しと計測処理
  BenchmarkReporter.cs           # サマリー表示、CSV / JSON 出力
  ConsoleRestLoggingPolicy.cs    # REST ログ出力用 pipeline policy
  Models.cs                      # 設定、タスク、結果モデル
  appsettings.json               # サンプル設定
  requests.json                  # サンプル計測タスク
```

## Contributing

Issue や Pull Request は歓迎です。変更する場合は、まず `dotnet build .\AOAI_ResponseTime_Checker.sln` でビルドが通ることを確認してください。計測仕様に影響する変更では、README の設定項目、出力列、ログレベルの説明も合わせて更新してください。

## License

This project is licensed under the [MIT License](LICENSE).
