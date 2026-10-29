using System.Globalization;
using ScottPlot;

namespace Budgeter;

/// <summary>Draws chart images on this machine (no chart service ever sees the data).</summary>
public static class Charts
{
    public const int Width = 900, Height = 600;
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    // A bare Linux server has no font that Skia can find on its own; install.sh adds DejaVu, so use it when present.
    private const string LinuxFont = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    private static readonly string? FontName = LoadFont();

    private static string? LoadFont()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(LinuxFont)) return null;
        Fonts.AddFontFile("DejaVu Sans", LinuxFont);
        return "DejaVu Sans";
    }

    private static Plot NewPlot(string title)
    {
        var plot = new Plot();
        if (FontName is not null) plot.Font.Set(FontName);
        plot.Title(title);
        return plot;
    }

    /// <summary>This month: spent per category against its limit (red where over).</summary>
    public static byte[] ByCategory(IReadOnlyList<CategoryStatus> rows, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var sorted = rows.Where(r => r.Limit > 0 || r.Spent > 0).OrderBy(r => r.Spent).ToList();
        var plot = NewPlot($"{local:MMMM} so far (day {local.Day}): spent vs limit");

        var bars = new List<Bar>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            bars.Add(new Bar { Position = i, Value = (double)r.Limit, FillColor = Colors.LightGray, Size = 0.8 });
            bars.Add(new Bar
            {
                Position = i, Value = (double)r.Spent, Size = 0.45,
                FillColor = r.Left < 0 ? Colors.Crimson : Colors.SteelBlue,
                Label = r.Spent.ToString("C0", Usd),
            });
        }
        var series = plot.Add.Bars(bars);
        series.Horizontal = true;
        series.ValueLabelStyle.FontSize = 14;

        plot.Axes.Left.SetTicks(
            Enumerable.Range(0, sorted.Count).Select(i => (double)i).ToArray(),
            sorted.Select(r => r.Category).ToArray());
        plot.Axes.Margins(left: 0, right: 0.15);
        plot.XLabel("Gray = monthly limit, blue = spent, red = over");
        return plot.GetImageBytes(Width, Height, ImageFormat.Png);
    }

    /// <summary>This month: spending per day, with a line at the even daily pace of the total budget.</summary>
    public static byte[] ByDay(IReadOnlyCollection<Purchase> purchases, decimal monthlyLimit, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var days = DateTime.DaysInMonth(local.Year, local.Month);
        var perDay = Enumerable.Range(1, local.Day)
            .Select(d => purchases.Where(p => p.Posted.ToLocalTime() is var t && t.Year == local.Year && t.Month == local.Month && t.Day == d)
                .Sum(p => p.Amount))
            .ToList();
        var plot = NewPlot($"{local:MMMM}: spending per day (posted)");

        var bars = plot.Add.Bars(perDay.Select((v, i) => new Bar
        {
            Position = i + 1, Value = (double)v, FillColor = Colors.SteelBlue, Size = 0.8,
        }).ToList());
        if (monthlyLimit > 0)
        {
            var pace = plot.Add.HorizontalLine((double)(monthlyLimit / days));
            pace.LineColor = Colors.Crimson;
            pace.LinePattern = LinePattern.Dashed;
            pace.LegendText = $"Even pace: {(monthlyLimit / days).ToString("C0", Usd)}/day";
            plot.ShowLegend(Alignment.UpperLeft);
        }
        plot.Axes.Margins(bottom: 0);
        // After Margins, which would otherwise shrink the axis to the days so far: show the whole month.
        plot.Axes.SetLimitsX(0.4, days + 0.6);
        plot.XLabel("Day of month");
        return plot.GetImageBytes(Width, Height, ImageFormat.Png);
    }
}
