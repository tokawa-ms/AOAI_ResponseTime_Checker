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
    "Targets": [
      { "Name": "gpt-4.1-mini", "Deployment": "<deployment-name-1>" },
      { "Name": "gpt-4.1", "Deployment": "<deployment-name-2>" }
    ]
  }
}
```

同一リージョンの別リソースを比べたい場合は、target ごとに `Endpoint` を指定できます。

```json
{
  "Name": "eastus-gpt-4.1",
  "Deployment": "<deployment-name>",
  "Endpoint": "https://<another-resource>.openai.azure.com/"
}
```

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
  "userPrompt": "Azure OpenAI Service を一文で説明してください。",
  "maxOutputTokenCount": 128
}
```

現在は短いものから長いものまで 10 個のタスクを入れています。

## 実行

```powershell
dotnet restore
dotnet run --project .\AOAI.ResponseTime.Checker.csproj
```

結果はコンソールに summary と task 別平均が表示され、`results` フォルダーに CSV と JSON が出力されます。

## 計測仕様

- `WarmupRuns` は平均計算から除外します。
- `MeasurementRunsPerRequest` を増やすと、タスクごとに複数回測定して平均できます。
- 失敗した本測定は CSV/JSON に記録されますが、平均値からは除外します。
- endpoint は `https://<resource>.openai.azure.com/` と `https://<resource>.openai.azure.com/openai/v1/` のどちらでも指定できます。

## 注意

平均応答時間は、モデルやプロンプト長だけでなく、出力トークン数、ネットワーク、認証トークン取得、サービス側の混雑、レート制限にも影響されます。厳密な比較では、実行順序のランダム化、複数回実行、P50/P95、同時実行条件の固定も検討してください。
