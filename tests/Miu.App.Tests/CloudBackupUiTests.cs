using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Cloud;
using Miu.Core.Cleanup;
using Miu.Core.Rules;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class CloudBackupUiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-cbui-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCloudStorage _cloud = new();

    public CloudBackupUiTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private MainWindowViewModel Shell(bool withAccount)
    {
        var s = AppServices.Create(":memory:", new MovingBin(Path.Combine(_root, ".bin")), secrets: new TelegramServiceTests.MemorySecrets(), networkDrives: new UnsupportedNetworkDrives("n/a"));
        s.CloudStorageOverride = _ => _cloud;
        if (withAccount) s.CloudAccounts.Add("s3", "Backblaze B2 · photos");
        var vm = new MainWindowViewModel(s, new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram());
        vm.Automation.Backup.RetryDelay = _ => TimeSpan.Zero;
        return vm;
    }

    [AvaloniaFact]
    public async Task Accounts_card_explains_what_is_available_and_checks_the_s3_form()
    {
        var b = Shell(withAccount: false).CloudNetwork.Backup;
        b.HasAccounts.Should().BeFalse();
        b.Providers.Select(p => p.Name).Should().Equal("Google Drive", "OneDrive", "Dropbox");
        var unavailable = b.Providers.First(p => !p.Available);
        await b.ConnectOAuthCommand.ExecuteAsync(unavailable);
        b.AccountMessageIsError.Should().BeTrue();
        b.AccountMessage.Should().Contain("Not available in this build");

        await b.ConnectS3Command.ExecuteAsync(null);
        b.AccountMessage.Should().Contain("Fill in");
        b.S3Endpoint = "http://insecure.example";
        b.S3Bucket = "x"; b.S3AccessKey = "k"; b.S3SecretKey = "s";
        await b.ConnectS3Command.ExecuteAsync(null);
        b.AccountMessage.Should().Contain("https://");
    }

    [AvaloniaFact]
    public async Task Plan_save_preview_run_and_restore()
    {
        var photos = Path.Combine(_root, "Photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "a.jpg"), "photo a");
        File.WriteAllText(Path.Combine(photos, "b.jpg"), "photo b");
        var vm = Shell(withAccount: true);
        var b = vm.CloudNetwork.Backup;
        b.HasAccounts.Should().BeTrue();
        b.SelectedAccount.Should().NotBeNull();

        b.FoldersText = "";
        await b.SavePlanCommand.ExecuteAsync(null);
        b.PlanMessageIsError.Should().BeTrue();

        b.FoldersText = photos;
        b.Frequency = "Only when I press Run";
        await b.SavePlanCommand.ExecuteAsync(null);
        b.Plans.Should().ContainSingle();
        b.HasPreview.Should().BeTrue();
        b.PreviewText.Should().Contain("Upload 2 files");
        await b.RunPreviewCommand.ExecuteAsync(null);
        b.PlanMessage.Should().Contain("Uploaded 2 files");
        _cloud.Files.Keys.Should().BeEquivalentTo("Miu Backup/Photos/a.jpg", "Miu Backup/Photos/b.jpg");
        b.Plans.Single().LastText.Should().Contain("Uploaded 2 files");

        b.Plans.Single().RestoreCommand.Execute(null);
        b.HasRestore.Should().BeTrue();
        b.RestoreRows.Should().HaveCount(2);
        b.RestoreFilter = "b.jpg";
        b.RestoreRows.Should().ContainSingle();
        b.SelectAllRestoreCommand.Execute(null);
        b.RestoreToOriginal = false;
        b.RestoreFolder = Path.Combine(_root, "Restored");
        await b.RunRestoreCommand.ExecuteAsync(null);
        b.RestoreMessage.Should().Contain("Restored 1 file");
        File.ReadAllText(Path.Combine(_root, "Restored", "Photos", "b.jpg")).Should().Be("photo b");

        b.AccountRows.Single().DisconnectCommand.Execute(null);
        b.AccountMessage.Should().Contain("Delete the backup plans");
    }

    [AvaloniaFact]
    public async Task Review_and_clean_can_back_up_first_and_cleans_nothing_if_that_fails()
    {
        var file = Path.Combine(_root, "old-installer.exe");
        File.WriteAllText(file, "installer bytes");
        var vm = Shell(withAccount: true);
        vm.OpenReview([new CartItem(file, "old-installer.exe", 15, Verdict.Safe, "Old installer")], "Review", "cart");
        var review = vm.Review!;
        review.CanBackUpFirst.Should().BeTrue();
        review.BackUpFirst = true;

        _cloud.Corrupt = _ => true;
        await review.CleanCommand.ExecuteAsync(null);
        File.Exists(file).Should().BeTrue();
        review.BackupStatus.Should().Contain("nothing was cleaned");

        _cloud.Corrupt = _ => false;
        await review.CleanCommand.ExecuteAsync(null);
        File.Exists(file).Should().BeFalse();
        _cloud.Files.Keys.Should().Contain(k => k.StartsWith("Miu Backup/Before cleaning/") && k.EndsWith("/old-installer.exe"));
    }
}
