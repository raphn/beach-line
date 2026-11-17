using Godot;

internal static class TrackColorTests
{
    public static void Run()
    {
        WhiteTintPreservesVelocityFill();
        TrackRgbaAndVelocityScaleCombine();
        FillCalculationDoesNotMutateThemeColor();
        Console.WriteLine("PASS track tint and velocity note fill calculation");
    }

    private static void WhiteTintPreservesVelocityFill()
    {
        var themed = new Color(0.115224f, 0.116016f, 0.1176f, 0.8f);
        for (var level = 1; level <= 5; level++)
        {
            var expected = Color.FromHsv(themed.H, themed.S, 0.13f + (level - 1) * 0.205f, themed.A);
            var actual = MeasureTabView.CalculateNoteFill(themed, level, 0xffffffff);
            CheckColor(actual, expected, $"white tint changed level {level} fill");
        }
    }

    private static void TrackRgbaAndVelocityScaleCombine()
    {
        var themed = new Color(0.2f, 0.3f, 0.4f, 0.8f);
        uint packed = 0x80ff4080;
        var tint = new Color(packed);
        var previousBaseFill = Color.FromHsv(themed.H, themed.S, 0.13f + (3 - 1) * 0.205f, themed.A);
        var expected = previousBaseFill * tint;
        CheckColor(MeasureTabView.CalculateNoteFill(themed, 3, packed), expected,
            "track RGB and alpha should multiply the velocity-adjusted theme fill");

        var red = MeasureTabView.CalculateNoteFill(themed, 3, 0xff0000ff);
        Check(red.R > 0 && Near(red.G, 0) && Near(red.B, 0), "red tint should preserve its channel hue");
        var darkRed = MeasureTabView.CalculateNoteFill(themed, 3, 0x400000ff);
        Check(darkRed.R < red.R, "track color value should affect fill brightness");
        var black = MeasureTabView.CalculateNoteFill(themed, 3, 0x000000ff);
        Check(Near(black.R, 0) && Near(black.G, 0) && Near(black.B, 0), "black track tint should remain black");
        var translucent = MeasureTabView.CalculateNoteFill(themed, 3, 0xff000080);
        Check(Near(translucent.A, previousBaseFill.A * (128f / 255f)), "track alpha should multiply fill alpha");

        var previous = MeasureTabView.CalculateNoteFill(themed, 1, 0x4080ffff);
        for (var level = 2; level <= 5; level++)
        {
            var current = MeasureTabView.CalculateNoteFill(themed, level, 0x4080ffff);
            Check(current.R > previous.R && current.G > previous.G && current.B > previous.B,
                $"velocity level {level} should brighten all tinted channels");
            previous = current;
        }
    }

    private static void FillCalculationDoesNotMutateThemeColor()
    {
        var themedFill = new Color(0.2f, 0.3f, 0.4f, 0.8f);
        var before = themedFill;
        _ = MeasureTabView.CalculateNoteFill(themedFill, 4, 0x336699ff);
        CheckColor(themedFill, before, "fill calculation mutated the theme color");
    }

    private static void CheckColor(Color actual, Color expected, string message) =>
        Check(Near(actual.R, expected.R) && Near(actual.G, expected.G) && Near(actual.B, expected.B) && Near(actual.A, expected.A), message);

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.00001f;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
