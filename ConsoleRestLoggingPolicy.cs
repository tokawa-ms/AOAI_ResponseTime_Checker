using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;

namespace AOAI.ResponseTime.Checker;

/// <summary>
/// OpenAI SDK の pipeline に差し込み、REST 要求と応答をコンソールへ出力するポリシー。
/// 認証ヘッダーはマスクし、JSON 本文は読みやすいよう整形する。
/// </summary>
internal sealed class ConsoleRestLoggingPolicy(object consoleLock) : PipelinePolicy
{
    /// <summary>
    /// 同期 pipeline 用の処理。送信前に要求を出力し、受信後に応答本文をバッファリングしてから出力する。
    /// </summary>
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PrintRequest(message);
        ProcessNext(message, pipeline, currentIndex);
        if (message.Response is not null)
        {
            // Content は一度読むと消費されるため、ログ出力前にバッファへ退避する。
            message.Response.BufferContent(message.CancellationToken);
        }
        PrintResponse(message);
    }

    /// <summary>
    /// 非同期 pipeline 用の処理。同期版と同じ内容を async API で実行する。
    /// </summary>
    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PrintRequest(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        if (message.Response is not null)
        {
            // 非同期呼び出しでも後続処理が本文を読める状態を保つ。
            await message.Response.BufferContentAsync(message.CancellationToken).ConfigureAwait(false);
        }
        PrintResponse(message);
    }

    /// <summary>
    /// リクエストライン、ヘッダー、本文をまとめて出力する。
    /// </summary>
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
                // アクセストークンをログに残さないよう Authorization ヘッダーだけは常に伏せる。
                string value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "<redacted>" : header.Value;
                Console.WriteLine($"{header.Key}: {value}");
            }
            Console.WriteLine("Body:");
            Console.WriteLine(ReadRequestBody(message.Request.Content, message.CancellationToken));
            Console.WriteLine();
        }
    }

    /// <summary>
    /// ステータス、ヘッダー、本文をまとめて出力する。
    /// </summary>
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

    /// <summary>
    /// BinaryContent から UTF-8 文字列を取り出し、JSON なら整形して返す。
    /// </summary>
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

    /// <summary>
    /// JSON として解釈できる本文はインデント付きに変換し、それ以外は元の本文を返す。
    /// </summary>
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
