using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Miu.App.Services;

namespace Miu.App.ViewModels;

public sealed record NavItem(string Title, string Icon, object Page);

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(AppServices services)
    {
        Services = services;
        Home = new HomeViewModel(this);
        Analyze = new AnalyzeViewModel(services, this);
        DevProjects = new DevProjectsViewModel(this);
        Rules = new PlaceholderViewModel("Rules", "Your rules and the ones Miu learns from your choices will live here. Coming in the next milestone.");
        Settings = new PlaceholderViewModel("Settings", "Background mode, automation, Telegram and theme settings are coming soon.");
        NavItems =
        [
            new("Home", "M3,11 L12,4 L21,11 L21,20 L3,20 Z", Home),
            new("Analyze", "M3,3 L13,3 L13,21 L3,21 Z M13,3 L21,3 L21,11 L13,11 Z M13,11 L21,11 L21,21 L13,21 Z", Analyze),
            new("Dev Projects", "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,4 L10,20", DevProjects),
            new("Rules", "M12,3 L20,7 L20,12 C20,17 16.5,20 12,21 C7.5,20 4,17 4,12 L4,7 Z", Rules),
            new("Settings", "M12,9 A3,3 0 1 1 11.99,9 Z M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M5,19 L7,17 M17,7 L19,5", Settings),
        ];
        selectedNav = NavItems[0];
        currentPage = Home;
    }

    public AppServices Services { get; }
    public HomeViewModel Home { get; }
    public AnalyzeViewModel Analyze { get; }
    public DevProjectsViewModel DevProjects { get; }
    public PlaceholderViewModel Rules { get; }
    public PlaceholderViewModel Settings { get; }
    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem selectedNav;
    [ObservableProperty] private object currentPage;

    partial void OnSelectedNavChanged(NavItem value) => CurrentPage = value.Page;

    public void Navigate(object page) => SelectedNav = NavItems.First(n => n.Page == page);

    public void OnScanCompleted(ScanSession session)
    {
        Home.Update(session);
        DevProjects.Update(session);
    }
}

public sealed record PlaceholderViewModel(string Title, string Message);
