using Concordia.Hook.Config;

namespace Concordia.Hook.Tests;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("concordia-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Folder_without_stack_or_config_returns_null()
    {
        Assert.Null(ConfigLoader.Load(_dir));
    }

    [Fact]
    public void DotNet_repository_gets_the_preset()
    {
        File.WriteAllText(Path.Combine(_dir, "App.slnx"), "<Solution />");

        var settings = ConfigLoader.Load(_dir);

        Assert.NotNull(settings);
        Assert.Equal("dotnet", settings.Stack);
        Assert.StartsWith("dotnet build", settings.BuildCommand);
        Assert.True(settings.Protect.Matches("src/Data/Migrations/Init.cs"));
    }

    [Fact]
    public void Repository_file_overrides_preset_and_empty_command_disables_a_step()
    {
        File.WriteAllText(Path.Combine(_dir, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_dir, "concordia.json"), """
            {
              // comments and trailing commas are allowed
              "commands": { "format": "", "test": "dotnet test App.slnx" },
              "gate": { "maxAttempts": 5 },
            }
            """);

        var settings = ConfigLoader.Load(_dir)!;

        Assert.Null(settings.FormatCommand);
        Assert.Equal("dotnet test App.slnx", settings.TestCommand);
        Assert.StartsWith("dotnet build", settings.BuildCommand);
        Assert.Equal(5, settings.MaxAttempts);
        Assert.Equal(30, settings.MaxOutputLines);
    }

    [Fact]
    public void Custom_stack_works_from_the_repository_file_alone()
    {
        File.WriteAllText(Path.Combine(_dir, "concordia.json"), """{ "commands": { "build": "make", "test": "make test" } }""");

        var settings = ConfigLoader.Load(_dir)!;

        Assert.Equal("custom", settings.Stack);
        Assert.Equal("make test", settings.TestCommand);
        Assert.Empty(settings.Lenses);
    }
}
