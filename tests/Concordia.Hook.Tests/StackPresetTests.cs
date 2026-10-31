using Concordia.Hook.Config;
using Concordia.Hook.Gate;

namespace Concordia.Hook.Tests;

/// <summary>Detection, command adaptation and test markers for every built-in stack.</summary>
public class StackPresetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("concordia-stack-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(params string[] files)
    {
        foreach (var file in files) File.WriteAllText(Path.Combine(_dir, file), "");
    }

    [Theory]
    [InlineData("App.slnx", "dotnet")]
    [InlineData("pom.xml", "java-maven")]
    [InlineData("build.gradle.kts", "java-gradle")]
    [InlineData("settings.gradle", "java-gradle")]
    [InlineData("pyproject.toml", "python")]
    [InlineData("requirements.txt", "python")]
    [InlineData("package.json", "node")]
    public void Root_marker_selects_the_stack_and_its_preset(string marker, string stack)
    {
        Touch(marker);

        var settings = ConfigLoader.Load(_dir);

        Assert.NotNull(settings);
        Assert.Equal(stack, settings.Stack);
        Assert.NotNull(settings.TestCommand);
        Assert.True(settings.TestGuard.Enabled);
        Assert.NotEmpty(settings.Lenses);
    }

    [Fact]
    public void Several_stacks_at_the_root_follow_the_priority_order()
    {
        Touch("package.json", "App.slnx", "pyproject.toml");

        Assert.Equal(new[] { "dotnet", "python", "node" }, ConfigLoader.DetectStacks(_dir));
        Assert.Equal("dotnet", ConfigLoader.Load(_dir)!.Stack);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", "pnpm run test --if-present")]
    [InlineData("yarn.lock", "yarn run test --if-present")]
    [InlineData("package-lock.json", "npm run test --if-present")]
    public void Node_uses_the_package_manager_of_the_lockfile(string lockfile, string test)
    {
        Touch("package.json", lockfile);

        Assert.Equal(test, ConfigLoader.Load(_dir)!.TestCommand);
    }

    [Theory]
    [InlineData("uv.lock", "uv run pytest -q -p no:cacheprovider")]
    [InlineData("poetry.lock", "poetry run pytest -q -p no:cacheprovider")]
    public void Python_uses_the_project_runner(string lockfile, string test)
    {
        Touch("pyproject.toml", lockfile);

        Assert.Equal(test, ConfigLoader.Load(_dir)!.TestCommand);
    }

    [Fact]
    public void Python_has_no_build_step()
    {
        Touch("pyproject.toml");

        Assert.Null(ConfigLoader.Load(_dir)!.BuildCommand);
    }

    [Fact]
    public void Wrappers_replace_maven_and_gradle_on_this_os()
    {
        var windows = OperatingSystem.IsWindows();

        Assert.Equal(windows ? "mvnw.cmd " : "./mvnw ", StackTools.Replacements("java-maven", Touched("mvnw", "mvnw.cmd"), windows)[0].To);
        Assert.Equal(windows ? "gradlew.bat " : "./gradlew ", StackTools.Replacements("java-gradle", Touched("gradlew", "gradlew.bat"), windows)[0].To);
    }

    [Fact]
    public void Commands_written_in_concordia_json_are_never_adapted()
    {
        Touch("package.json", "pnpm-lock.yaml");
        File.WriteAllText(Path.Combine(_dir, "concordia.json"), """{ "commands": { "test": "npm run test:unit" } }""");

        var settings = ConfigLoader.Load(_dir)!;

        Assert.Equal("npm run test:unit", settings.TestCommand);
        Assert.Equal("pnpm run build --if-present", settings.BuildCommand);
    }

    [Theory]
    [InlineData("node", "it('adds', () => {})", 1, 0)]
    [InlineData("node", "  test.each([1, 2])('x %i', n => {})", 1, 0)]
    [InlineData("node", "await submit(form)", 0, 0)]
    [InlineData("node", "it.skip('adds', () => {})", 0, 1)]
    [InlineData("node", "test.only('adds', () => {})", 0, 1)]
    [InlineData("node", "// it('adds', () => {})", 0, 0)]
    [InlineData("python", "def test_adds():", 1, 0)]
    [InlineData("python", "    async def test_adds(client):", 1, 0)]
    [InlineData("python", "def helper_test():", 0, 0)]
    [InlineData("python", "# def test_adds():", 0, 0)]
    [InlineData("python", "@pytest.mark.skip(reason='x')", 0, 1)]
    [InlineData("java-maven", "@Test", 1, 0)]
    [InlineData("java-maven", "    @Test void adds() {}", 1, 0)]
    [InlineData("java-maven", "@TestInstance(Lifecycle.PER_CLASS)", 0, 0)]
    [InlineData("java-maven", "@ParameterizedTest", 1, 0)]
    [InlineData("java-gradle", "@Disabled(\"flaky\")", 0, 1)]
    [InlineData("java-gradle", "// @Test", 0, 0)]
    public void Test_markers_count_tests_and_skips_per_stack(string stack, string line, int tests, int skips)
    {
        var guard = Settings.From(stack, Presets.Load(stack)!).TestGuard;

        var counts = TestGuard.Count(line, guard);

        Assert.Equal(tests, counts.Tests);
        Assert.Equal(skips, counts.Skips);
    }

    [Theory]
    [InlineData("node", "src/cart.test.ts")]
    [InlineData("node", "src/__tests__/cart.ts")]
    [InlineData("python", "tests/unit/test_cart.py")]
    [InlineData("python", "cart_test.py")]
    [InlineData("java-maven", "core/src/test/java/shop/CartTest.java")]
    [InlineData("java-gradle", "app/src/test/kotlin/shop/CartTest.kt")]
    public void Test_files_are_recognized_per_stack(string stack, string path)
    {
        var guard = Settings.From(stack, Presets.Load(stack)!).TestGuard;

        Assert.True(guard.Paths.Matches(path));
    }

    private string Touched(params string[] files)
    {
        Touch(files);
        return _dir;
    }
}
