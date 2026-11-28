using Concordia.Hook.Commands;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Tests;

public class SessionStartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("concordia session ").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Outside_a_git_repository_the_helper_path_is_still_in_the_context()
    {
        var stdout = new StringWriter();

        var code = SessionStartCommand.Run(new HookInput { Cwd = _dir, SessionId = "s" }, stdout);

        Assert.Equal(0, code);
        Assert.Contains("Concordia helper: \"", stdout.ToString());
        Assert.Contains(SessionStartCommand.HelperPath(), stdout.ToString());
    }
}
