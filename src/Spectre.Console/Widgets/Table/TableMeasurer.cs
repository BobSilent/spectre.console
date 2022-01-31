namespace Spectre.Console;

internal sealed class TableMeasurer : TableAccessor
{
    private const int EdgeCount = 2;

    private readonly int? _explicitWidth;
    private readonly TableBorder _border;
    private readonly bool _padRightCell;

    public TableMeasurer(Table table, RenderContext options)
        : base(table, options)
    {
        _explicitWidth = table.Width;
        _border = table.Border;
        _padRightCell = table.PadRightCell;
    }

    public int CalculateTotalCellWidth(int maxWidth)
    {
        var totalCellWidth = maxWidth;
        if (_explicitWidth != null)
        {
            totalCellWidth = Math.Min(_explicitWidth.Value, maxWidth);
        }

        return totalCellWidth - GetNonColumnWidth();
    }

    /// <summary>
    /// Gets the width of everything that's not a cell.
    /// That means separators, edges and padding.
    /// </summary>
    /// <returns>The width of everything that's not a cell.</returns>
    public int GetNonColumnWidth()
    {
        var hideBorder = !_border.Visible;
        var separators = hideBorder ? 0 : Columns.Count - 1;
        var edges = hideBorder ? 0 : EdgeCount;
        var padding = Columns.Select(x => x.Padding?.GetWidth() ?? 0).Sum();

        if (!_padRightCell)
        {
            padding -= Columns.Last().Padding.GetRightSafe();
        }

        return separators + edges + padding;
    }

    /// <summary>
    /// Calculates the width of all columns minus any padding.
    /// </summary>
    /// <param name="maxWidth">The maximum width that the columns may occupy.</param>
    /// <returns>A list of column widths.</returns>
    public List<int> CalculateColumnWidths(int maxWidth)
    {
        var width_ranges = MeasureColumns(maxWidth);
        var widths = width_ranges.Select(range => range.Max).ToList();

        var tableWidth = widths.Sum();
        if (tableWidth > maxWidth)
        {
            var wrappable = Columns.Select(c => !c.NoWrap).ToList();
            widths = CollapseWidths(widths, wrappable, maxWidth);
            tableWidth = widths.Sum();

            // Last resort, reduce columns evenly
            if (tableWidth > maxWidth)
            {
                var excessWidth = tableWidth - maxWidth;
                widths = Ratio.Reduce(excessWidth, widths.ConvertAll(_ => 1), widths, widths);
                tableWidth = widths.Sum();
            }
        }

        if (tableWidth < maxWidth && Expand)
        {
            var padWidths = Ratio.Distribute(maxWidth - tableWidth, widths);
            widths = widths.Zip(padWidths, (a, b) => (a, b)).Select(f => f.a + f.b).ToList();
        }

        return widths;
    }

    internal Measurement[] MeasureColumns(int maxWidth)
    {
        IEnumerable<(int Index, Measurement Measurement)> MeasureStarColumns(IEnumerable<(TableColumn Column, int Index)> starColumns, int totalWidthForStar)
        {
            var sumStarWeight = starColumns.Sum(x => x.Column.Width.Value);
            var ratio = totalWidthForStar / sumStarWeight;
            foreach (var x in starColumns)
            {
                var starWidth = (int)Math.Round(x.Column.Width.Value * ratio);

                yield return (x.Index, new Measurement(starWidth, starWidth));
            }
        }

        // Index and separate columns
        var indexColumns = Columns.Select((column, index) => (column, index)).ToList();
        var fixedColumns = indexColumns.Where(x => !x.column.IsProportionalWidth());
        var starColumns = indexColumns.Where(x => x.column.IsProportionalWidth());

        // First calculate fixed cells
        var fixedWidth_ranges = fixedColumns.Select(x => (x.index, measurement: MeasureColumn(x.column, maxWidth))).ToList();

        // Get remainder
        var consumedWidth = fixedWidth_ranges.Sum(x => x.measurement.Max);
        var totalWidthForStar = Math.Max(0, maxWidth - consumedWidth);

        // And apportioned to the remainder
        var starWidth_ranges = MeasureStarColumns(starColumns, totalWidthForStar);
        var width_ranges = fixedWidth_ranges.Concat(starWidth_ranges).OrderBy(x => x.Item1).Select(x => x.Item2);

        return width_ranges.ToArray();
    }

    private Measurement MeasureColumn(TableColumn column, int maxWidth)
    {
        // Predetermined width?
        if (column.IsFixedWidth())
        {
            return new Measurement((int)column.Width.Value, (int)column.Width.Value);
        }

        var columnIndex = Columns.IndexOf(column);
        var rows = Rows.Select(row => row[columnIndex]);

        var minWidths = new List<int>();
        var maxWidths = new List<int>();

        // Include columns (both header and footer) in measurement
        var headerMeasure = column.Header.Measure(Options, maxWidth);
        var footerMeasure = column.Footer?.Measure(Options, maxWidth) ?? headerMeasure;
        minWidths.Add(Math.Min(headerMeasure.Min, footerMeasure.Min));
        maxWidths.Add(Math.Max(headerMeasure.Max, footerMeasure.Max));

        foreach (var row in rows)
        {
            var rowMeasure = row.Measure(Options, maxWidth);
            minWidths.Add(rowMeasure.Min);
            maxWidths.Add(rowMeasure.Max);
        }

        var padding = column.Padding?.GetWidth() ?? 0;

        return new Measurement(
            minWidths.Count > 0 ? minWidths.Max() : padding,
            maxWidths.Count > 0 ? maxWidths.Max() : maxWidth);
    }

    // Reduce widths so that the total is less or equal to the max width.
    // Ported from Rich by Will McGugan, licensed under MIT.
    // https://github.com/willmcgugan/rich/blob/527475837ebbfc427530b3ee0d4d0741d2d0fc6d/rich/table.py#L442
    private static List<int> CollapseWidths(List<int> widths, List<bool> wrappable, int maxWidth)
    {
        var totalWidth = widths.Sum();
        var excessWidth = totalWidth - maxWidth;

        if (wrappable.AnyTrue())
        {
            while (totalWidth != 0 && excessWidth > 0)
            {
                var maxColumn = widths.Zip(wrappable, (first, second) => (width: first, allowWrap: second))
                    .Where(x => x.allowWrap)
                    .Max(x => x.width);

                var secondMaxColumn = widths.Zip(wrappable, (width, allowWrap) => allowWrap && width != maxColumn ? width : 1).Max();
                var columnDifference = maxColumn - secondMaxColumn;

                var ratios = widths.Zip(wrappable, (width, allowWrap) => width == maxColumn && allowWrap ? 1 : 0).ToList();
                if (!ratios.Any(x => x != 0) || columnDifference == 0)
                {
                    break;
                }

                var maxReduce = widths.Select(_ => Math.Min(excessWidth, columnDifference)).ToList();
                widths = Ratio.Reduce(excessWidth, ratios, maxReduce, widths);

                totalWidth = widths.Sum();
                excessWidth = totalWidth - maxWidth;
            }
        }

        return widths;
    }
}