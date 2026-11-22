namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Groups raw OCR lines (often split column-by-column in vertical Japanese manga speech bubbles)
/// into cohesive clickable text blocks with normalized [0..1] image coordinates.
/// </summary>
public static class OcrRegionClusterer
{
    public static IReadOnlyList<OcrTextBlock> Cluster(
        IReadOnlyList<OcrRawLine> rawLines,
        int imageWidth,
        int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(rawLines);
        if (rawLines.Count == 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return [];
        }

        var valid = new List<OcrRawLine>(rawLines.Count);
        foreach (OcrRawLine line in rawLines)
        {
            if (!string.IsNullOrWhiteSpace(line.Text) && line.Width > 0 && line.Height > 0)
            {
                valid.Add(line);
            }
        }

        if (valid.Count == 0)
        {
            return [];
        }

        int n = valid.Count;
        bool[] isVert = new bool[n];
        double[] thickness = new double[n];
        for (int i = 0; i < n; i++)
        {
            OcrRawLine line = valid[i];
            isVert[i] = line.Height > line.Width * 1.15 && ContainsCjk(line.Text);
            thickness[i] = isVert[i] ? line.Width : line.Height;
        }

        int[] parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (ShouldMerge(valid[i], isVert[i], thickness[i], valid[j], isVert[j], thickness[j], imageWidth, imageHeight))
                {
                    Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<(OcrRawLine Line, bool Vertical)>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out List<(OcrRawLine, bool)>? list))
            {
                list = [];
                groups[root] = list;
            }
            list.Add((valid[i], isVert[i]));
        }

        var blocks = new List<OcrTextBlock>(groups.Count);
        int idx = 0;
        double minDim = Math.Min(imageWidth, imageHeight);
        double padPx = Math.Max(4.0, minDim * 0.004);

        foreach (List<(OcrRawLine Line, bool Vertical)> cluster in groups.Values)
        {
            bool groupVertical = cluster.Count(static c => c.Vertical) >= (cluster.Count + 1) / 2;

            List<OcrRawLine> ordered = groupVertical
                ? [.. cluster.Select(static c => c.Line).OrderByDescending(static l => l.X + l.Width * 0.5).ThenBy(static l => l.Y)]
                : [.. cluster.Select(static c => c.Line).OrderBy(static l => l.Y).ThenBy(static l => l.X)];

            string joined = JoinOrderedLines(ordered);
            double minX = Math.Max(0, ordered.Min(static l => l.X) - padPx);
            double minY = Math.Max(0, ordered.Min(static l => l.Y) - padPx);
            double maxX = Math.Min(imageWidth, ordered.Max(static l => l.X + l.Width) + padPx);
            double maxY = Math.Min(imageHeight, ordered.Max(static l => l.Y + l.Height) + padPx);

            double w = Math.Max(1.0, maxX - minX);
            double h = Math.Max(1.0, maxY - minY);

            blocks.Add(new OcrTextBlock(
                Id: $"ocr-{idx++}",
                Text: joined,
                NormalizedX: Math.Clamp(minX / imageWidth, 0.0, 1.0),
                NormalizedY: Math.Clamp(minY / imageHeight, 0.0, 1.0),
                NormalizedWidth: Math.Clamp(w / imageWidth, 0.005, 1.0),
                NormalizedHeight: Math.Clamp(h / imageHeight, 0.005, 1.0),
                IsVertical: groupVertical));
        }

        // Sort reading order for predictable tab/list feel (top-to-bottom, right-to-left)
        blocks.Sort(static (a, b) =>
        {
            double rowDiff = a.NormalizedY - b.NormalizedY;
            if (Math.Abs(rowDiff) > 0.06)
            {
                return rowDiff.CompareTo(0.0);
            }
            return b.NormalizedX.CompareTo(a.NormalizedX);
        });

        return blocks;
    }

    private static bool ShouldMerge(
        OcrRawLine a,
        bool aVert,
        double aThick,
        OcrRawLine b,
        bool bVert,
        double bThick,
        int imageWidth,
        int imageHeight)
    {
        if (aVert != bVert)
        {
            return false;
        }

        double refSize = Math.Max(8.0, (aThick + bThick) * 0.5);
        double gapX = AxisGap(a.X, a.X + a.Width, b.X, b.X + b.Width);
        double gapY = AxisGap(a.Y, a.Y + a.Height, b.Y, b.Y + b.Height);

        if (aVert)
        {
            // Adjacent vertical columns inside the same speech bubble
            double maxColGap = Math.Max(refSize * 1.4, imageWidth * 0.015);
            double yOverlap = AxisOverlapRatio(a.Y, a.Y + a.Height, b.Y, b.Y + b.Height);
            bool colNeighbor = gapX <= maxColGap && (yOverlap >= 0.25 || gapY <= refSize * 0.9);
            // Split line fragments along same column
            bool sameColumnContinuation = gapX <= refSize * 0.45 && gapY <= refSize * 1.1;
            return colNeighbor || sameColumnContinuation;
        }
        else
        {
            // Adjacent horizontal lines inside the same bubble
            double maxRowGap = Math.Max(refSize * 1.1, imageHeight * 0.012);
            double xOverlap = AxisOverlapRatio(a.X, a.X + a.Width, b.X, b.X + b.Width);
            bool rowNeighbor = gapY <= maxRowGap && (xOverlap >= 0.25 || gapX <= refSize * 0.9);
            bool sameLineContinuation = gapY <= refSize * 0.45 && gapX <= refSize * 1.2;
            return rowNeighbor || sameLineContinuation;
        }
    }

    private static double AxisGap(double aMin, double aMax, double bMin, double bMax)
    {
        if (aMax < bMin)
        {
            return bMin - aMax;
        }
        if (bMax < aMin)
        {
            return aMin - bMax;
        }
        return 0.0;
    }

    private static double AxisOverlapRatio(double aMin, double aMax, double bMin, double bMax)
    {
        double overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
        if (overlap <= 0)
        {
            return 0.0;
        }
        double minLen = Math.Max(1.0, Math.Min(aMax - aMin, bMax - bMin));
        return overlap / minLen;
    }

    private static string JoinOrderedLines(IReadOnlyList<OcrRawLine> lines)
    {
        if (lines.Count == 1)
        {
            return lines[0].Text.Trim();
        }

        bool cjk = lines.Any(static l => ContainsCjk(l.Text));
        return cjk
            ? string.Concat(lines.Select(static l => l.Text.Trim()))
            : string.Join(' ', lines.Select(static l => l.Text.Trim()));
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char ch in text)
        {
            if ((ch >= '\u3040' && ch <= '\u30FF') || // Hiragana & Katakana
                (ch >= '\u4E00' && ch <= '\u9FFF') || // CJK Unified
                (ch >= '\uAC00' && ch <= '\uD7AF'))   // Hangul
            {
                return true;
            }
        }
        return false;
    }
}
