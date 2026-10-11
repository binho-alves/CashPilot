using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;

namespace CashPilot.Web.Components.Charts;

/// <summary>Small helpers shared by the SVG charts (no JavaScript library: they render on the server).</summary>
public static class ChartMath
{
    private static readonly CultureInfo Br = new("pt-BR");

    public static readonly string[] ShortMonths =
        ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

    /// <summary>A round upper bound for an axis (1, 2, 5 times a power of ten), so the ticks read well.</summary>
    public static decimal NiceMax(decimal max)
    {
        if (max <= 0) return 1;
        var power = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)max)));
        var n = max / power;
        var step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return step * power;
    }

    /// <summary>Axis label: "R$ 0", "R$ 800", "R$ 2,5 mil", "R$ 1,2 mi".</summary>
    public static string Short(decimal value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000m) return "R$ " + (value / 1_000_000m).ToString("0.##", Br) + " mi";
        if (abs >= 10_000m) return "R$ " + (value / 1_000m).ToString("0.#", Br) + " mil";
        if (abs >= 1_000m) return "R$ " + (value / 1_000m).ToString("0.##", Br) + " mil";
        return "R$ " + value.ToString("0", Br);
    }

    public static string Money(decimal value) => value.ToString("C2", Br);

    /// <summary>A number for an SVG attribute: dot as decimal separator, whatever the machine's culture.</summary>
    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// An SVG &lt;text&gt;. Written as markup on purpose: Razor treats a literal &lt;text&gt; inside a code block as its own
    /// "switch to markup" tag and drops it, so the labels of a chart drawn in a loop would vanish.
    /// </summary>
    public static MarkupString Text(double x, double y, string anchor, string cssClass, string content) =>
        new($"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" class=\"{cssClass}\">{WebUtility.HtmlEncode(content)}</text>");
}
