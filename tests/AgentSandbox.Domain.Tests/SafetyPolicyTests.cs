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

    [Fact]
    public void CuratedLinuxImagesHaveUniqueIdsAndApprovedReferences()
    {
        Assert.Equal(7, LinuxImages.All.Count);
        Assert.Equal(LinuxImages.All.Count, LinuxImages.All.Select(image => image.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(LinuxImages.All.Where(image => !image.IsUserSupplied), image => Assert.True(LinuxImages.IsKnownReference(image.ImageReference)));
        Assert.Equal(new ResourceProfile(1, 1, 10), LinuxImages.GetRequired("alpine-3.22").MinimumResources);
        Assert.Equal(new ResourceProfile(1, 1, 15), LinuxImages.GetRequired("alpine-3.22").RecommendedResources);
    }

    [Theory]
    [InlineData("https://images.example.org/arch.qcow2")]
    [InlineData("https://cdn.example.org/image.qcow2?build=42")]
    public void CustomCloudImageRequiresSafeHttpsUrl(string url) =>
        Assert.Equal(url, LinuxImages.ValidateCustomImageUrl(url));

    [Theory]
    [InlineData("http://images.example.org/image.qcow2")]
    [InlineData("https://user:secret@images.example.org/image.qcow2")]
    [InlineData("https://localhost/image.qcow2")]
    [InlineData("https://127.0.0.1/image.qcow2")]
    [InlineData("https://[::1]/image.qcow2")]
    public void UnsafeCustomCloudImageUrlIsRejected(string url) =>
        Assert.Throws<ArgumentException>(() => LinuxImages.ValidateCustomImageUrl(url));

    [Fact]
    public void LightweightImageCanUseItsLowerResourceFloor()
    {
        var alpine = LinuxImages.GetRequired("alpine-3.22");
        Assert.Empty(new ResourceProfile(1, 1, 10).Validate(8, 16L << 30, 100L << 30, alpine.MinimumResources));
    }

    [Fact]
    public void HardeningCatalogOffersACompatibilityToOfflineRange()
    {
        Assert.Equal(4, HardeningPresets.All.Count);
        Assert.Equal(HardeningPresets.All.Count, HardeningPresets.All.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(HardeningPresets.GetRequired(HardeningPresets.BalancedId).IsRecommended);
        Assert.Equal(NetworkAccessPolicy.Unrestricted, HardeningPresets.GetRequired(HardeningPresets.DevelopmentId).Options.NetworkAccess);
        var offline = HardeningPresets.GetRequired(HardeningPresets.OfflineId).Options;
        Assert.Equal(NetworkAccessPolicy.Offline, offline.NetworkAccess);
        Assert.False(offline.AllowAdministrativeTools);
        Assert.False(offline.AutomaticSecurityUpdates);
    }

    [Fact]
    public void NamedPresetCannotMisrepresentModifiedOptions()
    {
        var mislabeled = HardeningPresets.GetRequired(HardeningPresets.BalancedId).Options with { KernelHardening = false };
        var exception = Assert.Throws<ArgumentException>(() => mislabeled.Validate());
        Assert.Contains("custom preset ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineCustomHardeningRejectsAutomaticUpdates()
    {
        var invalid = SandboxHardeningOptions.Development with
        {
            PresetId = HardeningPresets.CustomId,
            NetworkAccess = NetworkAccessPolicy.Offline,
            AutomaticSecurityUpdates = true
        };
        Assert.Throws<ArgumentException>(() => invalid.Validate());
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
