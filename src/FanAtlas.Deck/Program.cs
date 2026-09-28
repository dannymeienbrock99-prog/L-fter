using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace FanAtlas.Deck;
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render-test") { KeyImage.Draw("GPU-Lüfter 1", "31°", "0 %", "Live", false).Save(args[1], ImageFormat.Png); return 0; }
        try
        {
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
            if (!int.TryParse(Arg("-port"), out int port) || port < 1024 || port > 65535 || Arg("-pluginUUID").Length == 0 || Arg("-registerEvent") != "registerPlugin") return 2;
            using var plugin = new DeckPlugin();
            await plugin.Run(port, Arg("-pluginUUID"), Arg("-registerEvent")); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FanAtlas Stream Deck: " + e.Message); return 1; }
    }
}
public sealed class DeckPlugin : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<string, ActionState> actions = new();
    private readonly CancellationTokenSource lifetime = new();
    private JsonObject? snapshot;
    private readonly string descriptorPath = Environment.GetEnvironmentVariable("FANATLAS_TEST_BRIDGE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrazyBatto", "FanAtlas", "bridge.json");
    private string address = "", token = "";
    private sealed class ActionState
    {
        public string Action { get; set; } = "";
        public JsonObject Settings { get; set; } = new();
        public string LastImageHash { get; set; } = "";
    }
    public async Task Run(int port, string uuid, string registration)
    {
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), lifetime.Token);
        await Send(new { @event = registration, uuid });
        var poller = PollLoop();
        var buffer = new byte[65536];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult read;
                do { read = await socket.ReceiveAsync(buffer, lifetime.Token); if (read.MessageType == WebSocketMessageType.Close) return; message.Write(buffer, 0, read.Count); if (message.Length > 1_000_000) throw new InvalidDataException("Stream-Deck-Nachricht zu groß."); } while (!read.EndOfMessage);
                if (JsonNode.Parse(message.ToArray()) is JsonObject data) await Handle(data);
            }
        }
        finally { lifetime.Cancel(); try { await poller; } catch (OperationCanceledException) { } }
    }
    private async Task Send(object data)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data));
        await sendLock.WaitAsync(lifetime.Token);
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token); }
        finally { sendLock.Release(); }
    }
    private async Task Handle(JsonObject e)
    {
        string evt = e["event"]?.GetValue<string>() ?? "", context = e["context"]?.GetValue<string>() ?? "";
        string action = e["action"]?.GetValue<string>() ?? "";
        var payload = e["payload"] as JsonObject ?? new();
        if (evt is "willAppear" or "didReceiveSettings")
        {
            actions.AddOrUpdate(context, _ => new() { Action = action, Settings = (payload["settings"]?.DeepClone() as JsonObject) ?? new() },
                (_, previous) => { previous.Settings = (payload["settings"]?.DeepClone() as JsonObject) ?? new(); previous.LastImageHash = ""; return previous; });
            await Render(context);
        }
        if (evt == "willDisappear") actions.TryRemove(context, out _);
        if (evt is "propertyInspectorDidAppear" or "sendToPlugin")
        {
            if (evt == "sendToPlugin" && payload["command"]?.GetValue<string>() != "catalog") return;
            await RefreshSnapshot();
            await Send(new { @event = "sendToPropertyInspector", action, context, payload = new { online = Online, catalog = snapshot?.DeepClone() } });
        }
        if (evt == "keyDown" && actions.TryGetValue(context, out var state))
        {
            if (state.Action.EndsWith(".fan"))
            {
                var next = (JsonObject)state.Settings.DeepClone(); next["mode"] = next["mode"]?.GetValue<string>() == "rpm" ? "temperature" : "rpm"; state.Settings = next;
                await Send(new { @event = "setSettings", context, payload = state.Settings }); state.LastImageHash = ""; await Render(context);
            }
            else if (state.Action.EndsWith(".curve"))
            {
                string id = state.Settings["curveId"]?.GetValue<string>() ?? "";
                bool ok = false;
                try
                {
                    if (id.Length > 0 && Online)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, address + "/api/select-curve");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { curveId = id }), Encoding.UTF8, "application/json");
                        using var response = await http.SendAsync(request, lifetime.Token); ok = response.IsSuccessStatusCode;
                    }
                }
                catch (Exception) when (!lifetime.IsCancellationRequested) { }
                await Send(new { @event = ok ? "showOk" : "showAlert", context });
                await RefreshSnapshot(); await Render(context);
            }
        }
    }
    private bool Online => snapshot != null && DateTime.TryParse(snapshot["generatedUtc"]?.GetValue<string>(), out var dt) && DateTime.UtcNow - dt.ToUniversalTime() < TimeSpan.FromSeconds(10);
    private async Task RefreshSnapshot()
    {
        try
        {
            using var file = new FileStream(descriptorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 8192) { snapshot = null; return; }
            var descriptor = await JsonNode.ParseAsync(file, cancellationToken: lifetime.Token);
            int port = descriptor?["port"]?.GetValue<int>() ?? 0;
            string secret = descriptor?["token"]?.GetValue<string>() ?? "";
            if (port < 1024 || port > 65535 || secret.Length != 64) { snapshot = null; return; }
            address = $"http://127.0.0.1:{port}"; token = secret;
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/api/state"); request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request, lifetime.Token);
            if (!response.IsSuccessStatusCode) { snapshot = null; return; }
            string text = await response.Content.ReadAsStringAsync(lifetime.Token);
            if (text.Length > 2_000_000) { snapshot = null; return; }
            snapshot = JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception) when (!lifetime.IsCancellationRequested) { snapshot = null; }
    }
    private async Task PollLoop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            await RefreshSnapshot();
            foreach (string id in actions.Keys) await Render(id);
            await Task.Delay(2000, lifetime.Token);
        }
    }
    private async Task Render(string context)
    {
        if (!actions.TryGetValue(context, out var action)) return;
        string name = "Lüfter wählen", main = "—", bottom = "", status = Online ? "Einrichten" : "FanAtlas offline";
        if (action.Action.EndsWith(".fan"))
        {
            string tileId = action.Settings["tileId"]?.GetValue<string>() ?? "";
            var tile = (snapshot?["scene"]?["tiles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t["id"]?.GetValue<string>() == tileId);
            if (tile != null)
            {
                name = tile["name"]?.GetValue<string>() ?? "Lüfter";
                var sensors = (snapshot?["sensors"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
                var temp = sensors.FirstOrDefault(s => s["id"]?.GetValue<string>() == tile["temperatureSensorId"]?.GetValue<string>());
                var rpm = sensors.FirstOrDefault(s => s["id"]?.GetValue<string>() == tile["rpmSensorId"]?.GetValue<string>());
                string Value(JsonObject? s, bool temperature) => Online && s?["fresh"]?.GetValue<bool>() == true && s["value"] != null ? s["value"]!.GetValue<double>().ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + (temperature ? "°" : " " + s["unit"]?.GetValue<string>()) : "—";
                bool rpmMode = action.Settings["mode"]?.GetValue<string>() == "rpm";
                main = Value(rpmMode ? rpm : temp, !rpmMode); bottom = Value(rpmMode ? temp : rpm, rpmMode);
                status = !Online ? "FanAtlas offline" : rpm?["fresh"]?.GetValue<bool>() == true ? "Live" : "Quelle fehlt";
            }
        }
        else if (action.Action.EndsWith(".curve"))
        {
            string id = action.Settings["curveId"]?.GetValue<string>() ?? "";
            var curve = (snapshot?["curves"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["id"]?.GetValue<string>() == id);
            name = curve?["name"]?.GetValue<string>() ?? "Kurve wählen"; main = "KURVE"; bottom = "Entwurf";
            status = !Online ? "FanAtlas offline" : snapshot?["selectedCurveId"]?.GetValue<string>() == id ? "Ausgewählt" : "Öffnen";
        }
        using var bitmap = KeyImage.Draw(name, main, bottom, status, status is "FanAtlas offline" or "Quelle fehlt");
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); byte[] bytes = stream.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)); if (hash == action.LastImageHash) return; action.LastImageHash = hash;
        await Send(new { @event = "setImage", context, payload = new { image = "data:image/png;base64," + Convert.ToBase64String(bytes), target = 0 } });
        await Send(new { @event = "setTitle", context, payload = new { title = "", target = 0 } });
    }
    public void Dispose() { lifetime.Cancel(); socket.Dispose(); http.Dispose(); sendLock.Dispose(); lifetime.Dispose(); }
}
public static class KeyImage
{
    public static Bitmap Draw(string name, string main, string bottom, string state, bool stale)
    {
        var bmp = new Bitmap(144, 144); using var g = Graphics.FromImage(bmp); g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(7, 19, 35));
        string imagePath = Path.Combine(AppContext.BaseDirectory, "fan.png");
        if (File.Exists(imagePath)) { using var img = Image.FromFile(imagePath); g.DrawImage(img, new Rectangle(9, 14, 126, 112)); }
        using var shade = new SolidBrush(Color.FromArgb(235, 7, 19, 35)); g.FillEllipse(shade, 35, 40, 74, 66);
        using var outline = new Pen(stale ? Color.Gray : Color.Cyan, 2); g.DrawEllipse(outline, 35, 40, 74, 66);
        void Text(string text, int size, FontStyle style, Color color, RectangleF box)
        { using var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel); using var brush = new SolidBrush(color); using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }; g.FillRectangle(shade, box); g.DrawString(text, font, brush, box, format); }
        Text(name, 13, FontStyle.Bold, Color.White, new(0, 0, 144, 22));
        // Center text is drawn without the rectangular backdrop, preserving the circular fan hub.
        using (var font = new Font("Segoe UI", main.Length > 5 ? 16 : 23, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.White))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(main, font, brush, new RectangleF(35, 40, 74, 66), format);
        Text(bottom, 14, FontStyle.Bold, Color.Cyan, new(0, 112, 144, 16));
        Text(state, 11, FontStyle.Regular, stale ? Color.Goldenrod : Color.LightGreen, new(0, 128, 144, 16));
        return bmp;
    }
}

