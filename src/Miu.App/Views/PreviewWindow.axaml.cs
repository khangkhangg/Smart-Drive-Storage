using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Miu.App.ViewModels;

namespace Miu.App.Views;

/// <summary>Quick preview: a picture, the first lines of a text file, or details. Nothing is opened in another app.</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow() => InitializeComponent();

    public PreviewWindow(PreviewContent content) : this()
    {
        DataContext = content;
        if (content.IsImage)
        {
            try
            {
                using var f = File.OpenRead(content.FullPath);
                Picture.Source = Bitmap.DecodeToWidth(f, 1600);
            }
            catch (Exception) // a broken or unsupported picture: show its details instead
            {
                DataContext = content with { Kind = PreviewKind.Details, Facts = [.. content.Facts, "Miu couldn't read this picture."] };
            }
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
