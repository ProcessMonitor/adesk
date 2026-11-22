using AdeskPro.Platform;
using Xunit;

namespace AdeskPro.Tests;

public class ClickPlannerTests
{
    [Theory]
    [InlineData(new[] { "invoke" }, "invoke")]
    [InlineData(new[] { "invoke", "toggle" }, "invoke")]
    [InlineData(new[] { "expandcollapse" }, "expandcollapse")]
    [InlineData(new[] { "selectionitem", "toggle" }, "selectionitem")]
    [InlineData(new[] { "toggle" }, "toggle")]
    public void Patterns_map_to_the_expected_strategy(string[] patterns, string expected)
    {
        Assert.Equal(expected, ClickPlanner.Decide(patterns));
    }

    [Theory]
    [InlineData(new string[0], "mouse-coords")]
    [InlineData(new[] { "scroll" }, "mouse-coords")]
    [InlineData(new[] { "value" }, "mouse-coords")]
    [InlineData(new[] { "scroll", "value", "toggle" }, "toggle")]
    public void Elements_without_clickable_patterns_fall_back_to_coordinates(string[] patterns, string expected)
    {
        Assert.Equal(expected, ClickPlanner.Decide(patterns));
    }
}
