using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.Core.Automation;
using Miu.Core.Cleanup;
using Miu.Core.Learning;
using Miu.Core.Rules;
using Miu.Notify;

namespace Miu.App.Tests;

public sealed class TelegramServiceTests : IDisposable
{
    private const long Me = 42, Stranger = 666;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-tg-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTelegram _api = new();
    private readonly CleanupServiceTests.FakeBin _bin = new();
    private DateTime _utc = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public TelegramServiceTests()
    {
        foreach (var n in new[] { "a.dmp", "b.dmp" })
        {
            var p = Path.Combine(_root, "crash", n);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[3000]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-30));
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private (AppServices S, TelegramService T) Setup(bool paired = true)
    {
        var s = AppServices.Create(":memory:", _bin, secrets: new MemorySecrets());
        var automation = new AutomationService(s, new AutomationTests.FakeNotifier(), new Miu.Platform.Abstractions.AlwaysPlugged(), () => _utc.ToLocalTime(), _ => { });
        var t = new TelegramService(s, automation, _ => _api, () => _utc);
        if (paired)
        {
            t.ConnectAsync("123:ABC").GetAwaiter().GetResult();
            var code = t.StartPairing();
            t.HandleAsync(Msg(Me, "/pair " + code)).GetAwaiter().GetResult();
            _api.Sent.Clear();
        }
        return (s, t);
    }

    private static TelegramUpdate Msg(long chat, string text) => new(1, chat, text, null, null, 1);
    private static TelegramUpdate Tap(long chat, string data) => new(2, chat, null, data, "cb", 7);

    [AvaloniaFact]
    public async Task Pairing_needs_the_exact_code_in_time()
    {
        var (s, t) = Setup(paired: false);
        await t.ConnectAsync("123:ABC");
        t.BotUsername.Should().Be("MiuTestBot");
        s.Secrets.Get(TelegramService.TokenKey).Should().Be("123:ABC");
        var code = t.StartPairing();
        await t.HandleAsync(Msg(Stranger, "/pair 000000"));
        t.IsPaired.Should().BeFalse();
        _utc = _utc.AddMinutes(11);
        await t.HandleAsync(Msg(Me, "/pair " + code));
        t.IsPaired.Should().BeFalse(); // expired
        code = t.StartPairing();
        await t.HandleAsync(Msg(Me, "/pair " + code));
        t.IsPaired.Should().BeTrue();
        t.ChatId.Should().Be(Me);
        _api.Sent.Last().Text.Should().Contain("Paired");
    }

