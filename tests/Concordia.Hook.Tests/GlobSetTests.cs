using Concordia.Hook.Infra;

namespace Concordia.Hook.Tests;

public class GlobSetTests
{
    [Theory]
    [InlineData("**/Migrations/**", "src/Data/Migrations/20260101_Init.cs", true)]
    [InlineData("**/Migrations/**", "Migrations/Init.cs", true)]
    [InlineData("**/Migrations/**", "src/Data/MigrationsHelper.cs", false)]
    [InlineData("**/*.cs", "Program.cs", true)]
    [InlineData("**/*.cs", "src/Api/Program.cs", true)]
    [InlineData("**/*.cs", "src/Api/Program.csproj", false)]
    [InlineData("*.csproj", "src/Api/Api.csproj", true)]
    [InlineData("**/Auth*/**", "src/AuthServer/Token.cs", true)]
    [InlineData("**/*Controller.cs", "src/Api/Controllers/OrdersController.cs", true)]
    [InlineData("global.json", "global.json", true)]
    [InlineData("**/endpoints/**", "src/Api/Endpoints/Orders.cs", true)]
    public void Matches_follows_glob_rules(string glob, string path, bool expected)
    {
        Assert.Equal(expected, new GlobSet([glob]).Matches(path));
    }

    [Fact]
    public void Match_normalizes_backslashes_and_leading_dot_slash()
    {
        Assert.Equal("**/Migrations/**", new GlobSet(["**/Migrations/**"]).Match(@".\src\Migrations\Init.cs"));
    }

    [Fact]
    public void Empty_set_matches_nothing()
    {
        var set = new GlobSet(["", "  "]);
        Assert.True(set.IsEmpty);
        Assert.False(set.Matches("a.cs"));
    }
}
