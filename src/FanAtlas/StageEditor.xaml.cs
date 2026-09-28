using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FanAtlas;
public partial class StageEditor : UserControl
{
    private MainWindow? owner;
    private FanTile? selected;
    private bool loading, dragging;
    private Point grab;
    private string sensorSignature = "";
    private readonly Dictionary<string, (Grid Root, TextBlock Temp, TextBlock Rpm, TextBlock State)> visuals = new();
    private readonly HashSet<string> autoSeen = new();
    private readonly List<BackgroundChoice> backgrounds = new() { new("Stream startet", "stream-startet.jpg"), new("Bin gleich zurück", "bin-gleich-zurueck.jpg"), new("Transparent für OBS", "") };
    private record BackgroundChoice(string Name, string File);
    private record SensorChoice(string Name, string Key);
    public StageEditor() { InitializeComponent(); }
    public void Attach(MainWindow window)
    {
        owner = window; Settings.Normalize(); loading = true;
        BackgroundSelect.ItemsSource = backgrounds;
        BackgroundSelect.SelectedItem = backgrounds.FirstOrDefault(x => x.File == Settings.Background) ?? backgrounds[0];
        ProfileSelect.ItemsSource = owner.State.Profile.Fans;
        foreach (var tile in Settings.Tiles) { tile.Clamp(); if (tile.RpmSensorKey.Length > 0) autoSeen.Add(tile.RpmSensorKey); }
        loading = false; RefreshTileList(); BuildTiles(); Refresh();
    }
    private StageSettings Settings => owner!.State.Stage;
    public void Refresh()
    {
        if (owner == null) return;
        bool added = false;
        foreach (var sensor in owner.Sensors.Where(s => s.Fresh && (s.Unit == "RPM" || s.Unit == "%" && s.Name.Contains("Lüfter"))))
        {
            if (autoSeen.Contains(sensor.Key) || Settings.HiddenAutoSensors.Contains(sensor.Key) || Settings.Tiles.Count >= 32) continue;
            autoSeen.Add(sensor.Key);
            if (Settings.Tiles.Any(t => t.RpmSensorKey == sensor.Key)) continue;
            var temperature = sensor.Key.StartsWith("nvml/") ? owner.Sensors.FirstOrDefault(s => s.Device == sensor.Device && s.Unit == "°C") : null;
            int n = Settings.Tiles.Count;
            Settings.Tiles.Add(new() { Name = sensor.Name.Replace(" (Treiberwert)", ""), RpmSensorKey = sensor.Key, TemperatureSensorKey = temperature?.Key ?? "", X = 650 + n % 4 * 250, Y = 170 + n / 4 * 300 });
            Settings.Tiles[^1].Clamp(); added = true;
        }
        if (added) { RefreshTileList(); BuildTiles(); owner.StageChanged(); }
        string signature = string.Join("|", owner.Sensors.Select(s => s.Key));
        if (signature != sensorSignature) { sensorSignature = signature; UpdateSelectors(); }
        foreach (var tile in Settings.Tiles)
        {
            if (!visuals.TryGetValue(tile.Id, out var visual)) continue;
            var t = owner.Sensors.FirstOrDefault(s => s.Key == tile.TemperatureSensorKey);
            var rpm = owner.Sensors.FirstOrDefault(s => s.Key == tile.RpmSensorKey);
            visual.Temp.Text = t?.Fresh == true ? $"{t.NumericValue:0.#}°" : "—";
            visual.Temp.ToolTip = t?.ToString() ?? "Kein Temperatursensor zugeordnet";
            visual.Rpm.Text = rpm?.Fresh == true ? rpm.ValueText : "—";
            visual.State.Text = rpm?.Fresh == true ? "Live" : tile.ProfileKey.Length > 0 && tile.RpmSensorKey.Length == 0 ? "Profil · nicht erkannt" : "Quelle fehlt / veraltet";
            visual.State.Foreground = rpm?.Fresh == true ? Brushes.LightGreen : Brushes.Goldenrod;
        }
        SceneSummary.Text = $"{Settings.Tiles.Count} Lüfterbilder · {Settings.Tiles.Count(t => t.Visible)} im Stream · 1920 × 1080";
        BridgeStatus.Text = owner.BridgeStatusText + " · OBS: Browserquelle, 1920 × 1080. Bei geschlossener App werden keine aktuellen Werte angezeigt.";
    }
    private void RefreshTileList()
    {
        if (owner == null) return;
        loading = true; string? id = selected?.Id;
        TileSelect.ItemsSource = null; TileSelect.ItemsSource = Settings.Tiles;
        TileSelect.SelectedItem = Settings.Tiles.FirstOrDefault(t => t.Id == id) ?? Settings.Tiles.FirstOrDefault();
        selected = TileSelect.SelectedItem as FanTile; loading = false; UpdateSelectors();
    }
    private void UpdateSelectors()
    {
        if (owner == null) return;
        loading = true;
        TempSelect.ItemsSource = new[] { new SensorChoice("Keine Temperatur zugeordnet", "") }.Concat(owner.Sensors.Where(s => s.Unit == "°C").Select(s => new SensorChoice(s.Name + " · " + s.Device, s.Key))).ToList();
        RpmSelect.ItemsSource = new[] { new SensorChoice("Kein Lüfterwert zugeordnet", "") }.Concat(owner.Sensors.Where(s => s.Unit is "RPM" or "%").Select(s => new SensorChoice(s.Name + " · " + s.Device, s.Key))).ToList();
        TempSelect.SelectedItem = TempSelect.Items.Cast<SensorChoice>().FirstOrDefault(s => s.Key == selected?.TemperatureSensorKey) ?? TempSelect.Items[0];
        RpmSelect.SelectedItem = RpmSelect.Items.Cast<SensorChoice>().FirstOrDefault(s => s.Key == selected?.RpmSensorKey) ?? RpmSelect.Items[0];
        NameInput.Text = selected?.Name ?? ""; SizeSlider.Value = selected?.Size ?? 210; VisibleToggle.IsChecked = selected?.Visible ?? false;
        loading = false;
    }
    private static BitmapImage Bitmap(string name)
    {
        var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", name)); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
    }
    private void BuildTiles()
    {
        if (owner == null || dragging) return;
        Scene.Children.Clear(); visuals.Clear();
        Scene.Background = string.IsNullOrEmpty(Settings.Background) ? new SolidColorBrush(Color.FromRgb(15, 24, 40)) : new ImageBrush(Bitmap(Settings.Background)) { Stretch = Stretch.Fill };
        foreach (var tile in Settings.Tiles.Take(64))
        {
            tile.Clamp();
            var root = new Grid { Width = tile.Size, Height = tile.Size + 65, Opacity = tile.Visible ? 1 : 0.38, Cursor = Cursors.SizeAll };
            root.RowDefinitions.Add(new() { Height = new GridLength(tile.Size) }); root.RowDefinitions.Add(new() { Height = new GridLength(65) });
            var picture = new Grid();
            picture.Children.Add(new Image { Source = Bitmap("fan.png"), Stretch = Stretch.Uniform });
            double diameter = tile.Size * .43;
            var center = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 7, 19, 35)), BorderBrush = Brushes.Cyan, BorderThickness = new(2), CornerRadius = new(diameter / 2), Width = diameter, Height = diameter, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var temperature = new TextBlock { Text = "—", Foreground = Brushes.White, FontSize = tile.Size * .14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            center.Child = temperature; picture.Children.Add(center);
            if (selected?.Id == tile.Id) picture.Children.Add(new Border { BorderBrush = Brushes.Cyan, BorderThickness = new(4), IsHitTestVisible = false });
            root.Children.Add(picture);
            var caption = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(226, 7, 19, 35)) };
            caption.Children.Add(new TextBlock { Text = tile.Name, FontSize = 19, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White });
            var rpm = new TextBlock { Text = "—", FontSize = 18, TextAlignment = TextAlignment.Center, Foreground = Brushes.Cyan };
            var status = new TextBlock { FontSize = 13, TextAlignment = TextAlignment.Center, Foreground = Brushes.Goldenrod };
            caption.Children.Add(rpm); caption.Children.Add(status); Grid.SetRow(caption, 1); root.Children.Add(caption);
            Canvas.SetLeft(root, tile.X); Canvas.SetTop(root, tile.Y); Scene.Children.Add(root); visuals[tile.Id] = (root, temperature, rpm, status);
            root.MouseLeftButtonDown += (_, e) => { selected = tile; loading = true; TileSelect.SelectedItem = tile; loading = false; UpdateSelectors(); grab = e.GetPosition(root); dragging = true; root.CaptureMouse(); e.Handled = true; };
            root.MouseMove += (_, e) => { if (!dragging || !root.IsMouseCaptured) return; var p = e.GetPosition(Scene); tile.X = p.X - grab.X; tile.Y = p.Y - grab.Y; tile.Clamp(); Canvas.SetLeft(root, tile.X); Canvas.SetTop(root, tile.Y); };
            root.MouseLeftButtonUp += (_, e) => { if (!dragging) return; dragging = false; root.ReleaseMouseCapture(); owner.StageChanged(); BuildTiles(); Refresh(); e.Handled = true; };
            root.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; owner.StageChanged(); } };
        }
        Refresh();
    }
    private void BackgroundSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || owner == null || BackgroundSelect.SelectedItem is not BackgroundChoice b) return;
        Settings.Background = b.File; BuildTiles(); owner.ApplyBrandBackground(); owner.StageChanged();
    }
    private void TileSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (loading) return; selected = TileSelect.SelectedItem as FanTile; UpdateSelectors(); BuildTiles(); }
    private void Mapping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || selected == null || owner == null) return;
        selected.TemperatureSensorKey = (TempSelect.SelectedItem as SensorChoice)?.Key ?? "";
        selected.RpmSensorKey = (RpmSelect.SelectedItem as SensorChoice)?.Key ?? "";
        if (selected.RpmSensorKey.Length > 0) autoSeen.Add(selected.RpmSensorKey);
        owner.StageChanged(); Refresh();
    }
    private void NameInput_LostFocus(object sender, RoutedEventArgs e)
    { if (loading || selected == null || owner == null) return; selected.Name = NameInput.Text; selected.Clamp(); RefreshTileList(); BuildTiles(); owner.StageChanged(); }
    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (loading || selected == null || owner == null) return; selected.Size = SizeSlider.Value; selected.Clamp(); BuildTiles(); owner.StageChanged(); }
    private void VisibleToggle_Click(object sender, RoutedEventArgs e)
    { if (selected == null || owner == null) return; selected.Visible = VisibleToggle.IsChecked == true; BuildTiles(); owner.StageChanged(); }
    private void RemoveTile_Click(object sender, RoutedEventArgs e)
    { if (selected == null || owner == null) return; if (selected.RpmSensorKey.Length > 0) Settings.HiddenAutoSensors.Add(selected.RpmSensorKey); Settings.Tiles.Remove(selected); selected = null; RefreshTileList(); BuildTiles(); owner.StageChanged(); }
    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (owner == null || ProfileSelect.SelectedItem is not FanEntry f || Settings.Tiles.Count >= 32) return;
        var tile = new FanTile { Name = f.Name, ProfileKey = f.Key, RpmSensorKey = f.RpmSensorKey };
        Settings.Tiles.Add(tile); selected = tile; RefreshTileList(); BuildTiles(); owner.StageChanged();
    }
    private void RestoreDetected_Click(object sender, RoutedEventArgs e)
    { if (owner == null) return; Settings.HiddenAutoSensors.Clear(); autoSeen.Clear(); Refresh(); owner.StageChanged(); }
    private void CopyObs_Click(object sender, RoutedEventArgs e)
    { if (owner == null) return; try { Clipboard.SetText(owner.OverlayUrl); owner.StageNotice("OBS-Adresse kopiert. Als Browserquelle mit 1920 × 1080 einfügen."); } catch (Exception ex) { owner.StageNotice(ex.Message); } }
    private void Preview_Click(object sender, RoutedEventArgs e)
    { if (owner != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(owner.OverlayUrl) { UseShellExecute = true }); }
    private void Plugin_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Extras", "de.crazybatto.fanatlas.streamDeckPlugin");
        if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        else owner?.StageNotice("Das Stream-Deck-Paket liegt neben dem Installer im Ausgabeordner.");
    }
    private void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        if (owner == null) return; var picker = new SaveFileDialog { Filter = "FanAtlas-Layout (*.json)|*.json", FileName = "Crazy-Batto-Luefterlayout.json" };
        if (picker.ShowDialog() != true) return;
        try { File.WriteAllText(picker.FileName, JsonSerializer.Serialize(Settings, StateStore.Json)); } catch (Exception ex) { owner.StageNotice(ex.Message); }
    }
    private void LoadLayout_Click(object sender, RoutedEventArgs e)
    {
        if (owner == null) return; var picker = new OpenFileDialog { Filter = "FanAtlas-Layout (*.json)|*.json" };
        if (picker.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 256000) throw new InvalidDataException("Layoutdatei zu groß.");
            var imported = JsonSerializer.Deserialize<StageSettings>(File.ReadAllText(picker.FileName)) ?? throw new InvalidDataException("Layout fehlt.");
            if (imported.Tiles == null || imported.Tiles.Count > 32 || imported.Tiles.Any(t => t == null) || !backgrounds.Any(b => b.File == imported.Background)) throw new InvalidDataException("Ungültiges Layout.");
            foreach (var tile in imported.Tiles) { tile.Clamp(); tile.Id = Guid.NewGuid().ToString("N"); }
            owner.State.Stage = imported; selected = null; Attach(owner); owner.ApplyBrandBackground(); owner.StageChanged();
        }
        catch (Exception ex) { owner.StageNotice("Layout konnte nicht geladen werden: " + ex.Message); }
    }
}

