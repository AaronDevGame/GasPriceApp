using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

public sealed record DoeLocalPageResult(int PageNumber, string Method, string? FallbackReason,
    int Rows, long ElapsedMilliseconds);

// Experimental, conservative parser for the NCR company-column layout. Unsupported
// layouts go to AI; never infer a locality, a missing cell, or a column from flattened text.
public static class DoeLocalTableExtractor
{
    private static readonly string[] Companies =
        ["PETRON", "SHELL", "CALTEX", "PHOENIX", "TOTAL", "FLYING V", "UNIOIL", "SEAOIL", "PTT"];
    private static readonly string[] Grades =
        ["RON 100", "RON 97", "RON 95", "RON 91", "DIESEL", "DIESEL PLUS", "KEROSENE"];
    private static readonly Regex Coverage = new(
        @"For the week of (?<m1>[A-Za-z]+) (?<d1>\d{1,2})\s*[-–]\s*(?<m2>[A-Za-z]+) (?<d2>\d{1,2}),? (?<y>\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static DoePageExtraction? TryExtract(byte[] pdf, DoePageExtraction? context,
        out string? reason)
    {
        reason = null;
        try
        {
            using var document = PdfDocument.Open(pdf);
            var words = document.GetPage(1).GetWords().ToArray();
            if (words.Length == 0) return Reject("No embedded text; OCR or AI is required.", out reason);
            var lines = new List<List<Word>>();
            foreach (var word in words.OrderByDescending(Y))
            {
                if (lines.Count == 0 || Math.Abs(Y(lines[^1][0]) - Y(word)) > 2)
                    lines.Add([]);
                lines[^1].Add(word);
            }
            string Text(IEnumerable<Word> line) => string.Join(' ', line.OrderBy(X).Select(w => w.Text));
            var header = lines.SingleOrDefault(line => Text(line).Contains("PETRON SHELL CALTEX", StringComparison.Ordinal));
            if (header is null) return Reject("Unsupported or ambiguous company-column headers.", out reason);
            var product = header.SingleOrDefault(w => w.Text == "PRODUCT");
            var independent = header.SingleOrDefault(w => w.Text == "INDEPENDENT");
            if (product is null || independent is null)
                return Reject("Unsupported table boundaries.", out reason);
            var centers = new List<double> { X(product) };
            foreach (var company in Companies)
            {
                var token = company == "FLYING V" ? "FLYING" : company;
                var matches = header.Where(w => w.Text == token).ToArray();
                if (matches.Length != 1) return Reject("Missing or duplicate company header.", out reason);
                var center = X(matches[0]);
                if (company == "FLYING V")
                {
                    var v = header.SingleOrDefault(w => w.Text == "V");
                    if (v is null) return Reject("Ambiguous FLYING V header.", out reason);
                    center = (matches[0].BoundingBox.Left + v.BoundingBox.Right) / 2;
                }
                centers.Add(center);
            }
            centers.Add(X(independent));
            if (!centers.SequenceEqual(centers.Order()))
                return Reject("Unsupported company column order.", out reason);
            var boundaries = centers.Zip(centers.Skip(1), (a, b) => (a + b) / 2).ToArray();
            var areaEdge = product.BoundingBox.Left - 3;
            var tableLines = lines.Where(line => Y(line[0]) < Y(product) - 3)
                .Select(line => (Words: line, Grade: Text(line.Where(w => X(w) >= areaEdge && X(w) < boundaries[0]))))
                .Where(line => Grades.Contains(line.Grade)).ToArray();
            if (tableLines.Length == 0 || tableLines.Length % Grades.Length != 0)
                return Reject("Incomplete locality/product blocks.", out reason);
            var hasNcr = lines.Any(line => Text(line).Trim() == "NCR");
            if (!hasNcr && context?.Rows.Any(row => row.Province == "Metro Manila") != true)
                return Reject("Local parser supports explicitly identified NCR reports only.", out reason);
            var coverage = Coverage.Match(string.Join(' ', lines.Select(Text)));
            var start = context?.WeekStart;
            var end = context?.WeekEnd;
            if (coverage.Success)
            {
                var year = int.Parse(coverage.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (!DateTime.TryParseExact($"{coverage.Groups["m1"].Value} {coverage.Groups["d1"].Value} {year}",
                        "MMMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first) ||
                    !DateTime.TryParseExact($"{coverage.Groups["m2"].Value} {coverage.Groups["d2"].Value} {year}",
                        "MMMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
                    return Reject("Unsupported printed coverage dates.", out reason);
                if (first > last) first = first.AddYears(-1);
                start = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                end = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (start is null || end is null) return Reject("Coverage dates unavailable locally.", out reason);
            var rows = new List<ExtractedDoeBulkPrice>();
            for (var block = 0; block < tableLines.Length; block += Grades.Length)
            {
                if (!tableLines.Skip(block).Take(Grades.Length).Select(line => line.Grade).SequenceEqual(Grades))
                    return Reject("Unexpected fuel-grade sequence.", out reason);
                var top = Y(tableLines[block].Words[0]) + 3;
                var bottom = Y(tableLines[block + Grades.Length - 1].Words[0]) - 3;
                var city = Text(words.Where(w => X(w) < areaEdge && Y(w) <= top && Y(w) >= bottom));
                // Known printed abbreviation in DOE's NCR reports.
                if (city == "Taguig Cty") city = "Taguig City";
                if (!city.EndsWith(" City", StringComparison.Ordinal) || city.Length > 100)
                    return Reject("Ambiguous city label.", out reason);
                foreach (var line in tableLines.Skip(block).Take(Grades.Length))
                {
                    for (var column = 0; column < Companies.Length; column++)
                    {
                        var cell = Text(line.Words.Where(w => X(w) >= boundaries[column] && X(w) < boundaries[column + 1]));
                        if (string.IsNullOrWhiteSpace(cell)) continue;
                        var numbers = cell.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (numbers.Length != 2 || !decimal.TryParse(numbers[0], NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture, out var low) ||
                            !decimal.TryParse(numbers[1], NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture, out var high) || low > high)
                            return Reject("Company cell is not an unambiguous price pair.", out reason);
                        rows.Add(new(city, "Metro Manila", "National Capital Region", Companies[column], line.Grade, low, high));
                    }
                }
            }
            var validated = DoeFuelPriceImporter.ValidateBulkRows(rows);
            return new(start, end, "read", validated.Rows) { PrintedColumnHeaders = Companies };
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Parsing errors contain PDF internals; return a bounded, non-sensitive reason.
            return Reject("PDF text or table validation failed; AI fallback required.", out reason);
        }
    }

    private static DoePageExtraction? Reject(string message, out string? reason)
    { reason = message; return null; }
    private static double X(Word word) => (word.BoundingBox.Left + word.BoundingBox.Right) / 2;
    private static double Y(Word word) => (word.BoundingBox.Bottom + word.BoundingBox.Top) / 2;
}
