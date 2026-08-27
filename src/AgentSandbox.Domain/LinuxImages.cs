namespace AgentSandbox.Domain;

public sealed record LinuxImage(
    string Id,
    string DisplayName,
    string Distribution,
    string Release,
    string ImageReference,
    string Description,
    ResourceProfile MinimumResources,
    ResourceProfile RecommendedResources,
    bool IsCustomImage = false,
    bool IsUserSupplied = false,
    string Architecture = "x86_64");

public static class LinuxImages
{
    public const string DefaultId = "ubuntu-24.04";
    public const string CustomId = "custom";

    public static IReadOnlyList<LinuxImage> All { get; } =
    [
        new(
            DefaultId,
            "Ubuntu 24.04 LTS",
            "Ubuntu",
            "24.04 LTS",
            "24.04",
            "Default, broadly compatible development environment.",
            new ResourceProfile(2, 4, 30),
            new ResourceProfile(4, 4, 50)),
        new(
            "ubuntu-22.04",
            "Ubuntu 22.04 LTS",
            "Ubuntu",
            "22.04 LTS",
            "22.04",
            "Older LTS for projects that need its library baseline.",
            new ResourceProfile(2, 4, 30),
            new ResourceProfile(4, 4, 50)),
        new(
            "debian-13",
            "Debian 13",
            "Debian",
            "13",
            "https://cloud.debian.org/images/cloud/trixie/20260826-2582/debian-13-generic-amd64-20260826-2582.qcow2",
            "Smaller general-purpose base with a conservative package set.",
            new ResourceProfile(2, 2, 20),
            new ResourceProfile(2, 2, 30),
            true),
        new(
            "arch-linux",
            "Arch Linux",
            "Arch Linux",
            "2026.08",
            "https://geo.mirror.pkgbuild.com/images/v20260815.573966/Arch-Linux-x86_64-cloudimg-20260815.573966.qcow2",
            "Rolling, minimal base with current developer packages through pacman.",
            new ResourceProfile(1, 2, 15),
            new ResourceProfile(2, 2, 25),
            true),
        new(
            "fedora-44",
            "Fedora Cloud 44",
            "Fedora Linux",
            "44",
            "https://download.fedoraproject.org/pub/fedora/linux/releases/44/Cloud/x86_64/images/Fedora-Cloud-Base-Generic-44-1.7.x86_64.qcow2",
            "Current RPM-based cloud environment with a compact base install.",
            new ResourceProfile(2, 2, 20),
            new ResourceProfile(2, 4, 30),
            true),
        new(
            "alpine-3.22",
            "Alpine Linux 3.22",
            "Alpine Linux",
            "3.22",
            "https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/cloud/generic_alpine-3.22.5-x86_64-uefi-cloudinit-r0.qcow2",
            "Ultra-lightweight musl-based environment; some glibc-only tools may not work.",
            new ResourceProfile(1, 1, 10),
            new ResourceProfile(1, 1, 15),
            true),
        new(
            CustomId,
            "Custom cloud image…",
            "Custom Linux",
            "User supplied",
            "",
            "Advanced: provide an x86_64 HTTPS cloud image with cloud-init, SSH, and apt, apk, dnf, or pacman.",
            new ResourceProfile(1, 1, 10),
            new ResourceProfile(2, 2, 20),
            true,
            true)
    ];

    public static LinuxImage GetRequired(string id) =>
        All.SingleOrDefault(image => string.Equals(image.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException($"Unknown Linux image '{id}'.", nameof(id));

    public static bool IsKnownReference(string imageReference) =>
        All.Any(image => !image.IsUserSupplied && string.Equals(image.ImageReference, imageReference, StringComparison.Ordinal));

    public static string ResolveReference(string imageId, string? customImageUrl)
    {
        var image = GetRequired(imageId);
        if (!image.IsUserSupplied) return image.ImageReference;
        return ValidateCustomImageUrl(customImageUrl);
    }

    public static string ValidateCustomImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Custom images must use an absolute HTTPS URL without credentials or a fragment.", nameof(value));
        var host = uri.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("Custom image URLs cannot target the local host.", nameof(value));
        return uri.AbsoluteUri;
    }
}
