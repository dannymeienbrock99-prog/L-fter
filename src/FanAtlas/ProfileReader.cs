using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Globalization;

namespace FanAtlas;

public static class ProfileReader
{
    public static ProfileData Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName != "cereal") throw new InvalidDataException("Diese Datei ist kein unterstütztes iCUE-Profil.");
        var result = new ProfileData { Name = doc.Descendants("profile").FirstOrDefault()?.Element("name")?.Value ?? Path.GetFileNameWithoutExtension(path), SourcePath = path, ImportedAt = DateTime.Now };
        var byId = new Dictionary<string, FanCurve>(StringComparer.OrdinalIgnoreCase);
        // The stored assignments reference curve GUIDs. Default curves are only fallbacks;
        // confusing them with the assigned custom curve displays the wrong cooling policy.
        foreach (var points in doc.Descendants("customModePoints"))
        {
            var data = points.Parent!;
            var b = data.Element("base");
            string id = b?.Element("id")?.Value ?? "";
            if (id.Length == 0) continue;
            var curve = new FanCurve
            {
                Id = id, Name = b?.Element("name")?.Value ?? "Unbenannte Kurve", SensorId = data.Element("sensorId")?.Value ?? "",
                DeviceType = data.Element("type")?.Value ?? "", Mode = b?.Element("coolingMode")?.Value ?? "Unbekannt",
                Predefined = b?.Element("predefined")?.Value == "true", Origin = "iCUE-Profil · " + result.Name
            };
            curve.SensorLabel = SensorLabel(curve.SensorId);
            foreach (var p in points.Elements())
            {
                if (p.Element("enabled")?.Value == "false") continue;
                if (double.TryParse(p.Element("sensorValue")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                    && double.TryParse(p.Element("rpmPercents")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && double.IsFinite(t) && double.IsFinite(d)) curve.Points.Add(new(t, d));
            }
            // A later full definition in coolingModes is authoritative within this file.
            byId[id] = curve;
        }
        result.Curves = byId.Values.OrderBy(c => c.Predefined).ThenBy(c => c.Name).ToList();
        var seen = new HashSet<string>();
        foreach (var key in doc.Descendants("key"))
        {
            if (!key.Value.Contains("senstype<fan>", StringComparison.OrdinalIgnoreCase)) continue;
            var value = key.Parent?.Element("value");
            var assignment = value?.Element("assignmentConfigId")?.Value;
            if (assignment == null || !seen.Add(key.Value)) continue;
            var serial = Part(key.Value, "sensorSN"); var hub = Part(key.Value, "serial");
            byId.TryGetValue(assignment, out var curve);
            string type = serial.StartsWith("01") ? "QX-Lüfter" : serial.StartsWith("07") ? "AIO-Kanal" : "Lüfterkanal";
            result.Fans.Add(new() { Key = key.Value, Serial = serial, Hub = hub, Name = type + " · …" + serial[^Math.Min(6, serial.Length)..], CurveId = assignment, CurveName = curve?.Name ?? "Kurve nicht in Datei enthalten" });
        }
        if (result.Curves.Count == 0 && result.Fans.Count == 0) throw new InvalidDataException("In dieser Datei wurden keine unterstützten Kühlkurven oder Lüfterzuordnungen gefunden.");
        return result;
    }
    public static string Part(string text, string name) => Regex.Match(text, Regex.Escape(name) + "<([^>]*)>").Groups[1].Value;
    public static string SensorLabel(string id)
    {
        if (id.Contains("TemperaturePackage", StringComparison.OrdinalIgnoreCase)) return "CPU Package (Profil)";
        if (id.Contains("sensorSN<")) { var sn = Part(id, "sensorSN"); return "Corsair-Temperatur · …" + sn[^Math.Min(8, sn.Length)..]; }
        return string.IsNullOrEmpty(id) ? "Temperaturquelle nicht gespeichert" : "Temperaturquelle aus Profil";
    }
}

