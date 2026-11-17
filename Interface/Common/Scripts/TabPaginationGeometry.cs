using System;

/// <summary>Calculations for mapping a horizontally visible column to its measure indices.</summary>
public static class TabPaginationGeometry
{
	/// <summary>
	/// Returns the zero-based inclusive measure range intersecting the viewport.
	/// Measure cells are equal width; their left edges are inclusive and right edges exclusive.
	/// </summary>
	public static (int First, int Last)? VisibleSlice(
		double columnLeft,
		double columnWidth,
		int count,
		double viewportLeft,
		double viewportRight)
	{
		if (!double.IsFinite(columnLeft) || !double.IsFinite(columnWidth) || columnWidth <= 0 || count <= 0 ||
			!double.IsFinite(viewportLeft) || !double.IsFinite(viewportRight) || viewportRight <= viewportLeft)
			return null;

		var columnRight = columnLeft + columnWidth;
		var measureWidth = columnWidth / count;
		if (!double.IsFinite(columnRight) || !double.IsFinite(measureWidth) || measureWidth <= 0)
			return null;

		var visibleLeft = Math.Max(columnLeft, viewportLeft);
		var visibleRight = Math.Min(columnRight, viewportRight);
		if (visibleRight <= visibleLeft)
			return null;

		var first = (int)Math.Floor((visibleLeft - columnLeft) / measureWidth);
		var last = (int)Math.Ceiling((visibleRight - columnLeft) / measureWidth) - 1;
		first = Math.Clamp(first, 0, count - 1);
		last = Math.Clamp(last, 0, count - 1);
		return first <= last ? (first, last) : null;
	}
}