    [AvaloniaFact]
    public async Task Other_chats_are_ignored()
    {
        var (_, t) = Setup();
        await t.HandleAsync(Msg(Stranger, "/status"));
        await t.HandleAsync(Tap(Stranger, "y:1"));
        _api.Sent.Should().BeEmpty();
        _api.Answered.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Help_and_status_reply()
    {
        var (_, t) = Setup();
        await t.HandleAsync(Msg(Me, "/help"));
        _api.Sent.Single().Text.Should().Contain("/status").And.Contain("/unpair");
        await t.HandleAsync(Msg(Me, "/status"));
        _api.Sent.Last().Text.Should().Contain("Automations");
    }

    [AvaloniaFact]
    public async Task Clean_now_asks_confirms_cleans_still_safe_items_and_can_undo()
    {
        var (s, t) = Setup();
        var watchId = s.Watches.Add(new Watch(0, "Crash dumps", _root, WatchMode.Suggest, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters()));
        var a = Path.Combine(_root, "crash", "a.dmp");
        var b = Path.Combine(_root, "crash", "b.dmp");
        await t.NotifyAsync(new MiuNotification("Crash dumps", "2 items can be cleaned.", [], watchId,
            [new CartItem(a, "a.dmp", 3000, Verdict.Safe, "r"), new CartItem(b, "b.dmp", 3000, Verdict.Safe, "r")]));
        var alert = _api.Sent.Single();
        alert.Buttons!.SelectMany(r => r).Select(x => x.Text).Should().Contain(["Clean now", "Show list", "Snooze 7 days", "Never for this folder"]);
        var clean = alert.Buttons!.SelectMany(r => r).Single(x => x.Text == "Clean now").CallbackData;

        await t.HandleAsync(Tap(Me, clean));
        _bin.Received.Should().BeEmpty(); // asks first
        var confirm = _api.Sent.Last();
        confirm.Text.Should().Contain("Move 2 items");
        var yes = confirm.Buttons!.SelectMany(r => r).Single(x => x.Text == "Yes, clean").CallbackData;

        // Meanwhile you chose to keep b.dmp in the app.
        new LearningService(s).Teach(b, false, DecisionAction.Keep, null, null);

        await t.HandleAsync(Tap(Me, yes));
        _bin.Received.Should().Equal(a);
        s.Actions.Recent(1).Single().Origin.Should().Be("telegram");
        var done = _api.Sent.Last();
        done.Text.Should().Contain("Moved 1 item");
        var undo = done.Buttons!.SelectMany(r => r).Single(x => x.Text == "Undo").CallbackData;

        await t.HandleAsync(Tap(Me, yes)); // second tap: already used
        _bin.Received.Should().HaveCount(1);
        _api.Answered.Last().Should().Contain("expired");

        await t.HandleAsync(Tap(Me, undo));
        _bin.Restored.Should().Equal(a);
    }

    [AvaloniaFact]
    public async Task Buttons_expire_after_a_day()
    {
        var (s, t) = Setup();
        var watchId = s.Watches.Add(new Watch(0, "Crash dumps", _root, WatchMode.Suggest, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters()));
        await t.NotifyAsync(new MiuNotification("x", "y", [], watchId, [new CartItem(Path.Combine(_root, "crash", "a.dmp"), "a.dmp", 3000, Verdict.Safe, "r")]));
        var clean = _api.Sent.Single().Buttons!.SelectMany(r => r).Single(x => x.Text == "Clean now").CallbackData;
        _utc = _utc.AddHours(25);
        await t.HandleAsync(Tap(Me, clean));
        _api.Answered.Last().Should().Contain("expired");
        _bin.Received.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Malformed_callbacks_do_nothing()
    {
        var (_, t) = Setup();
        foreach (var data in new[] { "y:", "y:abc", "zz:1", "", "y:99999" })
            await t.HandleAsync(Tap(Me, data));
        _bin.Received.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Snooze_and_never_change_the_watch()
    {
        var (s, t) = Setup();
        var id = s.Watches.Add(new Watch(0, "Downloads", _root, WatchMode.Suggest, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters(),
            NextRunLocal: _utc.ToLocalTime().AddHours(1)));
        await t.HandleAsync(Tap(Me, $"s:{id}"));
        s.Watches.Get(id)!.NextRunLocal.Should().BeCloseTo(_utc.ToLocalTime().AddDays(7), TimeSpan.FromMinutes(1));
        await t.HandleAsync(Tap(Me, $"n:{id}"));
        s.Watches.Get(id)!.Enabled.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Pause_resume_and_unpair()
    {
        var (s, t) = Setup();
        await t.HandleAsync(Msg(Me, "/pause 2h"));
        _api.Sent.Last().Text.Should().Contain("Paused");
        await t.HandleAsync(Msg(Me, "/resume"));
        await t.HandleAsync(Msg(Me, "/unpair"));
        t.IsPaired.Should().BeFalse();
        s.Secrets.Get(TelegramService.TokenKey).Should().BeNull();
        _api.Sent.Clear();
        await t.HandleAsync(Msg(Me, "/status"));
        _api.Sent.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Polling_survives_network_errors()
    {
        var (_, t) = Setup();
        _api.FailNext = new HttpRequestException("offline");
        (await t.PollOnceAsync(CancellationToken.None)).Should().BeFalse();
        _api.Queue.Enqueue(new TelegramUpdate(5, Me, "/help", null, null, 1));
        (await t.PollOnceAsync(CancellationToken.None)).Should().BeTrue();
        _api.Sent.Single().Text.Should().Contain("/status");
        _api.LastOffset.Should().Be(0);
        await t.PollOnceAsync(CancellationToken.None);
        _api.LastOffset.Should().Be(6);
    }

    private async Task<string> AlertAsync(AppServices s, TelegramService t, params string[] names)
    {
        var watchId = s.Watches.Add(new Watch(0, "Crash dumps", _root, WatchMode.Suggest, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters()));
        await t.NotifyAsync(new MiuNotification("Crash dumps", "can be cleaned", [], watchId,
            names.Select(n => new CartItem(Path.Combine(_root, "crash", n), n, 3000, Verdict.Safe, "r")).ToList()));
        return _api.Sent.Last().Buttons!.SelectMany(r => r).Single(x => x.Text == "Clean now").CallbackData;
    }

    [AvaloniaFact]
    public async Task Unpair_works_even_if_the_goodbye_cannot_be_sent()
    {
        var (s, t) = Setup();
        _api.FailSends = new HttpRequestException("offline");
        await t.HandleAsync(Msg(Me, "/unpair"));
        t.IsPaired.Should().BeFalse();
        s.Secrets.Get(TelegramService.TokenKey).Should().BeNull();
    }

    [AvaloniaFact]
    public async Task Confirm_still_cleans_when_telegram_rejects_the_tap_answer()
    {
        var (s, t) = Setup();
        await t.HandleAsync(Tap(Me, await AlertAsync(s, t, "a.dmp")));
        var yes = _api.Sent.Last().Buttons!.SelectMany(r => r).Single(x => x.Text == "Yes, clean").CallbackData;
        _api.FailAnswers = new TelegramApiException("query is too old", 400);
        await t.HandleAsync(Tap(Me, yes));
        _bin.Received.Should().ContainSingle();
        _api.Sent.Last().Text.Should().Contain("Moved 1 item");
    }

    [AvaloniaFact]
    public async Task A_failed_clean_is_reported_in_the_chat()
    {
        var (s, t) = Setup();
        await t.HandleAsync(Tap(Me, await AlertAsync(s, t, "a.dmp")));
        var yes = _api.Sent.Last().Buttons!.SelectMany(r => r).Single(x => x.Text == "Yes, clean").CallbackData;
        _bin.FailWith = new IOException("disk on fire");
        await t.HandleAsync(Tap(Me, yes));
        _api.Sent.Last().Text.Should().StartWith("Couldn't clean");
    }

    [AvaloniaFact]
    public async Task Unpairing_expires_old_buttons()
    {
        var (s, t) = Setup();
        var clean = await AlertAsync(s, t, "a.dmp");
        t.Unpair();
        await t.ConnectAsync("123:ABC");
        await t.HandleAsync(Msg(Me, "/pair " + t.StartPairing()));
        await t.HandleAsync(Tap(Me, clean));
        _api.Answered.Last().Should().Contain("expired");
    }

    [AvaloniaFact]
    public async Task Switching_bots_starts_polling_from_the_beginning()
    {
        var (_, t) = Setup();
        _api.Queue.Enqueue(new TelegramUpdate(900000, Me, "/help", null, null, 1));
        await t.PollOnceAsync(CancellationToken.None);
        t.Unpair();
        await t.ConnectAsync("456:DEF");
        await t.PollOnceAsync(CancellationToken.None);
        _api.LastOffset.Should().Be(0);
    }

    [AvaloniaFact]
    public async Task Pairing_closes_after_five_wrong_codes()
    {
        var (_, t) = Setup(paired: false);
        await t.ConnectAsync("123:ABC");
        var code = t.StartPairing();
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) await t.HandleAsync(Msg(Stranger, "/pair " + wrong));
        await t.HandleAsync(Msg(Me, "/pair " + code));
        t.IsPaired.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Groups_cannot_be_paired()
    {
        var (_, t) = Setup(paired: false);
        await t.ConnectAsync("123:ABC");
        var code = t.StartPairing();
        await t.HandleAsync(new TelegramUpdate(1, -100, "/pair " + code, null, null, 1, IsPrivate: false));
        t.IsPaired.Should().BeFalse();
        await t.HandleAsync(Msg(Me, "/pair " + code));
        t.IsPaired.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Show_list_fits_in_one_telegram_message()
    {
        var (s, t) = Setup();
        var watchId = s.Watches.Add(new Watch(0, "Crash dumps", _root, WatchMode.Suggest, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters()));
        await t.NotifyAsync(new MiuNotification("x", "y", [], watchId,
            Enumerable.Range(0, 30).Select(i => new CartItem(Path.Combine(_root, i + new string('n', 250)), i + new string('n', 250), 1, Verdict.Safe, "r")).ToList()));
        var list = _api.Sent.Last().Buttons!.SelectMany(r => r).Single(x => x.Text == "Show list").CallbackData;
        await t.HandleAsync(Tap(Me, list));
        _api.Sent.Last().Text.Length.Should().BeLessThanOrEqualTo(4096);
        _api.Sent.Last().Text.Should().Contain("more");
    }

    [AvaloniaFact]
    public async Task Polling_survives_unexpected_errors()
    {
        var (_, t) = Setup();
        _api.FailNext = new InvalidOperationException("weird JSON from a proxy");
        (await t.PollOnceAsync(CancellationToken.None)).Should().BeFalse();
        t.LastError.Should().Contain("weird JSON");
    }

    internal sealed class FakeTelegram : ITelegramApi
    {
        public List<(long Chat, string Text, IReadOnlyList<IReadOnlyList<TelegramButton>>? Buttons)> Sent { get; } = [];
        public List<string> Answered { get; } = [];
        public Queue<TelegramUpdate> Queue { get; } = new();
        public Exception? FailNext { get; set; }
        public Exception? FailSends { get; set; }
        public Exception? FailAnswers { get; set; }
        public long LastOffset { get; private set; } = -1;

        public Task<string> GetMeAsync(CancellationToken ct) => Task.FromResult("MiuTestBot");

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
        {
            LastOffset = offset;
            if (FailNext is { } e) { FailNext = null; throw e; }
            var list = Queue.ToList();
            Queue.Clear();
            return Task.FromResult<IReadOnlyList<TelegramUpdate>>(list);
        }

        public Task<long> SendMessageAsync(long chatId, string text, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons, CancellationToken ct)
        {
            if (FailSends is { } e) throw e;
            if (text.Length > 4096) throw new TelegramApiException("message is too long", 400);
            Sent.Add((chatId, text, buttons));
            return Task.FromResult((long)Sent.Count);
        }

        public Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct)
        {
            if (FailAnswers is { } e) throw e;
            Answered.Add(text ?? "");
            return Task.CompletedTask;
        }

        public Task EditMessageAsync(long chatId, long messageId, string text, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class MemorySecrets : Miu.Platform.Abstractions.ISecretStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string key) => _d.GetValueOrDefault(key);
        public void Set(string key, string value) => _d[key] = value;
        public void Delete(string key) => _d.Remove(key);
    }
}
