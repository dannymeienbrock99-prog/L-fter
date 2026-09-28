using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
namespace FanAtlas;

public sealed class NvidiaReader : IDisposable
{
    private bool initialized;
    public string Status { get; private set; } = "Noch nicht abgefragt";
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlShutdown();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temperature);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetNumFans(IntPtr device, out uint count);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint speed);
    public List<Measurement> Read()
    {
        var result = new List<Measurement>();
        try
        {
            if (!initialized) { int e = nvmlInit_v2(); if (e != 0) { Status = "NVIDIA-Treiber meldet Fehler " + e; return result; } initialized = true; }
            if (nvmlDeviceGetCount_v2(out var count) != 0) { Status = "GPU-Liste derzeit nicht verfügbar"; return result; }
            for (uint i = 0; i < Math.Min(count, 16); i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != 0) continue;
                var name = new StringBuilder(96); nvmlDeviceGetName(device, name, 96);
                string label = name.Length > 0 ? name.ToString() : "NVIDIA GPU " + (i + 1);
                if (nvmlDeviceGetTemperature(device, 0, out var temp) == 0)
                    result.Add(new($"nvml/{i}/temperature", "GPU-Temperatur", label, "°C", temp, "NVIDIA-Treiber", DateTime.UtcNow));
                try
                {
                    if (nvmlDeviceGetNumFans(device, out var fans) == 0)
                        for (uint f = 0; f < Math.Min(fans, 16); f++)
                            if (nvmlDeviceGetFanSpeed_v2(device, f, out var speed) == 0)
                                result.Add(new($"nvml/{i}/fan/{f}", $"GPU-Lüfter {f + 1} (Treiberwert)", label, "%", speed, "NVIDIA-Treiber", DateTime.UtcNow));
                }
                catch (EntryPointNotFoundException) { /* Older drivers may expose temperature only. */ }
            }
            Status = result.Count > 0 ? $"Verbunden · {result.Count} Messwerte · ausschließlich lesend" : "Keine unterstützten NVIDIA-Messwerte verfügbar";
        }
        catch (DllNotFoundException) { Status = "NVIDIA-Schnittstelle nicht installiert"; }
        catch (Exception e) { Status = "NVIDIA nicht verfügbar: " + e.Message; }
        return result;
    }
    public void Dispose() { if (initialized) { try { nvmlShutdown(); } catch { } initialized = false; } }
}

