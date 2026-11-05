using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;

namespace Miu.App.Tests;

public class TelegramUiTests
{
    private static (MainWindowViewModel Vm, TelegramServiceTests.FakeTelegram Api) Shell()
    {
        var api = new TelegramServiceTests.FakeTelegram();
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        return (new MainWindowViewModel(s, new AutomationTests.FakeNotifier(), _ => api), api);
    }

    [AvaloniaFact]
    public async Task Connect_pair_test_and_unpair_from_settings()
    {
        var (vm, api) = Shell();
        var tg = vm.Settings.Telegram;
        tg.IsPaired.Should().BeFalse();
        tg.Token = "123:ABC";
        await tg.ConnectCommand.ExecuteAsync(null);
        tg.Status.Should().Contain("@MiuTestBot");
        tg.PairingCode.Should().MatchRegex(@"^\d{6}$");
        tg.Instruction.Should().Contain("/pair " + tg.PairingCode);

        await vm.Telegram.HandleAsync(new Miu.Notify.TelegramUpdate(1, 42, "/pair " + tg.PairingCode, null, null, 1));
        tg.Refresh();
        tg.IsPaired.Should().BeTrue();
        tg.Token.Should().BeEmpty(); // never kept in the view

        await tg.SendTestCommand.ExecuteAsync(null);
        api.Sent.Last().Text.Should().Contain("Hello from Miu");

        tg.UnpairCommand.Execute(null);
        tg.IsPaired.Should().BeFalse();
        vm.Services.Secrets.Get(TelegramService.TokenKey).Should().BeNull();
    }

    [AvaloniaFact]
    public async Task Notifications_go_to_telegram_when_paired_and_enabled()
    {
        var (vm, api) = Shell();
        await vm.Telegram.ConnectAsync("t");
        var code = vm.Telegram.StartPairing();
        await vm.Telegram.HandleAsync(new Miu.Notify.TelegramUpdate(1, 42, "/pair " + code, null, null, 1));
        api.Sent.Clear();
        vm.Notifier.Show(new MiuNotification("Downloads tidy", "3 items can be cleaned.", []));
        await Task.Delay(100);
        api.Sent.Should().ContainSingle(m => m.Text.Contains("Downloads tidy"));
        vm.Settings.Telegram.SendAlerts = false;
        vm.Notifier.Show(new MiuNotification("Again", "x", []));
        await Task.Delay(100);
        api.Sent.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task Bad_token_shows_an_error()
    {
        var api = new BadApi();
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        var vm = new MainWindowViewModel(s, new AutomationTests.FakeNotifier(), _ => api);
        vm.Settings.Telegram.Token = "nope";
        await vm.Settings.Telegram.ConnectCommand.ExecuteAsync(null);
        vm.Settings.Telegram.Status.Should().Contain("Unauthorized");
        vm.Settings.Telegram.PairingCode.Should().BeEmpty();
    }

    private sealed class BadApi : Miu.Notify.ITelegramApi
    {
        public Task<string> GetMeAsync(CancellationToken ct) => throw new Miu.Notify.TelegramApiException("Telegram: Unauthorized", 401);
        public Task<IReadOnlyList<Miu.Notify.TelegramUpdate>> GetUpdatesAsync(long o, int t, CancellationToken ct) => Task.FromResult<IReadOnlyList<Miu.Notify.TelegramUpdate>>([]);
        public Task<long> SendMessageAsync(long c, string t, IReadOnlyList<IReadOnlyList<Miu.Notify.TelegramButton>>? b, CancellationToken ct) => Task.FromResult(0L);
        public Task AnswerCallbackAsync(string id, string? t, CancellationToken ct) => Task.CompletedTask;
        public Task EditMessageAsync(long c, long m, string t, CancellationToken ct) => Task.CompletedTask;
    }
}
