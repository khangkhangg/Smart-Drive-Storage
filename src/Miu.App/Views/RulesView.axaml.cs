using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Miu.App.ViewModels;

namespace Miu.App.Views;

public partial class RulesView : UserControl
{
    public RulesView() => InitializeComponent();

    private RulesViewModel? Vm => DataContext as RulesViewModel;

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Miu rules", SuggestedFileName = "miu-rules.json", DefaultExtension = "json",
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(Vm.ExportJson());
        Vm.Message = "Rules exported.";
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Miu rules", AllowMultiple = false });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        Vm.Import(await reader.ReadToEndAsync());
    }
}
