using SafePatch.Host;
using SafePatch.Sandbox.Windows;

namespace SafePatch.Sandbox.Tests;

public sealed class SandboxOptionsTests
{
    [Fact]
    public void The_worker_gets_4_GiB_unless_the_environment_says_otherwise()
    {
        Assert.Equal(4UL << 30, SandboxOptions.ConfiguredMemoryLimitBytes(_ => null));
        Assert.Equal(8UL << 30, SandboxOptions.ConfiguredMemoryLimitBytes(name => name == SandboxOptions.MemoryLimitVariable ? "8192" : null));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("100")]
    [InlineData("4 GB")]
    [InlineData("99999999")]
    public void A_memory_limit_that_is_not_MiB_in_range_is_refused(string value)
    {
        var error = Assert.Throws<SafePatchException>(() => SandboxOptions.ConfiguredMemoryLimitBytes(_ => value));
        Assert.Contains(SandboxOptions.MemoryLimitVariable, error.Message);
    }
}
