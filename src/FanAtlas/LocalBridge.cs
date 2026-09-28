using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FanAtlas;

public sealed class LocalBridge : IAsyncDisposable
{
    private WebApplication? server;
    private readonly BridgeSettings settings;
    private readonly Func<string, Task<(bool Ok, string Message)>> selectCurve;
    private string snapshot = "{}";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string OverlayUrl => $"http://127.0.0.1:{settings.Port}/overlay?token={settings.OverlayToken}";
    public string Status { get; private set; } = "Noch nicht gestartet";
    public LocalBridge(BridgeSettings settings, Func<string, Task<(bool, string)>> selectCurve) { this.settings = settings; this.selectCurve = selectCurve; }
    public void Publish(BridgeSnapshot data) => Volatile.Write(ref snapshot, JsonSerializer.Serialize(data, Json));
    private static bool Equal(string? supplied, string expected) => supplied != null && supplied.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    public async Task Start()
    {
        try
        {
            settings.Port = Math.Clamp(settings.Port, 1024, 65535);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, settings.Port); k.Limits.MaxRequestBodySize = 16384; });
            server = builder.Build();
            server.Use(async (context, next) =>
            {
                if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
                var origin = context.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && origin != $"http://127.0.0.1:{settings.Port}") { context.Response.StatusCode = 403; return; }
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await next(context);
            });
            bool OverlayAuth(HttpContext c) => Equal(c.Request.Query["token"], settings.OverlayToken);
            bool ControlAuth(HttpContext c) => Equal(c.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal), settings.ControlToken);
            server.MapGet("/overlay", async c =>
            {
                if (!OverlayAuth(c)) { c.Response.StatusCode = 401; return; }
                c.Response.ContentType = "text/html; charset=utf-8";
                c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors *";
                await c.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "Web", "overlay.html"));
            });
            server.MapGet("/api/overlay", async c =>
            {
                if (!OverlayAuth(c)) { c.Response.StatusCode = 401; return; }
                using var doc = JsonDocument.Parse(Volatile.Read(ref snapshot));
                // The stream receives display information, never local paths or control credentials.
                if (!doc.RootElement.TryGetProperty("scene", out var scene)) { c.Response.StatusCode = 503; return; }
                c.Response.ContentType = "application/json";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { generatedUtc = doc.RootElement.GetProperty("generatedUtc"), sensors = doc.RootElement.GetProperty("sensors"), scene }, Json));
            });
            server.MapGet("/api/state", async c =>
            {
                if (!ControlAuth(c)) { c.Response.StatusCode = 401; return; }
                c.Response.ContentType = "application/json"; await c.Response.WriteAsync(Volatile.Read(ref snapshot));
            });
            server.MapPost("/api/select-curve", async c =>
            {
                if (!ControlAuth(c)) { c.Response.StatusCode = 401; return; }
                if (c.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) { c.Response.StatusCode = 415; return; }
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(c.Request.Body);
                    if (body == null || !body.TryGetValue("curveId", out var id) || string.IsNullOrEmpty(id) || id.Length > 100) { c.Response.StatusCode = 400; return; }
                    var result = await selectCurve(id);
                    c.Response.StatusCode = result.Ok ? 200 : 409;
                    await c.Response.WriteAsJsonAsync(new { ok = result.Ok, message = result.Message, hardwareApplied = false });
                }
                catch (JsonException) { c.Response.StatusCode = 400; }
            });
            foreach (var name in new[] { "overlay.js", "overlay.css" })
            {
                string file = name;
                server.MapGet("/" + file, async c => { c.Response.ContentType = file.EndsWith(".js") ? "application/javascript" : "text/css"; await c.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "Web", file)); });
            }
            foreach (var name in new[] { "fan.png", "fan.webp", "stream-startet.jpg", "bin-gleich-zurueck.jpg", "crazy-batto.png" })
            {
                string file = name;
                server.MapGet("/assets/" + file, async c => { c.Response.ContentType = file.EndsWith(".jpg") ? "image/jpeg" : file.EndsWith(".webp") ? "image/webp" : "image/png"; await c.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "Assets", file)); });
            }
            await server.StartAsync();
            Directory.CreateDirectory(StateStore.DirectoryPath);
            string descriptor = Path.Combine(StateStore.DirectoryPath, "bridge.json");
            File.WriteAllText(descriptor + ".tmp", JsonSerializer.Serialize(new { port = settings.Port, token = settings.ControlToken, pid = Environment.ProcessId, protocol = 1 }));
            File.Move(descriptor + ".tmp", descriptor, true);
            Status = $"Stream bereit · lokal auf Port {settings.Port}";
        }
        catch (Exception e) { Status = "Stream-Verbindung nicht gestartet: " + e.Message; if (server != null) await server.DisposeAsync(); server = null; }
    }
    public async ValueTask DisposeAsync() { if (server != null) { await server.StopAsync(); await server.DisposeAsync(); } }
}

