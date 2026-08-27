using AgentSandbox.Domain;

namespace AgentSandbox.Domain.Tests;

public sealed class SafetyPolicyTests
{
    [Theory]
    [InlineData(4, 2, 50)]
    [InlineData(64, 8, 50)]
    public void RecommendationsAreClamped(int processors, int expectedCpu, int expectedDisk)
    {
        var result = ResourceProfile.Recommend(processors, 32L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
        Assert.Equal(expectedCpu, result.CpuCount);
        Assert.Equal(16, result.MemoryGiB);
        Assert.Equal(expectedDisk, result.DiskGiB);
    }

    [Fact]
    public void ResourceValidationPreservesWindowsCapacity()
    {
        var errors = new ResourceProfile(16, 30, 95).Validate(16, 32L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
        Assert.Equal(3, errors.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void GuestComponentsRejectAmbiguousPaths(string component) =>
        Assert.Throws<ArgumentException>(() => GuestPathPolicy.ValidateComponents([component]));

    [Fact]
    public void SystemRootIsReadOnly()
    {
        var request = new GuestFileRequest { Operation = "mkdir", RootId = GuestRoots.System, RelativePath = ["tmp", "x"] };
        Assert.Throws<UnauthorizedAccessException>(() => GuestPathPolicy.ValidateRequest(request));
    }

    [Fact]
    public void ListingPagesAreBounded()
    {
        var request = new GuestFileRequest { Operation = "list", PageSize = 201 };
        Assert.Throws<ArgumentOutOfRangeException>(() => GuestPathPolicy.ValidateRequest(request));
    }
}
