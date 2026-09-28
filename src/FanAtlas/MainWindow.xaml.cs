using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FanAtlas;

public partial class MainWindow : Window
{
    public AppState State { get; }
    public ObservableCollection<SensorRow> Sensors { get; } = new();
    private readonly NvidiaReader nvidia = new();
    private readonly CsvReader csv = new();
    private readonly Dictionary<string, List<(DateTime Time, double Value)>> histories = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Task? pollTask;
    private bool loading, dirty, changingSelection, closing, testMode;
    private FanCurve? draft;
    private FanCurve? selectedOriginal;
    private FanEntry? fan;
    private LocalBridge? bridge;
    public string OverlayUrl => bridge?.OverlayUrl ?? $"http://127.0.0.1:{State.Bridge.Port}/overlay?token={State.Bridge.OverlayToken}";
    public string BridgeStatusText => bridge?.Status ?? "Stream-Vorschau noch nicht gestartet";
    public MainWindow(AppState state, bool test = false)
    {
        State = state; testMode = test; loading = true;
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("de-DE");
        SensorGrid.ItemsSource = Sensors;
        NvidiaToggle.IsChecked = state.NvidiaEnabled;
        StaleCombo.ItemsSource = new[] { 5, 15, 30, 60, 120, 300 };
        StaleCombo.SelectedItem = state.StaleSeconds;
        if (StaleCombo.SelectedIndex < 0) StaleCombo.SelectedItem = 15;
        StorageInfo.Text = "Speicherort: " + StateStore.DirectoryPath;
        EditorChart.Edited += () => { if (!loading) { dirty = true; ValidateDraft(); } };
        RefreshProfile(); RefreshCurves(); RefreshCsv();
        loading = false;
        Stage.Attach(this); ApplyBrandBackground();
        if (CurveList.Items.Count > 0) CurveList.SelectedIndex = 0;
        if (FanList.Items.Count > 0) FanList.SelectedIndex = 0;
        timer.Tick += (_, _) => { if (pollTask == null || pollTask.IsCompleted) pollTask = PollOnce(); };
        Loaded += async (_, _) => { if (!testMode) { await StartBridge(); timer.Start(); pollTask = PollOnce(); Pages.SelectedIndex = 4; } };
        Closing += (_, e) => { if (!testMode && !ConfirmDraft()) e.Cancel = true; else { closing = true; timer.Stop(); } };
        Closed += async (_, _) => { if (pollTask != null) await pollTask; nvidia.Dispose(); if (bridge != null) await bridge.DisposeAsync(); };
        if (StateStore.LoadWarning != null) SetStatus(StateStore.LoadWarning);
    }
    private void SetStatus(string text) { StatusText.Text = text; StatusText.ToolTip = text; }
    internal bool SaveState()
    {
        try { StateStore.Save(State); return true; }
        catch (Exception e) { MessageBox.Show(this, "Speichern fehlgeschlagen:\n" + e.Message, "FanAtlas", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    private void RefreshProfile()
    {
        FanList.ItemsSource = State.Profile.Fans;
        FanMetric.Text = State.Profile.Fans.Count.ToString();
        int devices = State.Profile.Fans.Select(f => f.Serial.Length > 0 ? f.Serial : f.Key).Distinct().Count();
        ProfileCaption.Text = $"{State.Profile.Name} · {State.Profile.Fans.Count} gespeicherte Zuordnungen · {devices} unterschiedliche Geräte-IDs. Dateiimport: {State.Profile.ImportedAt:g}";
        ProfileCaption.ToolTip = State.Profile.SourcePath;
    }
    private void RefreshCurves(string? selectedId = null)
    {
        changingSelection = true;
        var assigned = State.Profile.Fans.Select(f => f.CurveId).ToHashSet();
        var all = State.CustomCurves.Concat(State.Profile.Curves.OrderByDescending(c => assigned.Contains(c.Id)).ThenBy(c => c.Predefined).ThenBy(c => c.Name)).ToList();
        CurveList.ItemsSource = all;
        CompareCombo.ItemsSource = new[] { new FanCurve { Id = "", Name = "Kein Vergleich" } }.Concat(all).ToList();
        CompareCombo.SelectedIndex = 0;
        if (selectedId != null) CurveList.SelectedItem = all.FirstOrDefault(c => c.Id == selectedId);
        changingSelection = false;
        CurveMetric.Text = State.CustomCurves.Count.ToString();
    }
    private void RefreshCsv()
    {
        CsvList.ItemsSource = null; CsvList.ItemsSource = State.CsvPaths;
        CsvStatus.Text = State.CsvPaths.Count == 0 ? "Noch keine CSV verbunden. GPU-Werte funktionieren unabhängig davon." : "Dateien werden beim nächsten Durchlauf geprüft.";
    }
    public async Task PollOnce()
    {
        bool readNvidia = State.NvidiaEnabled; var paths = State.CsvPaths.ToArray();
        try
        {
            var values = await Task.Run(() =>
            {
                var result = readNvidia ? nvidia.Read() : new List<Measurement>();
                result.AddRange(csv.Read(paths)); return result;
            });
            if (closing) return;
            foreach (var m in values)
            {
                var row = Sensors.FirstOrDefault(s => s.Key == m.Key);
                if (row == null) { row = new(m); Sensors.Add(row); }
                row.Update(m, State.StaleSeconds);
                if (row.Fresh)
                {
                    if (!histories.TryGetValue(m.Key, out var list)) histories[m.Key] = list = new();
                    if (list.Count == 0 || list[^1].Time != m.UpdatedUtc) list.Add((m.UpdatedUtc, m.Value));
                    list.RemoveAll(s => DateTime.UtcNow - s.Time > TimeSpan.FromMinutes(10));
                    if (list.Count > 1200) list.RemoveRange(0, list.Count - 1200);
                }
            }
            // Explicitly removed sources vanish; temporarily unavailable sources age visibly.
            foreach (var old in Sensors.Where(s => s.Key.StartsWith("nvml/") && !State.NvidiaEnabled
                || s.Key.StartsWith("csv/") && !State.CsvPaths.Any(p => s.Key.StartsWith("csv/" + Path.GetFullPath(p).ToUpperInvariant() + "/"))).ToList())
            { Sensors.Remove(old); histories.Remove(old.Key); }
            foreach (var s in Sensors) s.Refresh(State.StaleSeconds);
            var gpu = Sensors.FirstOrDefault(s => s.Key.StartsWith("nvml/") && s.Unit == "°C" && s.Fresh);
            GpuMetric.Text = gpu?.ValueText ?? "—";
            GpuHint.Text = gpu?.Device.Replace("NVIDIA GeForce ", "") ?? "Kein aktueller GPU-Wert";
            SensorMetric.Text = Sensors.Count(s => s.Fresh).ToString();
            NvidiaStatus.Text = readNvidia ? nvidia.Status : "Ausgeschaltet";
            CsvStatus.Text = paths.Length == 0 ? "Noch keine CSV verbunden. GPU-Werte funktionieren unabhängig davon." : string.Join("\n", csv.Messages);
            SourceBanner.Text = paths.Length == 0 ? "CPU / Mainboard / Corsair: Noch keine Sensor-CSV verbunden."
                : $"{Sensors.Count(s => s.Fresh && s.Key.StartsWith("csv/"))} aktuelle CSV-Messwerte · Fehlende Werte bleiben als solche sichtbar.";
            if (SensorGrid.SelectedItem == null && Sensors.Count > 0) SensorGrid.SelectedIndex = 0;
            RefreshFanMapping(); RefreshFanValue(); RefreshHistory(); Stage.Refresh(); PublishBridge();
        }
        catch (Exception e) { if (!closing) SetStatus("Messwerte konnten nicht aktualisiert werden: " + e.Message); }
    }
    private void RefreshHistory()
    {
        if (SensorGrid.SelectedItem is not SensorRow row) return;
        HistoryTitle.Text = row.Name + " · letzte 10 Minuten · " + row.Status;
        History.Samples = histories.GetValueOrDefault(row.Key) ?? new();
        History.Unit = row.Unit; History.InvalidateVisual();
    }
    private void SensorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshHistory();
    private void Sources_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 3;
    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDraft()) return;
        var picker = new OpenFileDialog { Filter = "iCUE-Profil (*.cueprofile)|*.cueprofile", Title = "Exportiertes iCUE-Profil öffnen" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var profile = ProfileReader.Read(picker.FileName);
            State.Profile = profile; draft = null; selectedOriginal = null; dirty = false;
            RefreshProfile(); RefreshCurves(); Stage.Attach(this);
            if (CurveList.Items.Count > 0) CurveList.SelectedIndex = 0;
            if (FanList.Items.Count > 0) FanList.SelectedIndex = 0;
            if (SaveState()) SetStatus($"Profil gelesen: {profile.Fans.Count} Zuordnungen, {profile.Curves.Count} Kurven. iCUE wurde nicht verändert.");
            Pages.SelectedIndex = 1;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profil konnte nicht gelesen werden", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void FanList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FanList.SelectedItem is not FanEntry selected) return; fan = selected;
        FanTitle.Text = fan.Name; FanDetails.Text = "Gespeichert an Hub …" + fan.Hub[^Math.Min(12, fan.Hub.Length)..] + "\nGeräte-ID: " + fan.Serial;
        var curve = State.Profile.Curves.FirstOrDefault(c => c.Id == fan.CurveId);
        FanCurveTitle.Text = curve?.Name ?? "Zugeordnete Kurve fehlt";
        FanCurveSensor.Text = curve == null ? "Die Referenz ist in der Datei nicht auflösbar." : curve.SensorLabel + " · " + curve.Points.Count + " gespeicherte Punkte · Modus: " + curve.Mode;
        FanChart.Curve = curve;
        RefreshFanMapping(true); RefreshFanValue();
    }
    private string mappingSignature = "";
    private void RefreshFanMapping(bool force = false)
    {
        var options = Sensors.Where(s => s.Unit is "RPM" or "%").ToList();
        string sig = string.Join("|", options.Select(o => o.Key));
        if (!force && sig == mappingSignature) return;
        mappingSignature = sig; bool before = loading; loading = true;
        FanRpmCombo.ItemsSource = new object[] { "Kein Messwert zugeordnet" }.Concat(options.Cast<object>()).ToList();
        FanRpmCombo.SelectedItem = options.FirstOrDefault(s => s.Key == fan?.RpmSensorKey) as object ?? "Kein Messwert zugeordnet";
        loading = before;
    }
    private void FanRpmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || fan == null) return;
        fan.RpmSensorKey = (FanRpmCombo.SelectedItem as SensorRow)?.Key ?? "";
        SaveState(); RefreshFanValue();
    }
    private void RefreshFanValue()
    {
        var row = Sensors.FirstOrDefault(s => s.Key == fan?.RpmSensorKey);
        FanLiveValue.Text = row == null ? "Kein Messwert zugeordnet" : row.ValueText + " · " + row.Status;
    }
    private void EditFan_Click(object sender, RoutedEventArgs e)
    {
        if (fan == null || !ConfirmDraft()) return;
        CurveList.SelectedItem = CurveList.Items.Cast<FanCurve>().FirstOrDefault(c => c.Id == fan.CurveId);
        Pages.SelectedIndex = 2;
    }
    private void CurveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingSelection || CurveList.SelectedItem is not FanCurve selected) return;
        if (!ConfirmDraft()) { changingSelection = true; CurveList.SelectedItem = selectedOriginal; changingSelection = false; return; }
        var canonical = CurveList.Items.Cast<FanCurve>().FirstOrDefault(c => c.Id == selected.Id) ?? selected;
        changingSelection = true; CurveList.SelectedItem = canonical; changingSelection = false;
        selectedOriginal = canonical; LoadDraft(canonical.Copy());
    }
    private void LoadDraft(FanCurve value)
    {
        loading = true; draft = value;
        CurveNameInput.Text = value.Name; CurveSensorInput.Text = value.SensorLabel;
        EditorChart.Curve = draft; PointsGrid.ItemsSource = draft.Points;
        DraftInfo.Text = value.IsCustom ? "Eigener Entwurf · erst nach Übernahme in deinem Steuerprogramm wirksam."
            : "Kopie aus iCUE-Profil · Speichern legt eine eigene Kurve an.";
        dirty = false; loading = false; ValidateDraft();
    }
    private void Draft_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || draft == null) return;
        draft.Name = CurveNameInput.Text; draft.SensorLabel = CurveSensorInput.Text; dirty = true; ValidateDraft();
    }
    private void ValidateDraft()
    {
        if (draft == null) return;
        string? error = FanCurve.Validate(draft);
        string warning = draft.Points.Zip(draft.Points.Skip(1)).Any(p => p.Second.Duty < p.First.Duty) ? "Hinweis: Die Leistung sinkt an mindestens einer Stelle bei steigender Temperatur." : "";
        ValidationText.Text = error ?? warning;
        var value = draft.Evaluate(PreviewSlider.Value);
        PreviewValue.Text = value.HasValue ? $"{PreviewSlider.Value:0} °C → {value:0.#} %  ·  Simulation" : "Vorschau erst bei gültigen Kurvenpunkten";
        EditorChart.MarkerTemperature = PreviewSlider.Value; EditorChart.InvalidateVisual();
    }
    private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!loading) ValidateDraft(); }
    private void PointsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(ValidateDraft, DispatcherPriority.Background);
    private void CompareCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (EditorChart != null) { EditorChart.Comparison = CompareCombo.SelectedItem as FanCurve; EditorChart.InvalidateVisual(); } }
    private bool ConfirmDraft()
    {
        if (!dirty) return true;
        var choice = MessageBox.Show(this, "Änderungen an der aktuellen Kurve speichern?", "Ungespeicherter Entwurf", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return SaveDraft();
        dirty = false; return true;
    }
    private bool CommitDraft()
    {
        PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true); PointsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (draft == null) return false;
        if (HasValidationError(PointsGrid)) { ValidationText.Text = "Bitte fehlerhafte Zahleneingaben korrigieren."; return false; }
        string? error = FanCurve.Validate(draft); if (error != null) { ValidationText.Text = error; return false; }
        return true;
    }
    private static bool HasValidationError(DependencyObject node)
    {
        if (Validation.GetHasError(node)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasValidationError(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    internal bool SaveDraft()
    {
        if (!CommitDraft()) return false;
        var saved = draft!.Copy();
        if (!saved.IsCustom) { saved.Id = Guid.NewGuid().ToString(); saved.IsCustom = true; }
        saved.Origin = "Eigener Entwurf"; saved.Predefined = false; saved.Mode = "Entwurf";
        var old = State.CustomCurves.ToList();
        int index = State.CustomCurves.FindIndex(c => c.Id == saved.Id);
        if (index >= 0) State.CustomCurves[index] = saved; else State.CustomCurves.Add(saved);
        if (!SaveState()) { State.CustomCurves = old; return false; }
        dirty = false; selectedOriginal = saved; RefreshCurves(saved.Id); LoadDraft(saved.Copy());
        SetStatus("Eigene Kurve gespeichert. Noch nicht in iCUE aktiviert."); return true;
    }
    private void SaveCurve_Click(object sender, RoutedEventArgs e) => SaveDraft();
    private void NewCurve_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDraft()) return;
        changingSelection = true; CurveList.SelectedItem = null; changingSelection = false; selectedOriginal = null;
        LoadDraft(new FanCurve { Name = "Meine CPU-Kurve", IsCustom = true, Points = new(new[] { new CurvePoint(30, 30), new(40, 35), new(50, 45), new(60, 60), new(70, 75), new(80, 90), new(90, 100) }) });
        dirty = true; SetStatus("Neuer Kurvenentwurf. Temperaturbezug und Werte auf deine Kühlung abstimmen.");
    }
    private void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        if (draft == null || draft.Points.Count >= 20) return;
        if (draft.Points.Count == 0) { draft.Points.Add(new(30, 30)); return; }
        int index = PointsGrid.SelectedIndex;
        if (index < 0 || index >= draft.Points.Count - 1)
        {
            var last = draft.Points[^1];
            if (last.Temperature < 120) { draft.Points.Add(new(Math.Min(120, last.Temperature + 5), last.Duty)); return; }
            index = draft.Points.Count - 2;
        }
        if (index < 0) return;
        var a = draft.Points[index]; var b = draft.Points[index + 1];
        if (b.Temperature - a.Temperature < 1) { SetStatus("Zwischen diesen Punkten ist zu wenig Platz."); return; }
        draft.Points.Insert(index + 1, new(Math.Round((a.Temperature + b.Temperature) / 2, 1), Math.Round((a.Duty + b.Duty) / 2, 1)));
    }
    private void RemovePoint_Click(object sender, RoutedEventArgs e)
    { if (draft?.Points.Count > 2 && PointsGrid.SelectedItem is CurvePoint p) draft.Points.Remove(p); else SetStatus("Punkt auswählen. Mindestens zwei Punkte müssen bleiben."); }
    private void DeleteCurve_Click(object sender, RoutedEventArgs e)
    {
        if (selectedOriginal?.IsCustom != true) { SetStatus("Nur eigene gespeicherte Kurven können gelöscht werden."); return; }
        if (MessageBox.Show(this, $"Eigene Kurve „{selectedOriginal.Name}“ löschen?", "Kurve löschen", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var old = State.CustomCurves.ToList(); State.CustomCurves.RemoveAll(c => c.Id == selectedOriginal.Id);
        if (!SaveState()) { State.CustomCurves = old; return; }
        dirty = false; draft = null; selectedOriginal = null; RefreshCurves();
        EditorChart.Curve = null; PointsGrid.ItemsSource = null;
        if (CurveList.Items.Count > 0) CurveList.SelectedIndex = 0;
    }
    internal static string CurveCsv(FanCurve curve) => "Temperatur (°C);Lüfterleistung (%)\r\n" + string.Join("\r\n", curve.Points.Select(p => $"{p.Temperature.ToString(CultureInfo.InvariantCulture)};{p.Duty.ToString(CultureInfo.InvariantCulture)}")) + "\r\n";
    private void ExportCurve_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        var picker = new SaveFileDialog { Filter = "FanAtlas-Kurve (*.json)|*.json|Wertetabelle (*.csv)|*.csv", FileName = SafeName(draft!.Name), AddExtension = true };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, picker.FilterIndex == 2 ? CurveCsv(draft!) : JsonSerializer.Serialize(draft, StateStore.Json), new UTF8Encoding(true)); SetStatus("Entwurf exportiert. Aktivierung erfolgt separat in iCUE."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export fehlgeschlagen"); }
    }
    private static string SafeName(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
    private void CopyCurve_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        try { Clipboard.SetText(draft!.Name + "\r\nTemperaturbezug: " + draft.SensorLabel + "\r\n" + CurveCsv(draft)); SetStatus("Wertetabelle kopiert. Die Punkte in iCUE unter einer eigenen Temperaturkurve eintragen."); }
        catch (Exception ex) { SetStatus("Zwischenablage nicht verfügbar: " + ex.Message); }
    }
    private void ImportCurve_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDraft()) return;
        var picker = new OpenFileDialog { Filter = "FanAtlas-Kurve (*.json)|*.json", Title = "Eigenen Kurvenentwurf importieren" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 1000000) throw new InvalidDataException("Kurvendatei zu groß.");
            var imported = JsonSerializer.Deserialize<FanCurve>(File.ReadAllText(picker.FileName), StateStore.Json) ?? throw new InvalidDataException("Kurvendatei ist leer.");
            if (imported.Points == null) throw new InvalidDataException("Kurvenpunkte fehlen.");
            string? error = FanCurve.Validate(imported); if (error != null) throw new InvalidDataException(error);
            imported.Id = Guid.NewGuid().ToString(); imported.IsCustom = true; imported.Origin = "Eigener Entwurf";
            changingSelection = true; CurveList.SelectedItem = null; changingSelection = false;
            selectedOriginal = null; LoadDraft(imported); dirty = true; SetStatus("Kurve eingelesen. Mit „Eigene Kurve speichern“ übernehmen.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kurve konnte nicht gelesen werden"); }
    }
    private void AddCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Sensorprotokoll (*.csv)|*.csv", Multiselect = true, Title = "Laufendes iCUE- oder HWiNFO-Protokoll auswählen" };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames) if (!State.CsvPaths.Contains(path, StringComparer.OrdinalIgnoreCase) && State.CsvPaths.Count < 20) State.CsvPaths.Add(path);
        SaveState(); RefreshCsv(); SetStatus("CSV verbunden. Aktuelle Anzeige beginnt nach einer neu geschriebenen vollständigen Zeile.");
    }
    private void RemoveCsv_Click(object sender, RoutedEventArgs e)
    {
        if (CsvList.SelectedItem is not string path) return;
        State.CsvPaths.Remove(path); SaveState(); RefreshCsv(); SetStatus("Quelle getrennt. Die Protokolldatei bleibt erhalten.");
    }
    private void FindLogs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "iCUE-Protokollordner auswählen", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var files = Directory.EnumerateFiles(picker.FolderName, "*.csv").OrderByDescending(File.GetLastWriteTimeUtc).Take(1).ToList();
            if (files.Count == 0) { SetStatus("In diesem Ordner liegt noch keine CSV-Datei."); return; }
            if (!State.CsvPaths.Contains(files[0], StringComparer.OrdinalIgnoreCase) && State.CsvPaths.Count < 20) State.CsvPaths.Add(files[0]);
            SaveState(); RefreshCsv(); SetStatus("Neueste CSV im gewählten Ordner verbunden: " + Path.GetFileName(files[0]));
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void NvidiaToggle_Click(object sender, RoutedEventArgs e) { State.NvidiaEnabled = NvidiaToggle.IsChecked == true; SaveState(); }
    private void StaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && StaleCombo.SelectedItem is int seconds) { State.StaleSeconds = seconds; SaveState(); } }
    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(StateStore.DirectoryPath); Process.Start(new ProcessStartInfo("explorer.exe", StateStore.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    public async Task StartBridge()
    {
        bridge = new LocalBridge(State.Bridge, id => Dispatcher.InvokeAsync(() =>
        {
            if (dirty) return (false, "Ungespeicherte Kurve in FanAtlas: zuerst speichern oder verwerfen.");
            var curve = CurveList.Items.Cast<FanCurve>().FirstOrDefault(c => c.Id == id);
            if (curve == null) return (false, "Kurve nicht gefunden.");
            CurveList.SelectedItem = curve; Pages.SelectedIndex = 2; PublishBridge();
            SetStatus("Stream Deck: Entwurf ausgewählt. iCUE-Steuerung unverändert.");
            return (true, "Entwurf ausgewählt; keine Hardwareänderung. Für echte Wechsel die offizielle iCUE-Aktion verwenden.");
        }).Task);
        PublishBridge(); await bridge.Start(); SaveState(); Stage.Refresh();
    }
    internal void StageChanged() { SaveState(); PublishBridge(); }
    internal void StageNotice(string message) => SetStatus(message);
    internal void ApplyBrandBackground()
    {
        string filename = string.IsNullOrEmpty(State.Stage.Background) ? "stream-startet.jpg" : State.Stage.Background;
        if (!new[] { "stream-startet.jpg", "bin-gleich-zurueck.jpg" }.Contains(filename)) filename = "stream-startet.jpg";
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", filename);
        if (File.Exists(path)) Background = new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri(path))) { Stretch = Stretch.UniformToFill };
    }
    internal BridgeSnapshot CreateSnapshot()
    {
        var sensors = Sensors.Select(s => new BridgeSensor(SensorIdentity.PublicId(s.Key), s.Name, s.Key.StartsWith("csv/") ? "Sensorprotokoll" : s.Device, s.Unit, s.Fresh ? s.NumericValue : null, s.Fresh, s.UpdatedUtc)).ToList();
        var tiles = State.Stage.Tiles.Select(t => new BridgeTile(t.Id, t.Name, t.X, t.Y, t.Size, t.Visible, SensorIdentity.PublicId(t.RpmSensorKey), SensorIdentity.PublicId(t.TemperatureSensorKey), t.ProfileKey.Length > 0)).ToList();
        var curves = State.CustomCurves.Concat(State.Profile.Curves).Select(c => new BridgeCurve(c.Id, c.Name, c.IsCustom)).ToList();
        return new("0.2", DateTime.UtcNow, sensors, curves, new(State.Stage.Background, tiles), selectedOriginal?.Id ?? "", "Auswahl = Entwurf. Hardwarewechsel über iCUE.");
    }
    private void PublishBridge() => bridge?.Publish(CreateSnapshot());
}

