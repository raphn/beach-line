using System;

public static class TabPaginationGeometryTests
{
	public static void Run()
	{
		CheckSlice(20, 100, 5, 30, 50, (0, 1), "partial measure edges should include each intersected measure");
		CheckSlice(0, 100, 4, 25, 75, (1, 2), "exact measure boundaries should exclude adjacent cells");
		CheckSlice(0, 120, 6, 40, 80, (2, 3), "a remainder column should map across its full measure count");
		CheckSlice(0, 100, 4, 100, 110, null, "a viewport after the column should not intersect");
		CheckSlice(0, 100, 4, -20, 0, null, "a viewport ending at the column edge should not intersect");
		CheckSlice(0, 100, 0, 0, 10, null, "an empty column should be invalid");
		Console.WriteLine("PASS tab pagination visible measure geometry");
	}

	private static void CheckSlice(double left, double width, int count, double viewportLeft, double viewportRight,
		(int First, int Last)? expected, string message)
	{
		var actual = TabPaginationGeometry.VisibleSlice(left, width, count, viewportLeft, viewportRight);
		if (actual != expected)
			throw new InvalidOperationException($"{message}. Expected {Format(expected)}, got {Format(actual)}.");
	}

	private static string Format((int First, int Last)? slice) => slice is { } value
		? $"[{value.First}, {value.Last}]"
		: "null";
}
