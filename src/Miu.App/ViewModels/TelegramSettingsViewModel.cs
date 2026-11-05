using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;

namespace Miu.App.ViewModels;

/// <summary>Settings › Telegram: connect your bot, pair this chat, test, choose alerts, unpair.</summary>
public partial class TelegramSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public TelegramSettingsViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        sendAlerts = shell.Services.Settings.GetBool(TelegramNotifier.AlertsKey, true);
        _pairingTimer.Tick += (_, _) => Refresh();
        shell.Telegram.PairingChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private TelegramService Telegram => _shell.Telegram;

    [ObservableProperty] private string token = "";
    [ObservableProperty] private string status = "Not connected.";
    [ObservableProperty] private string pairingCode = "";
    [ObservableProperty] private string instruction = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNotPaired))] private bool isPaired;
    [ObservableProperty] private bool sendAlerts;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))] private bool isBusy;

    public bool IsNotPaired => !IsPaired;
    public bool IsPairing => PairingCode.Length > 0 && !IsPaired;

    partial void OnPairingCodeChanged(string value) => OnPropertyChanged(nameof(IsPairing));
    partial void OnSendAlertsChanged(bool value) => _shell.Services.Settings.SetBool(TelegramNotifier.AlertsKey, value);

    public void Refresh()
    {
        IsPaired = Telegram.IsPaired;
        if (IsPaired)
        {
            _pairingTimer.Stop();
            PairingCode = "";
            Instruction = "";
            Status = Telegram.LastError is { } err
                ? "Paired, but Miu can't reach Telegram right now: " + err
                : "Paired ✅ Alerts and commands work in your Telegram chat.";
        }
        else if (PairingCode.Length > 0 && !Telegram.IsPairingOpen)
        {
            // Expired, too many wrong codes, or unpaired from Telegram.
            _pairingTimer.Stop();
            PairingCode = "";
            Instruction = "";
            Status = Telegram.IsConnected ? "Pairing closed. Press Connect to get a new code." : "Not connected.";
        }
        else if (PairingCode.Length == 0) Status = Telegram.IsConnected ? Status : "Not connected.";
        OnPropertyChanged(nameof(IsPairing));
    }

    private bool CanConnect() => !IsBusy;

    /// <summary>Checks the token with Telegram, stores it in the secret store, and starts pairing.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        if (string.IsNullOrWhiteSpace(Token)) { Status = "Paste the token @BotFather gave you."; return; }
        IsBusy = true;
        try
        {
            await Telegram.ConnectAsync(Token);
            Token = ""; // the token now lives only in the secret store
            PairingCode = Telegram.StartPairing();
            Status = $"Connected to @{Telegram.BotUsername}.";
            Instruction = $"Open Telegram, find @{Telegram.BotUsername} and send:  /pair {PairingCode}   (valid for 10 minutes)";
            _pairingTimer.Start();
        }
        catch (Exception ex) when (ex is Miu.Notify.TelegramApiException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't connect: " + ex.Message;
            PairingCode = "";
            Instruction = "";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SendTest()
    {
        try
        {
            Status = await Telegram.SendTestAsync() ? "Test message sent." : "Not paired. Connect and pair first.";
        }
        catch (Exception ex) when (ex is Miu.Notify.TelegramApiException or HttpRequestException or TaskCanceledException)
        {
            Status = "Couldn't send: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Unpair()
    {
        Telegram.Unpair();
        _pairingTimer.Stop();
        PairingCode = "";
        Instruction = "";
        Status = "Not connected.";
        Refresh();
    }
}
