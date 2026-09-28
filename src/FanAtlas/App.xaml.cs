using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FanAtlas;
public partial class App : Application
{
    private Mutex? mutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        if (e.Args.Length >= 3 && e.Args[0] == "--test-host")
        {
            StateStore.DirectoryPath = Path.GetFullPath(e.Args[2]);
            var test = new AppState { Profile = ProfileReader.Read(e.Args[1]), Bridge = new() { Port = 17655 } };
            var host = new MainWindow(test); MainWindow = host; host.Show(); return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--self-test")
        { int result = SelfTests.Run(e.Args[1], e.Args[2]); Shutdown(result); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--render")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StateStore.DirectoryPath = Path.Combine(e.Args[2], "test-data");
            Directory.CreateDirectory(e.Args[2]);
            var testState = new AppState { Profile = ProfileReader.Read(e.Args[1]) };
            var w = new MainWindow(testState, true);
            await w.PollOnce();
            var root = (FrameworkElement)w.Content;
            foreach (int page in new[] { 0, 1, 2, 3, 4 })
            {
                w.Pages.SelectedIndex = page;
                root.Measure(new Size(1264, 825)); root.Arrange(new Rect(0, 0, 1264, 825)); root.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap(1264, 825, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(e.Args[2], $"ansicht-{page}.png")); encoder.Save(output);
            }
            var report = SelfTests.Ui(w);
            File.WriteAllText(Path.Combine(e.Args[2], "ui-tests.txt"), report);
            Shutdown(0); return;
        }
        StateStore.DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrazyBatto", "FanAtlas");
        mutex = new Mutex(true, @"Local\CrazyBatto-FanAtlas-v2", out bool acquired);
        if (!acquired) { MessageBox.Show("FanAtlas läuft bereits aus diesem Ordner.", "FanAtlas"); Shutdown(); return; }
        var state = StateStore.Load();
        if (StateStore.LoadWarning != null)
        {
            MessageBox.Show(StateStore.LoadWarning + "\nNeue Daten werden in einem separaten Wiederherstellungsordner gespeichert.", "FanAtlas");
            StateStore.DirectoryPath = Path.Combine(StateStore.DirectoryPath, "Wiederherstellung-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }
        if (state.Profile.Fans.Count == 0 && state.Profile.Curves.Count == 0)
        {
            string initial = e.Args.FirstOrDefault(a => a.EndsWith(".cueprofile", StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Test", "Standard Profil.cueprofile");
            if (File.Exists(initial)) { try { state.Profile = ProfileReader.Read(initial); } catch { } }
        }
        var window = new MainWindow(state); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}

