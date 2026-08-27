using AgentSandbox.Domain;

namespace AgentSandbox.Domain.Tests;

public sealed class SafetyPolicyTests
{
    [Theory]
    [InlineData(4, 2, 50)]
    [InlineData(64, 8, 50)]
    public void Recommendations_are_clamped(int processors, int expectedCpu, int expectedDisk)
    {
        var result = ResourceProfile.Recommend(processors, 32L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
        Assert.Equal(expectedCpu, result.CpuCount);
        Assert.Equal(16, result.MemoryGiB);
        Assert.Equal(expectedDisk, result.DiskGiB);
    }

    [Fact]
    public void Resource_validation_preserves_windows_capacity()
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
    public void Guest_components_reject_ambiguous_paths(string component) =>
        Assert.Throws<ArgumentException>(() => GuestPathPolicy.ValidateComponents([component]));

    [Fact]
    public void System_root_is_read_only()
    {
        var request = new GuestFileRequest { Operation = "mkdir", RootId = GuestRoots.System, RelativePath = ["tmp", "x"] };
        Assert.Throws<UnauthorizedAccessException>(() => GuestPathPolicy.ValidateRequest(request));
    }

    [Fact]
    public void Listing_pages_are_bounded()
    {
        var request = new GuestFileRequest { Operation = "list", PageSize = 201 };
        Assert.Throws<ArgumentOutOfRangeException>(() => GuestPathPolicy.ValidateRequest(request));
    }
}
