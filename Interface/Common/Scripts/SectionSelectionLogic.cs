using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Pure selection operations shared by section navigation and model regression checks.</summary>
public static class SectionSelectionLogic
{
	/// <summary>Applies regular or Shift-click selection; returns false when it would hide the final section.</summary>
	public static bool ApplyClick(HashSet<Guid> visibleIds, Guid clickedId, bool shiftPressed)
	{
		ArgumentNullException.ThrowIfNull(visibleIds);
		if (!shiftPressed)
		{
			visibleIds.Clear();
			visibleIds.Add(clickedId);
			return true;
		}

		if (visibleIds.Contains(clickedId))
		{
			if (visibleIds.Count <= 1)
				return false;
			visibleIds.Remove(clickedId);
			return true;
		}

		visibleIds.Add(clickedId);
		return true;
	}

	/// <summary>Returns selected IDs in score order, including only IDs present in both inputs.</summary>
	public static IReadOnlyList<Guid> OrderVisible(IEnumerable<Guid> scoreOrder, IReadOnlySet<Guid> visibleIds)
	{
		ArgumentNullException.ThrowIfNull(scoreOrder);
		ArgumentNullException.ThrowIfNull(visibleIds);
		return scoreOrder.Where(visibleIds.Contains).ToArray();
	}

	/// <summary>Preserves a surviving visible focus, otherwise chooses the first visible section or selects fallback.</summary>
	public static Guid ResolveFocusAfterDeletion(
		IReadOnlyList<Guid> remainingScoreOrder,
		HashSet<Guid> visibleIds,
		Guid previousFocusedId,
		Guid fallbackId)
	{
		ArgumentNullException.ThrowIfNull(remainingScoreOrder);
		ArgumentNullException.ThrowIfNull(visibleIds);
		var remaining = remainingScoreOrder.ToHashSet();
		visibleIds.RemoveWhere(id => !remaining.Contains(id));
		if (visibleIds.Contains(previousFocusedId) && remaining.Contains(previousFocusedId))
			return previousFocusedId;

		var firstVisible = remainingScoreOrder.FirstOrDefault(visibleIds.Contains);
		if (firstVisible != Guid.Empty)
			return firstVisible;

		if (remaining.Contains(fallbackId))
		{
			visibleIds.Add(fallbackId);
			return fallbackId;
		}

		if (remainingScoreOrder.Count == 0) return Guid.Empty;
		var firstRemaining = remainingScoreOrder[0];
		visibleIds.Add(firstRemaining);
		return firstRemaining;
	}
}
