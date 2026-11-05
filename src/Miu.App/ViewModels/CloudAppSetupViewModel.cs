using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;

namespace Miu.App.ViewModels;

public sealed record SetupStep(int Number, string Text);

/// <summary>
/// One cloud provider in Settings › Cloud apps: you register your own free app with Google, Microsoft or Dropbox and
/// paste its keys here, so Miu never depends on someone else's app (or its limits) to keep working.
/// </summary>
public sealed partial class CloudAppSetupViewModel : ObservableObject
{
    private readonly CloudAccountsService _accounts;
    private readonly Action _changed;

    public CloudAppSetupViewModel(CloudAccountsService accounts, string provider, Action changed)
    {
        _accounts = accounts;
        _changed = changed;
        Provider = provider;
        var info = Guides[provider];
        Title = info.Title;
        ConsoleUrl = info.ConsoleUrl;
        ConsoleName = info.ConsoleName;
        RedirectUri = info.Redirect;
        IdLabel = info.IdLabel;
        IdWatermark = info.IdWatermark;
        NeedsSecret = provider == "gdrive";
        Steps = info.Steps.Select((t, i) => new SetupStep(i + 1, t)).ToList();
        clientId = provider switch
        {
            "gdrive" => accounts.UserClientIds.GoogleClientId ?? "",
            "onedrive" => accounts.UserClientIds.MicrosoftClientId ?? "",
            _ => accounts.UserClientIds.DropboxClientId ?? "",
        };
        UpdateStatus();
    }

    public string Provider { get; }
    public string Title { get; }
    public string ConsoleUrl { get; }
    public string ConsoleName { get; }
    public string RedirectUri { get; }
    public bool HasRedirect => RedirectUri.Length > 0;
    public string IdLabel { get; }
    public string IdWatermark { get; }
    public bool NeedsSecret { get; }
    public IReadOnlyList<SetupStep> Steps { get; }

    [ObservableProperty] private string clientId;
    [ObservableProperty] private string clientSecret = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool statusIsGood;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool messageIsError;

    private void UpdateStatus()
    {
        var src = _accounts.KeySource(Provider);
        StatusIsGood = src != "none";
        Status = src switch
        {
            "yours" => "Using your own app ✓",
            "build" => "Using the app that came with this build (you can add your own)",
            _ => "Not set up",
        };
    }

    [RelayCommand]
    private void Save()
    {
        var problem = _accounts.SaveUserApp(Provider, ClientId, NeedsSecret ? ClientSecret : null);
        MessageIsError = problem is not null;
        Message = problem ?? (string.IsNullOrWhiteSpace(ClientId) ? "Removed." : "Saved. Go to Cloud & Network › Cloud backup and connect your account.");
        ClientSecret = ""; // never kept on screen
        UpdateStatus();
        _changed();
    }

    [RelayCommand]
    private void Remove()
    {
        ClientId = "";
        Save();
    }

    [RelayCommand]
    private void OpenConsole() => ShellIntegration.Open(ConsoleUrl);

    private sealed record Guide(string Title, string ConsoleName, string ConsoleUrl, string Redirect, string IdLabel, string IdWatermark, string[] Steps);

    private static readonly Dictionary<string, Guide> Guides = new()
    {
        ["gdrive"] = new("Google Drive", "Google Cloud Console", "https://console.cloud.google.com/apis/credentials", "",
            "Client ID", "1234567890-abc.apps.googleusercontent.com",
        [
            "Open the Google Cloud Console and sign in with your Google account. Create a project (project picker at the top › New project), for example “Miu backup”. It's free.",
            "Go to APIs & Services › Library, search for “Google Drive API” and press Enable.",
            "Go to Google Auth Platform (APIs & Services › OAuth consent screen). Choose External, name the app (for example “Miu for me”) and enter your email address.",
            "Under Data access › Add or remove scopes, add “…/auth/drive.file”. That only lets Miu see files it creates itself.",
            "Under Audience, press “Publish app”. Otherwise Google signs you out every 7 days. For your own use with this scope, Google doesn't need to review anything.",
            "Under Clients › Create client, choose Application type “Desktop app” and press Create. Copy the Client ID and the Client secret into the boxes below.",
            "When you connect, Google may say it hasn't verified the app. It's your own app: choose Advanced › Go to … to continue.",
        ]),
        ["onedrive"] = new("OneDrive", "Microsoft Entra admin center", "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade", "http://localhost",
            "Application (client) ID", "1a2b3c4d-1234-5678-9abc-def012345678",
        [
            "Open the Microsoft Entra admin center (portal.azure.com works too) and sign in. A personal Microsoft account works; if it asks, create the free account it offers.",
            "Go to App registrations › New registration. Name it (for example “Miu for me”). Under Supported account types, choose “Accounts in any organizational directory and personal Microsoft accounts”.",
            "Under Redirect URI, choose “Public client/native (mobile & desktop)” and enter the address shown below. Press Register.",
            "Go to Authentication › Advanced settings, set “Allow public client flows” to Yes, and Save.",
            "Go to API permissions › Add a permission › Microsoft Graph › Delegated permissions. Add Files.ReadWrite.AppFolder, offline_access and User.Read.",
            "Copy the “Application (client) ID” from the Overview page into the box below. No secret is needed.",
        ]),
        ["dropbox"] = new("Dropbox", "Dropbox App Console", "https://www.dropbox.com/developers/apps", "http://localhost:53682/",
            "App key", "abc123xyz456def",
        [
            "Open the Dropbox App Console, sign in and press “Create app”.",
            "Choose “Scoped access” and “App folder”, so Miu only sees its own folder. Name it; the name must be unique (for example “Miu backup yourname”).",
            "On the Permissions tab, tick files.metadata.read, files.metadata.write, files.content.read and files.content.write, then press Submit.",
            "On the Settings tab › OAuth 2 › Redirect URIs, add the address shown below. Set “Allow public clients (Implicit Grant & PKCE)” to Allow.",
            "Copy the App key (not the App secret) into the box below.",
            "Apps in Development status can be linked by up to 500 people, which is plenty for you and your family.",
        ]),
    };
}
