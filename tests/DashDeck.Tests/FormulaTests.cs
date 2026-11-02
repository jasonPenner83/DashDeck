using DashDeck.Core.Calculated;

namespace DashDeck.Tests;

/// <summary>
/// The formulas a calculated signal is written in (ADR-0081): arithmetic, comparisons, five
/// functions, and nothing else.
/// </summary>
public sealed class FormulaTests
{
    private static double Eval(string text, params (string Name, double Value)[] values)
    {
        var lookup = values.ToDictionary(v => v.Name, v => v.Value);
        return Formula.Parse(text).Evaluate(n => lookup.TryGetValue(n, out var v) ? v : double.NaN);
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("12 / 4 / 3", 1)]
    [InlineData("-2 * -3", 6)]
    [InlineData("3.05e-5 * 100000", 3.05)]
    [InlineData(".5 + .25", 0.75)]
    public void Arithmetic_reads_as_on_paper(string text, double expected) =>
        Assert.Equal(expected, Eval(text), 9);

    [Theory]
    [InlineData("2 < 3", 1)]
    [InlineData("3 <= 3", 1)]
    [InlineData("2 > 3", 0)]
    [InlineData("3 >= 4", 0)]
    [InlineData("3 == 3", 1)]
    [InlineData("3 != 3", 0)]
    [InlineData("1 < 2 && 2 < 3", 1)]
    [InlineData("1 > 2 || 2 < 3", 1)]
    [InlineData("1 > 2 || 2 > 3", 0)]
    public void Comparisons_give_one_or_zero(string text, double expected) =>
        Assert.Equal(expected, Eval(text));

    [Theory]
    [InlineData("min(4, 2, 8)", 2)]
    [InlineData("max(4, 2, 8)", 8)]
    [InlineData("abs(-3)", 3)]
    [InlineData("clamp(12, 0, 10)", 10)]
    [InlineData("clamp(-1, 0, 10)", 0)]
    [InlineData("if(1 < 2, 5, 6)", 5)]
    [InlineData("if(1 > 2, 5, 6)", 6)]
    public void The_functions(string text, double expected) =>
        Assert.Equal(expected, Eval(text));

    [Fact]
    public void Names_are_looked_up_and_listed_once_each()
    {
        var f = Formula.Parse("flow / speed * 100 + flow");

        Assert.Equal(["flow", "speed"], f.Names);
        Assert.Equal(6, Eval("flow / speed * 100 + flow", ("flow", 2), ("speed", 50)), 9);
    }

    [Fact]
    public void Only_the_branch_taken_is_worked_out()
    {
        // speed 0 would divide by zero in the branch not taken.
        Assert.Equal(1.9, Eval("if(speed < 3, flow, flow / speed * 100)", ("flow", 1.9), ("speed", 0)), 9);
        Assert.Equal(0, Eval("speed > 0 && flow / speed > 1", ("flow", 1.9), ("speed", 0)));
    }

    [Fact]
    public void A_name_with_no_value_gives_no_number()
    {
        Assert.True(double.IsNaN(Eval("map + 1")));
        Assert.True(double.IsNaN(Eval("min(map, 1)")));
    }

    [Theory]
    [InlineData("map +", "ends where")]
    [InlineData("map * (1 + 2", "')' was expected")]
    [InlineData("map ^ 2", "'^' was not expected")]
    [InlineData("sqrt(map)", "'sqrt' is not a function")]
    [InlineData("abs(1, 2)", "abs takes 1 value, not 2")]
    [InlineData("if(1, 2)", "if takes 3 values, not 2")]
    [InlineData("1..2", "'1..2' is not a number")]
    [InlineData("map map", "'m' was not expected")]
    public void A_formula_written_wrong_says_where_and_why(string text, string reason)
    {
        var ex = Assert.Throws<FormulaException>(() => Formula.Parse(text));

        Assert.Contains(reason, ex.Message);
        Assert.Contains("at character", ex.Message);
    }

    [Fact]
    public void An_empty_formula_is_refused()
    {
        Assert.Null(Formula.TryParse("  ", out var problem));
        Assert.Equal("the formula is empty", problem);
    }
}
