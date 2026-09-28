using System.Security.Cryptography;
using System.Text;

namespace FanAtlas;

public class StageSettings
{
    public string Background { get; set; } = "stream-startet.jpg";
    public List<FanTile> Tiles { get; set; } = new();
    public List<string> HiddenAutoSensors { get; set; } = new();
    public void Normalize()
    {
        if (Background is not ("stream-startet.jpg" or "bin-gleich-zurueck.jpg" or "")) Background = "stream-startet.jpg";
        Tiles = (Tiles ?? new()).Where(t => t != null).Take(32).ToList();
        HiddenAutoSensors ??= new();
        var ids = new HashSet<string>();
        foreach (var tile in Tiles) { tile.Clamp(); if (string.IsNullOrEmpty(tile.Id) || !ids.Add(tile.Id)) { tile.Id = Guid.NewGuid().ToString("N"); ids.Add(tile.Id); } }
    }
}
public class FanTile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Lüfter";
    public string RpmSensorKey { get; set; } = "";
    public string TemperatureSensorKey { get; set; } = "";
    public string ProfileKey { get; set; } = "";
    public double X { get; set; } = 250;
    public double Y { get; set; } = 200;
    public double Size { get; set; } = 210;
    public bool Visible { get; set; } = true;
    public override string ToString() => Name;
    public void Clamp()
    {
        RpmSensorKey ??= ""; TemperatureSensorKey ??= ""; ProfileKey ??= "";
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 120, 420) : 210;
        X = double.IsFinite(X) ? Math.Clamp(X, 0, 1920 - Size) : 250;
        Y = double.IsFinite(Y) ? Math.Clamp(Y, 0, 1080 - Size - 64) : 200;
        Name = string.IsNullOrWhiteSpace(Name) ? "Lüfter" : Name[..Math.Min(Name.Length, 80)];
    }
}
public class BridgeSettings
{
    public int Port { get; set; } = 17654;
    public string ControlToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string OverlayToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
public static class SensorIdentity
{
    public static string PublicId(string key) => string.IsNullOrEmpty(key) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
}
public record BridgeSensor(string Id, string Name, string Device, string Unit, double? Value, bool Fresh, DateTime UpdatedUtc);
public record BridgeTile(string Id, string Name, double X, double Y, double Size, bool Visible, string RpmSensorId, string TemperatureSensorId, bool FromProfile);
public record BridgeCurve(string Id, string Name, bool IsCustom);
public record BridgeScene(string Background, List<BridgeTile> Tiles);
public record BridgeSnapshot(string Version, DateTime GeneratedUtc, List<BridgeSensor> Sensors, List<BridgeCurve> Curves, BridgeScene Scene, string SelectedCurveId, string SelectionNote);