public record CsvSnapshot(List<Measurement> Measurements, string Signature, DateTime WrittenUtc);
public sealed class CsvReader
{
    private readonly Dictionary<string, (string signature, DateTime updated, bool live)> seen = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Messages { get; } = new();
    public List<Measurement> Read(IEnumerable<string> paths)
    {
        Messages.Clear(); var values = new List<Measurement>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
        {
            try
            {
                var snapshot = ReadSnapshot(path);
                if (snapshot.Measurements.Count == 0) { Messages.Add(Path.GetFileName(path) + ": Noch keine vollständigen Temperatur-/Lüfterdaten erkannt."); continue; }
                bool live = false; DateTime updated = snapshot.WrittenUtc;
                if (seen.TryGetValue(path, out var old))
                {
                    live = old.live || old.signature != snapshot.Signature;
                    updated = old.signature == snapshot.Signature ? old.updated : snapshot.WrittenUtc;
                }
                seen[path] = (snapshot.Signature, updated, live);
                values.AddRange(snapshot.Measurements.Select(m => m with { Live = live, UpdatedUtc = updated }));
                Messages.Add(Path.GetFileName(path) + (live ? " · Protokoll wird mitgelesen" : " · Warte auf nächste vollständige Zeile"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { Messages.Add(Path.GetFileName(path) + ": " + e.Message); }
        }
        return values;
    }
    public static CsvSnapshot ReadSnapshot(string path)
    {
        // Shared reads allow the originating monitor to continue writing and rotating its log.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Protokolldatei größer als 4 GB. Bitte eine neue Protokolldatei wählen.");
        var first = new byte[(int)Math.Min(file.Length, 65536)]; file.ReadExactly(first);
        Encoding encoding = first.Length >= 2 && first[0] == 0xff && first[1] == 0xfe ? Encoding.Unicode
            : first.Length >= 2 && first[0] == 0xfe && first[1] == 0xff ? Encoding.BigEndianUnicode : Encoding.UTF8;
        string head = encoding.GetString(first).TrimStart('\ufeff');
        long start = Math.Max(0, file.Length - 262144);
        if (encoding == Encoding.Unicode || encoding == Encoding.BigEndianUnicode) start -= start % 2;
        file.Position = start; var tailBytes = new byte[(int)(file.Length - start)]; file.ReadExactly(tailBytes);
        string tail = encoding.GetString(tailBytes);
        if (start > 0) { int newline = tail.IndexOf('\n'); tail = newline >= 0 ? tail[(newline + 1)..] : ""; }
        // Ignore an unfinished final record while the producer is writing it.
        int complete = tail.LastIndexOf('\n'); tail = complete >= 0 ? tail[..(complete + 1)] : "";
        return Parse(head, tail, path, File.GetLastWriteTimeUtc(path));
    }
    public static CsvSnapshot Parse(string head, string tail, string path, DateTime written)
    {
        var headLines = head.Split('\n').Take(60).Select(s => s.TrimEnd('\r')).ToArray();
        string[]? headers = null; char delimiter = ','; int best = 0;
        foreach (char d in new[] { ';', ',', '\t' })
            foreach (var line in headLines)
            {
                var cols = Split(line, d); int units = cols.Count(s => Unit(s) != null && !TryNumber(s, out _));
                int score = units == 0 ? 0 : units * 1000 + Math.Min(999, cols.Length);
                if (score > best) { best = score; headers = cols; delimiter = d; }
            }
        if (headers == null) return new(new(), "", written);
        string[]? row = null; string signature = "";
        foreach (var line in tail.Split('\n').Reverse())
        {
            var cols = Split(line.TrimEnd('\r'), delimiter);
            if (cols.Length != headers.Length) continue;
            if (!cols.Where((v, i) => Unit(headers[i]) != null).Any(v => TryNumber(v, out _))) continue;
            row = cols; signature = line; break;
        }
        var result = new List<Measurement>(); if (row == null) return new(result, signature, written);
        for (int i = 0; i < row.Length; i++)
        {
            string? unit = Unit(headers[i]); if (unit == null || !TryNumber(row[i], out double value)) continue;
            if (unit == "°F") { value = (value - 32) * 5 / 9; unit = "°C"; }
            if (unit == "°C" && (value < -100 || value > 250) || unit == "RPM" && (value < 0 || value > 100000) || unit == "%" && (value < 0 || value > 200)) continue;
            result.Add(new("csv/" + Path.GetFullPath(path).ToUpperInvariant() + "/" + i, headers[i].Trim(), Path.GetFileNameWithoutExtension(path), unit, value, "CSV · " + Path.GetFileName(path), written, false));
        }
        return new(result, signature, written);
    }
    public static string[] Split(string text, char delimiter)
    {
        var result = new List<string>(); var current = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == delimiter && !quoted) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        result.Add(current.ToString()); return result.ToArray();
    }
    public static bool TryNumber(string text, out double value)
    {
        text = text.Trim().Replace("\u00a0", "");
        if (!text.Contains('.') && text.Count(c => c == ',') == 1) text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
    public static string? Unit(string text)
    {
        string s = text.ToLowerInvariant();
        if (s.Contains("rpm") || s.Contains("u/min")) return "RPM";
        if (s.Contains("°f") || s.Contains("[f]")) return "°F";
        if (s.Contains("°c") || s.Contains("℃") || s.Contains("[c]") || s.Contains("temperature") || s.Contains("temperatur") || Regex.IsMatch(s, @"\btemp\b")) return "°C";
        if (s.Contains('%') && (s.Contains("fan") || s.Contains("lüfter") || s.Contains("pump") || s.Contains("pwm"))) return "%";
        if ((s.Contains("fan") || s.Contains("lüfter") || s.Contains("pump")) && !s.Contains('%')) return "RPM";
        return null;
    }
}

