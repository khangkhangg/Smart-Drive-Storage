using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Miu.App.ViewModels;
using Miu.Core.Layout;

namespace Miu.App.Controls;

/// <summary>Squarified treemap of the current folder. Click selects, double-click opens a folder.</summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TreemapControl, IEnumerable?>(nameof(Items));
    public static readonly StyledProperty<NodeItemViewModel?> SelectedItemProperty =
        AvaloniaProperty.Register<TreemapControl, NodeItemViewModel?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<TreemapControl, ICommand?>(nameof(OpenCommand));

    private const int MaxTiles = 200;
    private const double Gap = 2;
    private readonly List<(NodeItemViewModel Item, Rect Rect)> _tiles = [];
    private INotifyCollectionChanged? _observed;

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(SelectedItemProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TreemapControl>(true);
    }

    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public NodeItemViewModel? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsProperty) return;
        if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = change.NewValue as INotifyCollectionChanged;
        if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
        InvalidateVisual();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _tiles.Clear();
        var items = Items?.OfType<NodeItemViewModel>().Where(i => i.Size > 0).Take(MaxTiles).ToList() ?? [];
        var bounds = new Rect(Bounds.Size);
        if (items.Count == 0 || bounds.Width < 10 || bounds.Height < 10) return;

        var rects = Squarify.Layout(items.Select(i => (double)i.Size).ToList(), new LayoutRect(0, 0, bounds.Width, bounds.Height));
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var regular = new Typeface(FontFamily.Default);
        for (var i = 0; i < items.Count; i++)
        {
            var r = rects[i];
            var rect = new Rect(r.X, r.Y, r.Width, r.Height).Deflate(Gap / 2);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var item = items[i];
            _tiles.Add((item, rect));
            context.DrawRectangle(item.TileFill, null, rect, 4, 4);
            if (ReferenceEquals(item, SelectedItem))
                context.DrawRectangle(null, new Pen(Brushes.Black, 3), rect.Deflate(1.5), 4, 4);

            if (rect.Width < 56 || rect.Height < 34) continue;
            using (context.PushClip(rect.Deflate(6)))
            {
                var name = new FormattedText(item.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, item.TileText)
                    { MaxTextWidth = rect.Width - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                var size = new FormattedText(item.SizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, regular, 12, item.TileText);
                context.DrawText(name, new Point(rect.X + 8, rect.Bottom - 8 - size.Height - name.Height));
                context.DrawText(size, new Point(rect.X + 8, rect.Bottom - 8 - size.Height));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var hit = _tiles.FirstOrDefault(t => t.Rect.Contains(p)).Item;
        if (hit is null) return;
        SelectedItem = hit;
        if (e.ClickCount == 2 && OpenCommand?.CanExecute(hit) == true) OpenCommand.Execute(hit);
        e.Handled = true;
    }
}
