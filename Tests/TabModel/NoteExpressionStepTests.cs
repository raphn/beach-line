using OpenTab.Music;

internal static class NoteExpressionStepTests
{
    public static void Run()
    {
        var expression = new NoteExpression
        {
            Kind = ExpressionKind.Pitch,
            Points = { new ExpressionPoint(0, -1, ExpressionSegmentMode.Step), new ExpressionPoint(.5, 2), new ExpressionPoint(1, 0) }
        };
        Assert(expression.Sample(.499999) == -1, "A step segment must hold the left value before its boundary.");
        Assert(expression.Sample(.5) == 2, "A step segment must reach the destination value at its exact boundary.");
        Assert(expression.Sample(.75) == 1, "A following linear segment must remain linear.");

        var legacy = new ExpressionPoint(.25, .5);
        Assert(legacy.SegmentMode == ExpressionSegmentMode.Linear, "Legacy two-value expression points must default to linear interpolation.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
