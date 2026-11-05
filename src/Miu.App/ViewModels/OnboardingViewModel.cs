using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Data;

namespace Miu.App.ViewModels;

public sealed partial class OnboardingLocation(string title, string detail, string path, bool isChecked) : ObservableObject
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Path { get; } = path;
    [ObservableProperty] private bool isChecked = isChecked;
}

/// <summary>First run, 3 steps: what to look at, background mode, optional extras. Ends with the first scan.</summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;

    public OnboardingViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        var home = shell.Home.HomeFolder;
        Locations.Add(new OnboardingLocation("Your home folder", "Documents, Downloads, Pictures, and everything else that's yours", home, true));
        foreach (var code in new[] { "code", "Code", "projects", "Projects", "dev", "src", "repos", "source/repos" }.Select(c => System.IO.Path.Combine(home, c)))
            if (Directory.Exists(code)) { Locations.Add(new OnboardingLocation("Your code", "Projects, node_modules, build output, caches", code, false)); break; }
        foreach (var d in shell.Home.Drives.Where(d => d.Root != System.IO.Path.GetPathRoot(home) || OperatingSystem.IsWindows()))
            Locations.Add(new OnboardingLocation(d.Title + " " + d.Badge, d.Subtitle + " · " + d.FreeText, d.Root, false));
    }

    public ObservableCollection<OnboardingLocation> Locations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(NextText), nameof(StepText), nameof(CanGoBack))]
    private int step = 1;

    [ObservableProperty] private bool runInBackground;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool CanGoBack => Step > 1;
    public string StepText => $"Step {Step} of 3";
    public string NextText => Step == 3 ? "Start my first scan" : "Next";
    public string BinName => _shell.Services.RecycleBinName;

    [RelayCommand]
    private async Task Next()
    {
        if (Step < 3) { Step++; return; }
        await FinishAsync(scan: true);
    }

    [RelayCommand]
    private void Back() { if (Step > 1) Step--; }

    [RelayCommand]
    private Task Skip() => FinishAsync(scan: false);

    [RelayCommand]
    private async Task SetUpTelegram()
    {
        await FinishAsync(scan: false);
        _shell.Navigate(_shell.Settings);
    }

    [RelayCommand]
    private async Task SetUpCloud()
    {
        await FinishAsync(scan: false);
        _shell.Navigate(_shell.CloudNetwork);
    }

    private async Task FinishAsync(bool scan)
    {
        _shell.Services.Settings.SetBool(SettingKeys.OnboardingDone, true);
        if (RunInBackground) _shell.Settings.RunInBackground = true; // also registers start at sign-in
        _shell.Onboarding = null;
        var first = Locations.FirstOrDefault(l => l.IsChecked);
        if (!scan || first is null) return;
        _shell.Navigate(_shell.Analyze);
        await _shell.Analyze.ScanPathAsync(first.Path);
    }
}
