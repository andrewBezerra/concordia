using Concordia.Hook.Config;
using Concordia.Hook.Review;

namespace Concordia.Hook.Tests;

public class LensTriageTests
{
    private static readonly Settings DotNet = Settings.From("dotnet", Presets.Load("dotnet")!);

    private static readonly Dictionary<string, List<string>> NoAddedLines = new();

    [Fact]
    public void Small_change_outside_risky_paths_selects_no_lens()
    {
        var result = LensTriage.Evaluate(["src/Api/Formatting/Money.cs"], NoAddedLines, 20, 1, DotNet);

        Assert.Empty(result.Lenses);
        Assert.False(result.EscalateAll);
    }

    [Fact]
    public void Path_rules_select_only_the_matching_lens()
    {
        var result = LensTriage.Evaluate(["src/Api/Auth/TokenService.cs"], NoAddedLines, 30, 1, DotNet);

        Assert.Contains(result.Lenses, l => l.Lens == "security");
        Assert.DoesNotContain(result.Lenses, l => l.Lens == "performance");
    }

    [Fact]
    public void Signal_in_added_lines_selects_the_lens()
    {
        var added = new Dictionary<string, List<string>>
        {
            ["src/Api/Reports/Export.cs"] = ["var rows = db.Orders.FromSqlRaw(\"select * from orders where id = \" + id);"],
        };

        var result = LensTriage.Evaluate(["src/Api/Reports/Export.cs"], added, 10, 1, DotNet);

        var security = Assert.Single(result.Lenses, l => l.Lens == "security");
        Assert.Contains(security.Reasons, r => r.Contains("FromSqlRaw"));
    }

    [Fact]
    public void Large_change_escalates_to_every_lens()
    {
        var result = LensTriage.Evaluate(["src/Api/Formatting/Money.cs"], NoAddedLines, 900, 1, DotNet);

        Assert.True(result.EscalateAll);
        Assert.Equal(DotNet.Lenses.Count, result.Lenses.Count);
    }

    [Fact]
    public void Many_projects_escalate_to_every_lens()
    {
        var result = LensTriage.Evaluate(["a/A.cs"], NoAddedLines, 10, 5, DotNet);

        Assert.True(result.EscalateAll);
        Assert.Contains(result.EscalationReasons, r => r.Contains("5 projects"));
    }
}
