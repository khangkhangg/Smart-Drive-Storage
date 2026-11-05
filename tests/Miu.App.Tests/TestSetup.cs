using System.Runtime.CompilerServices;

namespace Miu.App.Tests;

internal static class TestSetup
{
    /// <summary>Tests never search the real network just because a page opened.</summary>
    [ModuleInitializer]
    internal static void Init() => Miu.App.ViewModels.CloudNetworkViewModel.AutoFindDevices = false;
}
