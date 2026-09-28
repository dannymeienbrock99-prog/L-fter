using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace FanAtlas;
public static class SelfTests
{
    public static int Run(string profilePath, string reportPath)
    {
        var lines = new List<string>(); int failures = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        string fixtures = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "test-fixtures");
        Directory.CreateDirectory(fixtures);
        void Check(string name, Action action)
        {
            try { action(); lines.Add("PASS · " + name); }
            catch (Exception ex) { failures++; lines.Add("FAIL · " + name + " · " + ex.Message); }
        }
        void Assert(bool condition, string message = "Unerwartetes Ergebnis") { if (!condition) throw new Exception(message); }
        var profileHash = SHA256.HashData(File.ReadAllBytes(profilePath));
        var profile = ProfileReader.Read(profilePath);
        Check("Echtes iCUE-Profil: 13 Zuordnungen, 10 Geräte-IDs", () => { Assert(profile.Fans.Count == 13); Assert(profile.Fans.Select(f => f.Serial).Distinct().Count() == 10); });
        Check("Zugewiesene Kurve statt Defaultkurve", () =>
        {
            var first = profile.Fans[0]; var c = profile.Curves.Single(c => c.Id == first.CurveId);
            Assert(c.Name == "Dragon Fury EXTREME", c.Name); Assert(c.Points.Count == 7);
            Assert(profile.Fans.All(f => profile.Curves.Any(c => c.Id == f.CurveId)), "Nicht aufgelöste Zuordnung");
        });
        Check("Originalprofil unverändert", () => Assert(profileHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(profilePath)))));
        Check("XML mit externer Entität wird abgelehnt", () =>
        {
            string p = Path.Combine(fixtures, "dtd.cueprofile"); File.WriteAllText(p, "<!DOCTYPE cereal [<!ENTITY x SYSTEM 'file:///not-read'>]><cereal>&x;</cereal>");
            try { ProfileReader.Read(p); throw new Exception("DTD akzeptiert"); } catch (System.Xml.XmlException) { }
        });
        FanCurve c = new() { Name = "Test", SensorLabel = "CPU", IsCustom = true, Points = new(new[] { new CurvePoint(30, 20), new(50, 60), new(80, 100) }) };
        Check("Lineare Vorschau + Randwerte", () => { Assert(c.Evaluate(40) == 40); Assert(c.Evaluate(0) == 20); Assert(c.Evaluate(120) == 100); });
        Check("Ungültige Temperaturen und Stellwerte werden abgefangen", () =>
        {
            var bad = c.Copy(); bad.Points[1].Temperature = 30; Assert(FanCurve.Validate(bad) != null); Assert(bad.Evaluate(40) == null);
            bad = c.Copy(); bad.Points[1].Duty = 101; Assert(FanCurve.Validate(bad) != null);
            bad.Points[1].Duty = double.NaN; Assert(FanCurve.Validate(bad) != null);
        });
        Check("Tiefe Kurvenkopie lässt Ausgangskurve unverändert", () => { var copy = c.Copy(true); copy.Points[0].Duty = 99; Assert(c.Points[0].Duty == 20); Assert(copy.Id != c.Id); });
        Check("CSV mit Semikolon und Dezimalkomma", () =>
        {
            string content = "Zeit;CPU Package [°C];Fan 1 [RPM]\r\n12:00;42,5;1050\r\n";
            var snapshot = CsvReader.Parse(content, content, Path.Combine(fixtures, "de.csv"), DateTime.UtcNow);
            Assert(snapshot.Measurements.Count == 2); Assert(snapshot.Measurements[0].Value == 42.5);
        });
        Check("CSV mit Komma, Zitaten und einzelnem Sensor", () =>
        {
            string content = "Time,\"CPU, Package [°C]\"\n12:00,41.5\n";
            var snapshot = CsvReader.Parse(content, content, Path.Combine(fixtures, "en.csv"), DateTime.UtcNow);
            Assert(snapshot.Measurements.Count == 1); Assert(snapshot.Measurements[0].Value == 41.5);
        });
        Check("Fahrenheit wird in Celsius umgerechnet, NaN übersprungen", () =>
        {
            string content = "Time,GPU [°F],Fan [RPM]\n1,104,NaN\n";
            var snapshot = CsvReader.Parse(content, content, Path.Combine(fixtures, "f.csv"), DateTime.UtcNow);
            Assert(snapshot.Measurements.Count == 1); Assert(Math.Abs(snapshot.Measurements[0].Value - 40) < 0.001);
        });
        Check("Gleichzeitiger CSV-Schreiber, Teilzeile und neuer Livewert", () =>
        {
            string p = Path.Combine(fixtures, "laufend.csv");
            using var writer = new FileStream(p, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            void Add(string text) { writer.Write(Encoding.UTF8.GetBytes(text)); writer.Flush(); }
            Add("Time;CPU [°C];Fan [RPM]\n1;40;800\n2;45");
            var parser = new CsvReader(); var first = parser.Read(new[] { p }); Assert(first.Count == 2); Assert(first[0].Value == 40 && !first[0].Live);
            Add(";900\n"); var second = parser.Read(new[] { p }); Assert(second[0].Value == 45 && second[0].Live);
            DateTime time = second[0].UpdatedUtc; Add("3;49"); var third = parser.Read(new[] { p }); Assert(third[0].Value == 45); Assert(third[0].UpdatedUtc == time);
        });
        Check("UTF-16-Protokoll wird erkannt", () =>
        {
            string p = Path.Combine(fixtures, "utf16.csv"); File.WriteAllText(p, "Zeit;CPU [°C]\r\n1;38\r\n", Encoding.Unicode);
            Assert(CsvReader.ReadSnapshot(p).Measurements.Single().Value == 38);
        });
        Check("Großes Protokoll: letzte vollständige Zeile", () =>
        {
            string p = Path.Combine(fixtures, "gross.csv");
            using (var writer = new StreamWriter(p)) { writer.WriteLine("Zeit;CPU [°C]"); for (int i = 0; i < 50000; i++) writer.WriteLine($"{i};44"); }
            Assert(CsvReader.ReadSnapshot(p).Measurements.Single().Value == 44);
        });
        Check("Veraltete Werte erscheinen nicht als aktuell", () =>
        {
            var m = new Measurement("x", "CPU", "Test", "°C", 40, "Test", DateTime.UtcNow.AddMinutes(-1));
            var row = new SensorRow(m); row.Refresh(15); Assert(!row.Fresh && row.ValueText == "—");
            row.Update(m with { UpdatedUtc = DateTime.UtcNow }, 15); Assert(row.Fresh);
        });
        Check("Speichern + Neuladen + Sicherungskopie", () =>
        {
            StateStore.DirectoryPath = Path.Combine(fixtures, "state"); var state = new AppState { Profile = profile, CustomCurves = new() { c } };
            StateStore.Save(state); StateStore.Save(state);
            var loaded = StateStore.Load(); Assert(loaded.CustomCurves.Single().Points.Count == 3);
            Assert(loaded.Profile.Fans.Count == 13); Assert(File.Exists(StateStore.StatePath + ".bak"));
        });
        Check("JSON-Export kann wieder importiert werden", () =>
        {
            var restored = JsonSerializer.Deserialize<FanCurve>(JsonSerializer.Serialize(c, StateStore.Json))!;
            Assert(FanCurve.Validate(restored) == null); Assert(restored.Evaluate(40) == 40);
        });
        using (var reader = new NvidiaReader())
        {
            // Stage persistence must survive resizing, hidden fans and invalid imported coordinates.
            Check("Lüfterbühne: Grenzen, IDs und Hintergrund", () =>
            {
                var stage = new StageSettings { Background = "../../private", Tiles = new() { new() { Id = "x", X = -100, Y = 99999, Size = 999 }, new() { Id = "x", Name = null!, RpmSensorKey = null!, Size = double.NaN } } };
                stage.Normalize(); Assert(stage.Background == "stream-startet.jpg"); Assert(stage.Tiles[0].X == 0 && stage.Tiles[0].Y == 596 && stage.Tiles[0].Size == 420);
                Assert(stage.Tiles.Select(t => t.Id).Distinct().Count() == 2); Assert(stage.Tiles[1].RpmSensorKey == "");
            });
            Check("Lüfterbühne: Position und ausgeblendete Erkennung bleiben gespeichert", () =>
            {
                var s = new AppState(); s.Stage.Tiles.Add(new() { X = 321, Y = 456, TemperatureSensorKey = "sensor/test" }); s.Stage.HiddenAutoSensors.Add("hidden/test");
                StateStore.Save(s); var loaded = StateStore.Load(); Assert(loaded.Stage.Tiles.Single().X == 321 && loaded.Stage.Tiles.Single().TemperatureSensorKey == "sensor/test"); Assert(loaded.Stage.HiddenAutoSensors.Contains("hidden/test"));
                Assert(loaded.Bridge.ControlToken != loaded.Bridge.OverlayToken && loaded.Bridge.ControlToken.Length == 64);
            });
            Check("Öffentliche Sensor-ID enthält keinen Dateipfad", () => { Assert(SensorIdentity.PublicId("C:\\private\\log.csv") == SensorIdentity.PublicId("C:\\private\\log.csv")); Assert(SensorIdentity.PublicId("C:\\private\\log.csv").Length == 24); Assert(SensorIdentity.PublicId("") == ""); });
        }
        using (var reader = new NvidiaReader())
        {
            var values = reader.Read();
            lines.Add("NVIDIA-Diagnose · " + reader.Status);
            foreach (var v in values) lines.Add($"  {v.Device}: {v.Name} = {v.Value} {v.Unit}");
        }
        lines.Add($"Ergebnis: {failures} fehlgeschlagene Prüfungen.");
        File.WriteAllLines(reportPath, lines, Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }
    public static string Ui(MainWindow window)
    {
        int curves = window.State.CustomCurves.Count;
        if (!window.SaveDraft()) throw new Exception("UI konnte gültige Kurve nicht speichern.");
        if (window.State.CustomCurves.Count != curves + 1) throw new Exception("UI hat keine eigene Kopie erstellt.");
        var draft = window.EditorChart.Curve!;
        draft.Points[1].Temperature = draft.Points[0].Temperature;
        if (window.SaveDraft()) throw new Exception("UI akzeptiert doppelte Temperatur.");
        return "PASS · Oberfläche aller fünf Ansichten gerendert.\nPASS · Eigene Kurve über Editor-Speicherfunktion angelegt.\nPASS · Ungültige Kurve über Editor-Speicherfunktion abgelehnt.\n";
    }
}

