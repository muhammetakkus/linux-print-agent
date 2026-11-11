using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PrintAgent.Common.Models;

namespace PrintAgent.Linux;

public static class CupsPrintOptions
{
    public static IReadOnlyList<string> Build(PrintJobData job, byte[] pdfBytes)
    {
        static string Safe(string value) => value.Replace("\"", "").Replace(",", "").Trim();
        var options = job.Options;
        var result = new List<string> { options?.FitToPage == false ? "print-scaling=none" : "print-scaling=fit" };
        if (!string.IsNullOrEmpty(options?.Orientation))
        {
            if (options.Orientation == "landscape") result.Add("orientation-requested=4");
            else if (options.Orientation == "portrait") result.Add("orientation-requested=3");
        }
        else if (DetectOrientation(pdfBytes) == "landscape") result.Add("orientation-requested=4");
        switch (options?.Duplex)
        {
            case "long-edge": result.Add("sides=two-sided-long-edge"); break;
            case "short-edge": result.Add("sides=two-sided-short-edge"); break;
            case "one-sided": result.Add("sides=one-sided"); break;
        }
        if (options?.Color == false) result.Add("print-color-mode=monochrome");
        else if (options?.Color == true) result.Add("print-color-mode=color");
        var media = new List<string>();
        if (!string.IsNullOrEmpty(options?.PaperSize)) media.Add(Safe(options.PaperSize));
        if (!string.IsNullOrEmpty(options?.Tray)) media.Add(Safe(options.Tray));
        if (media.Count > 0) result.Add("media=" + string.Join(',', media));
        if (!string.IsNullOrEmpty(options?.Pages))
        {
            var pages = Regex.Replace(options.Pages, "[^0-9,-]", "");
            if (pages.Length > 0) result.Add("page-ranges=" + pages);
        }
        return result;
    }

    private static string DetectOrientation(byte[] pdfBytes)
    {
        try
        {
            var text = Encoding.ASCII.GetString(pdfBytes, 0, Math.Min(pdfBytes.Length, 8192));
            var match = Regex.Match(text, @"/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]");
            if (match.Success)
            {
                var width = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) - double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var height = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) - double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                return width > height ? "landscape" : "portrait";
            }
        }
        catch (FormatException) { }
        return "portrait";
    }
}
