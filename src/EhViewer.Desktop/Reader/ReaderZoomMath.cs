namespace EhViewer.Desktop.Reader;

/// <summary>
/// Pure geometry helpers for cursor- and center-anchored zoom offset calculations on WinUI ScrollViewers.
/// </summary>
public static class ReaderZoomMath
{
    public static (double HorizontalOffset, double VerticalOffset) ComputeAnchoredZoomOffsets(
        double currentHorizontalOffset,
        double currentVerticalOffset,
        float currentZoom,
        float targetZoom,
        double anchorViewportX,
        double anchorViewportY,
        double viewportWidth,
        double viewportHeight,
        double currentExtentWidth,
        double currentExtentHeight)
    {
        if (currentZoom <= 0f || targetZoom <= 0f ||
            !float.IsFinite(currentZoom) || !float.IsFinite(targetZoom) ||
            !double.IsFinite(currentHorizontalOffset) || !double.IsFinite(currentVerticalOffset) ||
            !double.IsFinite(anchorViewportX) || !double.IsFinite(anchorViewportY))
        {
            return (0.0, 0.0);
        }

        double safeCurH = Math.Max(0.0, currentHorizontalOffset);
        double safeCurV = Math.Max(0.0, currentVerticalOffset);
        double scaleRatio = (double)targetZoom / currentZoom;

        double contentX = (safeCurH + anchorViewportX) / currentZoom;
        double contentY = (safeCurV + anchorViewportY) / currentZoom;
        double rawH = contentX * targetZoom - anchorViewportX;
        double rawV = contentY * targetZoom - anchorViewportY;

        double maxH = Math.Max(0.0, Math.Max(0.0, currentExtentWidth) * scaleRatio - Math.Max(0.0, viewportWidth));
        double maxV = Math.Max(0.0, Math.Max(0.0, currentExtentHeight) * scaleRatio - Math.Max(0.0, viewportHeight));

        return (Math.Clamp(rawH, 0.0, maxH), Math.Clamp(rawV, 0.0, maxV));
    }
}
