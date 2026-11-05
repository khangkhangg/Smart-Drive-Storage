using Miu.App.Services;

namespace Miu.App.ViewModels;

public sealed record LinkItem(string Title, string Url);

public sealed class AboutViewModel
{
    public string Name => AppInfo.Name;
    public string VersionText => $"Version {AppInfo.Version}";
    public string MadeBy => $"Made with love by {AppInfo.Author}";
    public string Tagline => "See where your space went, know what's safe to delete, and keep your drives clean — especially if you code.";
    public IReadOnlyList<LinkItem> Links { get; } = AppInfo.Links.Select(l => new LinkItem(l.Title, l.Url)).ToList();
}
