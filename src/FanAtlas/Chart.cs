using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FanAtlas;

public sealed class CurveChart : FrameworkElement
{
    private FanCurve? curve;
    private int dragging = -1;
    public bool Editable { get; set; }
    public string EmptyText { get; set; } = "Keine Kurvenpunkte verfügbar";
    public FanCurve? Comparison { get; set; }
    public double? MarkerTemperature { get; set; }
    public event Action? Edited;
    public FanCurve? Curve
    {
        get => curve;
        set
        {
            if (curve != null) { curve.Points.CollectionChanged -= PointsChanged; foreach (var p in curve.Points) p.PropertyChanged -= PointChanged; }
            curve = value;
            if (curve != null) { curve.Points.CollectionChanged += PointsChanged; foreach (var p in curve.Points) p.PropertyChanged += PointChanged; }
            InvalidateVisual();
        }
    }
    private void PointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (CurvePoint p in e.OldItems) p.PropertyChanged -= PointChanged;
        if (e.NewItems != null) foreach (CurvePoint p in e.NewItems) p.PropertyChanged += PointChanged;
        InvalidateVisual(); Edited?.Invoke();
    }
    private void PointChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { InvalidateVisual(); Edited?.Invoke(); }
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
    private static readonly Brush Text = Brush("#A6B5C8"), GridLine = Brush("#263448"), Teal = Brush("#52E4BD"), Purple = Brush("#A69CFF");
    private Rect Plot => new(46, 28, Math.Max(1, ActualWidth - 72), Math.Max(1, ActualHeight - 70));
    private double XMax => Math.Clamp(Math.Ceiling((curve?.Points.Where(p => double.IsFinite(p.Temperature)).Select(p => p.Temperature).DefaultIfEmpty(100).Max() ?? 100) / 20) * 20, 100, 120);
    private Point XY(CurvePoint p) => new(Plot.Left + p.Temperature / XMax * Plot.Width, Plot.Bottom - p.Duty / 100 * Plot.Height);
    private void Label(DrawingContext dc, string value, double x, double y, Brush? brush = null, double size = 11)
        => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush ?? Text, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brush("#101B29"), null, new(0, 0, ActualWidth, ActualHeight));
        var r = Plot; Label(dc, "Lüfterleistung (%)", r.Left, 4);
        for (int v = 0; v <= 100; v += 20) { double y = r.Bottom - v / 100d * r.Height; dc.DrawLine(new(GridLine, 1), new(r.Left, y), new(r.Right, y)); Label(dc, v.ToString(), 12, y - 8); }
        for (int t = 0; t <= XMax; t += 20) { double x = r.Left + t / XMax * r.Width; dc.DrawLine(new(GridLine, 1), new(x, r.Top), new(x, r.Bottom)); Label(dc, t.ToString(), x - 6, r.Bottom + 8); }
        Label(dc, "Temperatur (°C)", r.Right - 85, r.Bottom + 25);
        if (Comparison?.Points.Count > 1) DrawCurve(dc, Comparison, Purple, false);
        if (curve?.Points.Count > 1) DrawCurve(dc, curve, Teal, true);
        else Label(dc, EmptyText, r.Left + 18, r.Top + r.Height / 2, Text, 13);
        if (MarkerTemperature is double m && curve?.Evaluate(m) is double duty)
        {
            var p = XY(new(m, duty)); dc.DrawLine(new(Purple, 1) { DashStyle = DashStyles.Dash }, new(p.X, r.Top), new(p.X, r.Bottom));
            dc.DrawEllipse(Purple, new(Brush("#FFFFFF"), 2), p, 5, 5);
        }
    }
    private void DrawCurve(DrawingContext dc, FanCurve c, Brush color, bool points)
    {
        var coords = c.Points.Where(p => double.IsFinite(p.Temperature) && double.IsFinite(p.Duty)).Select(XY).ToArray();
        if (coords.Length < 2) return;
        dc.PushClip(new RectangleGeometry(Plot));
        var g = new StreamGeometry(); using (var ctx = g.Open()) { ctx.BeginFigure(coords[0], false, false); ctx.PolyLineTo(coords.Skip(1).ToArray(), true, true); }
        dc.DrawGeometry(null, new(color, points ? 2.7 : 1.8) { DashStyle = points ? DashStyles.Solid : DashStyles.Dash }, g);
        if (points) foreach (var p in coords) dc.DrawEllipse(Brush("#101B29"), new(color, 2), p, Editable ? 5.5 : 3, Editable ? 5.5 : 3);
        dc.Pop();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Editable || curve == null) return;
        var pos = e.GetPosition(this);
        for (int i = 0; i < curve.Points.Count; i++) if ((pos - XY(curve.Points[i])).Length < 15) { dragging = i; CaptureMouse(); e.Handled = true; return; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging < 0 || curve == null) return;
        var p = e.GetPosition(this); var r = Plot;
        double low = dragging == 0 ? 0 : curve.Points[dragging - 1].Temperature + 1;
        double high = dragging == curve.Points.Count - 1 ? Math.Min(XMax, 120) : curve.Points[dragging + 1].Temperature - 1;
        if (high < low) return;
        curve.Points[dragging].Temperature = Math.Clamp(Math.Round((p.X - r.Left) / r.Width * XMax), low, high);
        curve.Points[dragging].Duty = Math.Clamp(Math.Round((r.Bottom - p.Y) / r.Height * 100), 0, 100);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { dragging = -1; ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { dragging = -1; }
}

public sealed class HistoryChart : FrameworkElement
{
    public List<(DateTime Time, double Value)> Samples { get; set; } = new();
    public string Unit { get; set; } = "°C";
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var brush = new SolidColorBrush(Color.FromRgb(82, 228, 189));
        var muted = new SolidColorBrush(Color.FromRgb(166, 181, 200));
        void Text(string s, double x, double y) => dc.DrawText(new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        double w = Math.Max(1, ActualWidth - 78), h = Math.Max(1, ActualHeight - 40);
        var now = DateTime.UtcNow; var start = now.AddMinutes(-10);
        var samples = Samples.Where(s => s.Time >= start).ToArray();
        if (samples.Length < 2) { Text("Sobald Messwerte eintreffen, erscheint hier ihr Verlauf.", 24, ActualHeight / 2); return; }
        double min = Math.Floor(samples.Min(s => s.Value) / 10) * 10;
        double max = Math.Max(min + 10, Math.Ceiling(samples.Max(s => s.Value) / 10) * 10);
        for (int i = 0; i <= 4; i++) { double y = 8 + h * i / 4; dc.DrawLine(new(new SolidColorBrush(Color.FromRgb(38, 52, 72)), 1), new(48, y), new(48 + w, y)); Text($"{max - (max - min) * i / 4:0.#}", 1, y - 7); }
        for (int i = 1; i < samples.Length; i++)
        {
            var a = samples[i - 1]; var b = samples[i]; if ((b.Time - a.Time).TotalSeconds > 15) continue;
            Point Pos((DateTime Time, double Value) p) => new(48 + (p.Time - start).TotalSeconds / 600 * w, 8 + h - (p.Value - min) / (max - min) * h);
            dc.DrawLine(new(brush, 2), Pos(a), Pos(b));
        }
        Text("−10 min", 48, h + 20); Text("Jetzt · " + Unit, w - 10, h + 20);
    }
}

