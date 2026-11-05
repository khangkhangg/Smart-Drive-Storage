using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miu.Notify;

/// <summary>One incoming update: a text message or a button tap (callback).</summary>
public sealed record TelegramUpdate(long UpdateId, long ChatId, string? Text, string? CallbackData, string? CallbackId, long? MessageId, bool IsPrivate = true);

public sealed record TelegramButton(string Text, string CallbackData);

public sealed class TelegramApiException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>The few Telegram Bot API calls Miu needs.</summary>
public interface ITelegramApi
{
    Task<string> GetMeAsync(CancellationToken ct);
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct);
    Task<long> SendMessageAsync(long chatId, string text, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken ct);
    Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct);
    Task EditMessageAsync(long chatId, long messageId, string text, CancellationToken ct);
}

/// <summary>Minimal HTTPS client for https://api.telegram.org (no third-party library). The token never appears in errors.</summary>
public sealed class TelegramBotApi(HttpClient http, string token) : ITelegramApi
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<string> GetMeAsync(CancellationToken ct)
    {
        var r = await CallAsync("getMe", null, ct);
        return r.GetProperty("username").GetString() ?? "";
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var r = await CallAsync("getUpdates", new { offset, timeout = timeoutSeconds, allowed_updates = new[] { "message", "callback_query" } }, ct);
        var list = new List<TelegramUpdate>();
        foreach (var u in r.EnumerateArray())
        {
            var id = u.GetProperty("update_id").GetInt64();
            if (u.TryGetProperty("message", out var m) && m.TryGetProperty("chat", out var chat))
                list.Add(new TelegramUpdate(id, chat.GetProperty("id").GetInt64(), m.TryGetProperty("text", out var t) ? t.GetString() : null, null, null,
                    m.TryGetProperty("message_id", out var mid) ? mid.GetInt64() : null, IsPrivateChat(chat)));
            else if (u.TryGetProperty("callback_query", out var cb) && cb.TryGetProperty("message", out var cm) && cm.TryGetProperty("chat", out var cchat))
                list.Add(new TelegramUpdate(id, cchat.GetProperty("id").GetInt64(), null,
                    cb.TryGetProperty("data", out var d) ? d.GetString() : null, cb.GetProperty("id").GetString(),
                    cm.TryGetProperty("message_id", out var cmid) ? cmid.GetInt64() : null, IsPrivateChat(cchat)));
            else list.Add(new TelegramUpdate(id, 0, null, null, null, null)); // something else: skip but advance the offset
        }
        return list;
    }

    private static bool IsPrivateChat(JsonElement chat) =>
        chat.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "private";

    public async Task<long> SendMessageAsync(long chatId, string text, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken ct)
    {
        object? markup = buttons is null ? null : new
        {
            inline_keyboard = buttons.Select(row => row.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray()).ToArray(),
        };
        var r = await CallAsync("sendMessage", new { chat_id = chatId, text, reply_markup = markup }, ct);
        return r.GetProperty("message_id").GetInt64();
    }

    public Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct) =>
        CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text }, ct);

    public Task EditMessageAsync(long chatId, long messageId, string text, CancellationToken ct) =>
        CallAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text }, ct);

    private async Task<JsonElement> CallAsync(string method, object? body, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{token}/{method}";
        using var response = body is null
            ? await http.GetAsync(url, ct)
            : await http.PostAsync(url, JsonContent.Create(body, options: Json), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new TelegramApiException($"Telegram answered {(int)response.StatusCode} without JSON.", (int)response.StatusCode); }
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var desc = root.TryGetProperty("description", out var d) ? d.GetString() : "Unknown error";
            var code = root.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)response.StatusCode;
            throw new TelegramApiException($"Telegram: {desc}", code);
        }
        return root.GetProperty("result");
    }
}
