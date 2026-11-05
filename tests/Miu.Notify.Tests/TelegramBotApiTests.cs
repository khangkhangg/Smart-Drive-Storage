using System.Net;
using System.Text;
using System.Text.Json;
using Miu.Notify;

namespace Miu.Notify.Tests;

public class TelegramBotApiTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> reply) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            var (code, json) = reply(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task GetMe_returns_username()
    {
        var h = new Handler((_, _) => (HttpStatusCode.OK, """{"ok":true,"result":{"id":1,"is_bot":true,"username":"MiuHelperBot"}}"""));
        var api = new TelegramBotApi(new HttpClient(h), "123:ABC");
        (await api.GetMeAsync(CancellationToken.None)).Should().Be("MiuHelperBot");
        h.Requests.Single().Url.Should().Be("https://api.telegram.org/bot123:ABC/getMe");
    }

    [Fact]
    public async Task GetUpdates_parses_messages_and_callbacks()
    {
        var h = new Handler((_, _) => (HttpStatusCode.OK, """
            {"ok":true,"result":[
              {"update_id":10,"message":{"message_id":5,"chat":{"id":42,"type":"private"},"text":"/status"}},
              {"update_id":11,"callback_query":{"id":"cb1","data":"c:7","message":{"message_id":6,"chat":{"id":42,"type":"private"}}}},
              {"update_id":12,"message":{"message_id":7,"chat":{"id":-100,"type":"group"},"text":"/pair 1"}}
            ]}
            """));
        var api = new TelegramBotApi(new HttpClient(h), "t");
        var updates = await api.GetUpdatesAsync(9, 30, CancellationToken.None);
        updates.Should().HaveCount(3);
        updates[0].Should().Be(new TelegramUpdate(10, 42, "/status", null, null, 5));
        updates[1].Should().Be(new TelegramUpdate(11, 42, null, "c:7", "cb1", 6));
        updates[2].IsPrivate.Should().BeFalse(); // groups can't pair
        h.Requests.Single().Body.Should().Contain("\"offset\":9").And.Contain("\"timeout\":30");
    }

    [Fact]
    public async Task SendMessage_with_buttons()
    {
        var h = new Handler((_, _) => (HttpStatusCode.OK, """{"ok":true,"result":{"message_id":99,"chat":{"id":42}}}"""));
        var api = new TelegramBotApi(new HttpClient(h), "t");
        var id = await api.SendMessageAsync(42, "Hello", [[new TelegramButton("Clean now", "c:1"), new TelegramButton("Snooze", "s:2")]], CancellationToken.None);
        id.Should().Be(99);
        using var doc = JsonDocument.Parse(h.Requests.Single().Body);
        doc.RootElement.GetProperty("chat_id").GetInt64().Should().Be(42);
        doc.RootElement.GetProperty("text").GetString().Should().Be("Hello");
        var kb = doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        kb[0][1].GetProperty("callback_data").GetString().Should().Be("s:2");
    }

    [Fact]
    public async Task Api_errors_throw_with_description()
    {
        var h = new Handler((_, _) => (HttpStatusCode.Unauthorized, """{"ok":false,"error_code":401,"description":"Unauthorized"}"""));
        var api = new TelegramBotApi(new HttpClient(h), "bad");
        var act = () => api.GetMeAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<TelegramApiException>()).Which.Message.Should().Contain("Unauthorized");
    }

    [Fact]
    public void Token_is_never_in_the_exception_text()
    {
        var ex = new TelegramApiException("Not Found", 404);
        ex.ToString().Should().NotContain("123:ABC");
    }
}
