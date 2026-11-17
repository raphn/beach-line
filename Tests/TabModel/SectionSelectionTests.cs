using System;
using System.Collections.Generic;
using System.Linq;

public static class SectionSelectionTests
{
	public static void Run()
	{
		var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
		var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
		var third = Guid.Parse("00000000-0000-0000-0000-000000000003");
		var fourth = Guid.Parse("00000000-0000-0000-0000-000000000004");
		var visible = new HashSet<Guid> { first };

		Check(SectionSelectionLogic.ApplyClick(visible, third, shiftPressed: false), "regular selection should succeed");
		Check(visible.SetEquals([third]), "regular selection should leave only the clicked section visible");

		Check(SectionSelectionLogic.ApplyClick(visible, first, shiftPressed: true), "Shift-click should add a section");
		Check(visible.SetEquals([first, third]), "Shift-click should preserve existing selection and add the clicked section");
		Check(SectionSelectionLogic.ApplyClick(visible, first, shiftPressed: true), "Shift-click should remove a selected section when another remains");
		Check(visible.SetEquals([third]), "Shift-click removal should leave the other section selected");
		Check(!SectionSelectionLogic.ApplyClick(visible, third, shiftPressed: true), "Shift-click must reject removal of the final visible section");
		Check(visible.SetEquals([third]), "rejecting final removal must preserve a nonempty selection");

		visible = [first, third];
		var ordered = SectionSelectionLogic.OrderVisible([first, second, third, fourth], visible);
		Check(ordered.SequenceEqual([first, third]), "nonconsecutive visible sections should remain in score order");

		var remainingAfterDeletion = new[] { first, third, fourth };
		var focused = SectionSelectionLogic.ResolveFocusAfterDeletion(remainingAfterDeletion, visible, second, fourth);
		Check(focused == first, "deleting an unselected focused section should choose the first surviving visible section in score order");

		visible = [second];
		focused = SectionSelectionLogic.ResolveFocusAfterDeletion(remainingAfterDeletion, visible, second, fourth);
		Check(focused == fourth && visible.SetEquals([fourth]), "deleting the only visible section should select the fallback neighbor");

		visible.Clear();
		focused = SectionSelectionLogic.ResolveFocusAfterDeletion(remainingAfterDeletion, visible, second, second);
		Check(focused == first && visible.SetEquals([first]), "an unavailable fallback should select the first surviving section");

		Console.WriteLine("PASS section selection and deletion rules");
	}

	private static void Check(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException(message);
	}
}
