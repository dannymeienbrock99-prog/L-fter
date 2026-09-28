using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FanAtlas;

public class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(property); return true; }
}
public class CurvePoint : Notify
{
    private double temperature, duty;
    public double Temperature { get => temperature; set => Set(ref temperature, value); }
    public double Duty { get => duty; set => Set(ref duty, value); }
    public CurvePoint() { }
    public CurvePoint(double t, double d) { Temperature = t; Duty = d; }
}
public class FanCurve
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Eigene Kurve";
    public string SensorId { get; set; } = "";
    public string SensorLabel { get; set; } = "CPU-Temperatur";
    public string DeviceType { get; set; } = "";
    public string Mode { get; set; } = "Normal";
    public bool Predefined { get; set; }
    public bool IsCustom { get; set; }
    public string Origin { get; set; } = "Eigener Entwurf";
    public ObservableCollection<CurvePoint> Points { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public string Display => Name + (IsCustom ? "  ·  Eigene" : "  ·  " + Id.Trim('{', '}')[..Math.Min(6, Id.Trim('{', '}').Length)]);
    [System.Text.Json.Serialization.JsonIgnore] public string Subtitle => IsCustom ? "Eigener Entwurf" : DeviceType + " · " + Id.Trim('{', '}')[..Math.Min(6, Id.Trim('{', '}').Length)];
    public FanCurve Copy(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid().ToString() : Id, Name = Name, SensorId = SensorId,
        SensorLabel = SensorLabel, DeviceType = DeviceType, Mode = Mode, Predefined = Predefined,
        IsCustom = IsCustom, Origin = Origin, Points = new(Points.Select(p => new CurvePoint(p.Temperature, p.Duty)))
    };
    public static string? Validate(FanCurve c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100) return "Bitte einen Namen mit 1 bis 100 Zeichen eingeben.";
        if (string.IsNullOrWhiteSpace(c.SensorLabel)) return "Bitte den Temperaturbezug angeben, z. B. CPU oder Kühlmittel.";
        if (c.Points.Count < 2 || c.Points.Count > 20) return "Eine Kurve braucht 2 bis 20 Punkte.";
        for (int i = 0; i < c.Points.Count; i++)
        {
            var p = c.Points[i];
            if (!double.IsFinite(p.Temperature) || p.Temperature < 0 || p.Temperature > 120 || !double.IsFinite(p.Duty) || p.Duty < 0 || p.Duty > 100)
                return "Temperaturen müssen zwischen 0 und 120 °C liegen, Stellwerte zwischen 0 und 100 %.";
            if (i > 0 && p.Temperature <= c.Points[i - 1].Temperature) return "Temperaturen müssen in aufsteigender Reihenfolge stehen und dürfen sich nicht wiederholen.";
        }
        return null;
    }
    public double? Evaluate(double temperature)
    {
        if (Validate(this) != null || !double.IsFinite(temperature)) return null;
        if (temperature <= Points[0].Temperature) return Points[0].Duty;
        for (int i = 1; i < Points.Count; i++)
            if (temperature <= Points[i].Temperature)
            { var a = Points[i - 1]; var b = Points[i]; return a.Duty + (b.Duty - a.Duty) * (temperature - a.Temperature) / (b.Temperature - a.Temperature); }
        return Points[^1].Duty;
    }
}
public class FanEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hub { get; set; } = "";
    public string Serial { get; set; } = "";
    public string CurveId { get; set; } = "";
    public string CurveName { get; set; } = "";
    public string RpmSensorKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Description => $"Hub …{Hub[^Math.Min(8, Hub.Length)..]} · {CurveName}";
}
public class ProfileData
{
    public string Name { get; set; } = "Kein Profil geladen";
    public string SourcePath { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public List<FanCurve> Curves { get; set; } = new();
    public List<FanEntry> Fans { get; set; } = new();
}
public class AppState
{
    public int Version { get; set; } = 1;
    public ProfileData Profile { get; set; } = new();
    public List<FanCurve> CustomCurves { get; set; } = new();
    public List<string> CsvPaths { get; set; } = new();
    public bool NvidiaEnabled { get; set; } = true;
    public int StaleSeconds { get; set; } = 15;
    public StageSettings Stage { get; set; } = new();
    public BridgeSettings Bridge { get; set; } = new();
}
public record Measurement(string Key, string Name, string Device, string Unit, double Value, string Source, DateTime UpdatedUtc, bool Live = true);
public class SensorRow : Notify
{
    private Measurement m;
    public SensorRow(Measurement value) { m = value; }
    public string Key => m.Key;
    public string Name => m.Name;
    public string Device => m.Device;
    public string Unit => m.Unit;
    public string Source => m.Source;
    public double NumericValue => m.Value;
    public DateTime UpdatedUtc => m.UpdatedUtc;
    public bool Fresh { get; private set; }
    public string ValueText => Fresh ? $"{m.Value.ToString(m.Unit == "RPM" ? "N0" : "0.#", CultureInfo.CurrentCulture)} {m.Unit}" : "—";
    public string LastValue => $"{m.Value:0.#} {m.Unit}";
    public string Status => Fresh ? "Aktuell" : m.Live ? "Veraltet / Quelle fehlt" : "Warte auf neue Logzeile";
    public string LastUpdate => m.UpdatedUtc.ToLocalTime().ToString("HH:mm:ss");
    public override string ToString() => $"{Device} · {Name} ({Unit})";
    public void Update(Measurement value, int staleSeconds)
    { m = value; Refresh(staleSeconds); Changed(""); }
    public void Refresh(int staleSeconds)
    { Fresh = m.Live && DateTime.UtcNow - m.UpdatedUtc < TimeSpan.FromSeconds(staleSeconds); Changed(""); }
}
public static class StateStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DirectoryPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Daten");
    public static string StatePath => Path.Combine(DirectoryPath, "einstellungen.json");
    public static string? LoadWarning { get; private set; }
    public static AppState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new();
            var s = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), Json) ?? throw new InvalidDataException();
            if (s.Version != 1 || s.Profile == null || s.CustomCurves == null || s.CsvPaths == null) throw new InvalidDataException("Unbekanntes Datenformat.");
            s.Stage ??= new(); s.Stage.Normalize(); s.Bridge ??= new();
            if (s.Bridge.ControlToken?.Length != 64 || s.Bridge.OverlayToken?.Length != 64 || s.Bridge.ControlToken == s.Bridge.OverlayToken) s.Bridge = new();
            s.StaleSeconds = Math.Clamp(s.StaleSeconds, 5, 300); return s;
        }
        catch (Exception e) { LoadWarning = "Gespeicherte Einstellungen konnten nicht geladen werden. Die Datei bleibt erhalten: " + e.Message; return new(); }
    }
    public static void Save(AppState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        if (File.Exists(StatePath)) File.Copy(StatePath, StatePath + ".bak", true);
        File.Move(temp, StatePath, true);
    }
}

