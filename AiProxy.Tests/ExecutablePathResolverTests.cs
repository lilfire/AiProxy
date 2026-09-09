using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class ExecutablePathResolverTests
{
    [TestMethod]
    public void Resolve_existing_absolute_path_returns_same_path()
    {
        var resolver = new ExecutablePathResolver();
        var path = typeof(ExecutablePathResolverTests).Assembly.Location;

        var result = resolver.Resolve(path);

        Assert.AreEqual(path, result);
    }

    [TestMethod]
    public void Resolve_nonexistent_command_returns_command_unchanged()
    {
        var resolver = new ExecutablePathResolver();

        var result = resolver.Resolve("this-does-not-exist-12345.exe");

        Assert.AreEqual("this-does-not-exist-12345.exe", result);
    }
}
