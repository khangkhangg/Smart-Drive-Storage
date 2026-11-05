using Avalonia.Data.Converters;
using Miu.Core.Automation;

namespace Miu.App.ViewModels;

public static class WatchConverters
{
    public static readonly IValueConverter Schedule =
        new FuncValueConverter<WatchSchedule?, string>(s => s is null ? "" : char.ToUpperInvariant(s.Describe()[0]) + s.Describe()[1..]);
}
