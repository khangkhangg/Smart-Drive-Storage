using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;

namespace Miu.App.Tests;

/// <summary>
/// Renders the main screens with a realistic demo folder (sparse files, so nothing big is written).
/// Runs only when MIU_SCREENSHOT_DIR is set: MIU_SCREENSHOT_DIR=docs/screenshots dotnet test --filter Screenshot
/// </summary>
public class ScreenshotTests
{
    [AvaloniaFact]
    public async Task Screenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("MIU_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(outDir)) return;
        Directory.CreateDirectory(outDir);
        var root = DemoData.Create();
        try
        {
            var vm = new MainWindowViewModel(AppServices.Create(":memory:"));
            var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
            window.Show();
            await vm.Analyze.ScanPathAsync(root);

            vm.Navigate(vm.Home);
            Save(window, Path.Combine(outDir, "home.png"));

            vm.Navigate(vm.Analyze);
            vm.Analyze.OpenCommand.Execute(vm.Analyze.Items.Single(i => i.Name == "Code"));
            vm.Analyze.Selected = vm.Analyze.Items.First(i => i.Name == "shop-app");
            Save(window, Path.Combine(outDir, "analyze.png"));

            vm.Analyze.OpenCommand.Execute(vm.Analyze.Selected);
            vm.Analyze.Selected = vm.Analyze.Items.First(i => i.Name == "node_modules");
            Save(window, Path.Combine(outDir, "analyze-project.png"));

            vm.Navigate(vm.DevProjects);
            Save(window, Path.Combine(outDir, "dev-projects.png"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }

    private static void Save(MainWindow window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        frame.Save(path);
    }
}

internal static class DemoData
{
    private const long MB = 1L << 20, GB = 1L << 30;

    public static string Create()
    {
        // Outside the system temp folder, so the temp-file rule doesn't apply to the whole demo.
        var root = Path.Combine(AppContext.BaseDirectory, "miu-demo-" + Guid.NewGuid().ToString("N")[..6], "Alex");
        F(root, "Videos/trip-japan-2024.mp4", 18 * GB, 300);
        F(root, "Videos/wedding-4k.mov", 12 * GB, 500);
        F(root, "Videos/screen-recordings/demo.mp4", 3 * GB, 40);

        Node(root, "Code/shop-app", 150, 4_300 * MB, 600 * MB);
        Node(root, "Code/portfolio-2023", 420, 1_100 * MB, 80 * MB);
        Node(root, "Code/landing-page", 2, 900 * MB, 0);
        F(root, "Code/ai-chatbot/pyproject.toml", 2_000, 5);
        F(root, "Code/ai-chatbot/app/main.py", 40_000, 5);
        F(root, "Code/ai-chatbot/.venv/pyvenv.cfg", 200, 30);
        F(root, "Code/ai-chatbot/.venv/lib/torch.so", 2_300 * MB, 30);
        F(root, "Code/ai-chatbot/app/__pycache__/main.pyc", 30 * MB, 5);
        F(root, "Code/rust-cli/Cargo.toml", 800, 60);
        F(root, "Code/rust-cli/src/main.rs", 20_000, 60);
        F(root, "Code/rust-cli/target/debug/rust-cli", 3_100 * MB, 60);
        F(root, "Code/api-server/Api.csproj", 1_500, 200);
        F(root, "Code/api-server/Program.cs", 9_000, 200);
        F(root, "Code/api-server/bin/Debug/Api.dll", 400 * MB, 200);
        F(root, "Code/api-server/obj/project.assets.json", 200 * MB, 200);

        F(root, "Downloads/setup-photoeditor.exe", 320 * MB, 120);
        F(root, "Downloads/ubuntu-24.04.iso", 5 * GB, 200);
        F(root, "Downloads/invoice.pdf", 2 * MB, 10);
        F(root, ".cache/huggingface/hub/llama-3-8b.safetensors", 9 * GB, 90);
        F(root, ".npm/_cacache/content-v2/blob", 1_800 * MB, 20);
        F(root, "Documents/Taxes/2025.pdf", 40 * MB, 100);
        F(root, "Documents/Photos/library.lrcat", 2 * GB, 3);
        F(root, "AppData/Local/Temp/installer-logs.tmp", 1_200 * MB, 15);
        F(root, "AppData/Local/CrashDumps/game.dmp", 900 * MB, 30);
        return root;
    }

    private static void Node(string root, string project, int ageDays, long nodeModules, long build)
    {
        F(root, project + "/package.json", 1_200, ageDays);
        F(root, project + "/src/index.tsx", 30_000, ageDays);
        F(root, project + "/node_modules/.pnpm/react/index.js", nodeModules, Math.Max(ageDays, 1));
        if (build > 0) F(root, project + "/.next/cache/webpack.pack", build, ageDays);
    }

    private static void F(string root, string rel, long size, int ageDays)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(size); // sparse on most file systems
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
    }
}
