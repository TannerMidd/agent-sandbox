using System.Xml.Linq;

namespace AgentSandbox.Ui.Tests;

public sealed class UiContractTests
{
    [Fact]
    public void EveryStandardButtonIsWired()
    {
        var document = XDocument.Load(RepoFile("src", "AgentSandbox.App", "MainPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dead = document.Descendants(xaml + "Button")
            .Where(button => button.Attribute("Click") is null && button.Attribute("Command") is null)
            .Select(button => button.Attribute("Content")?.Value ?? "icon button")
            .ToArray();
        Assert.Empty(dead);
    }

    [Fact]
    public void IconOnlyButtonsHaveAccessibleTooltips()
    {
        var document = XDocument.Load(RepoFile("src", "AgentSandbox.App", "MainPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var missing = document.Descendants(xaml + "Button")
            .Where(button => button.Attribute("Content") is null && button.Element(xaml + "FontIcon") is not null)
            .Where(button => button.Attributes().All(attribute => !attribute.Name.LocalName.Contains("ToolTip", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void PrimaryProductWorkflowsArePresent()
    {
        var text = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "MainPage.xaml"));
        foreach (var label in new[] { "Dashboard", "Files", "Snapshots &amp; Recovery", "Diagnostics", "Settings", "Embedded terminal", "Transfer queue" })
            Assert.Contains(label, text, StringComparison.Ordinal);
        Assert.Contains("Test connection", text, StringComparison.Ordinal);
        Assert.Contains("GuestConnectionStatus", text, StringComparison.Ordinal);
        Assert.Contains("Active VM", text, StringComparison.Ordinal);
        Assert.Contains("New VM", text, StringComparison.Ordinal);
        Assert.Contains("Delete VM…", text, StringComparison.Ordinal);
        Assert.Contains("Resource usage", text, StringComparison.Ordinal);
        Assert.Contains("CpuUsagePercent", text, StringComparison.Ordinal);
        Assert.Contains("MemoryUsagePercent", text, StringComparison.Ordinal);
        Assert.Contains("DiskUsagePercent", text, StringComparison.Ordinal);
        Assert.Contains("ResourceUsageBarsVisibility", text, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"900\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void VmToolbarControlsShareTheSameGridRow()
    {
        var document = XDocument.Load(RepoFile("src", "AgentSandbox.App", "MainPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var picker = document.Descendants(xaml + "ComboBox").Single(element => element.Attribute(x + "Name")?.Value == "SandboxPicker");
        var newVm = document.Descendants(xaml + "Button").Single(element => element.Attribute("Content")?.Value == "New VM");

        Assert.Equal("1", picker.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", newVm.Attribute("Grid.Row")?.Value);
        Assert.Equal("Stretch", picker.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Stretch", newVm.Attribute("VerticalAlignment")?.Value);
    }

    [Fact]
    public void SetupAndDestructiveConfirmationsAreImplemented()
    {
        var code = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "MainPage.xaml.cs"));
        Assert.Contains("InstallMultipassAsync", code, StringComparison.Ordinal);
        Assert.Contains("ProvisionAsync", code, StringComparison.Ordinal);
        Assert.Contains("Linux image", code, StringComparison.Ordinal);
        Assert.Contains("LinuxImages.GetRequired", code, StringComparison.Ordinal);
        Assert.Contains("Hardening preset", code, StringComparison.Ordinal);
        Assert.Contains("HardeningPresetOptions", code, StringComparison.Ordinal);
        Assert.Contains("Outbound network access", code, StringComparison.Ordinal);
        Assert.Contains("Allow passwordless administration", code, StringComparison.Ordinal);
        Assert.Contains("selectedHardening.Validate()", code, StringComparison.Ordinal);
        Assert.Contains("Type {exact}", code, StringComparison.Ordinal);
        Assert.Contains("Purge forever", code, StringComparison.Ordinal);
        Assert.Contains("OVERWRITE", code, StringComparison.Ordinal);
        Assert.Contains("ShowLegacyImportAsync", code, StringComparison.Ordinal);
        Assert.Contains("nothing is renamed, migrated, or rebuilt", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open Windows Terminal", File.ReadAllText(RepoFile("src", "AgentSandbox.App", "ViewModels", "MainPageViewModel.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void AppIconIsMultiResolutionAndAppliedToTheExecutable()
    {
        var icon = File.ReadAllBytes(RepoFile("src", "AgentSandbox.App", "Assets", "AppIcon.ico"));
        Assert.Equal(0, BitConverter.ToUInt16(icon, 0));
        Assert.Equal(1, BitConverter.ToUInt16(icon, 2));
        var count = BitConverter.ToUInt16(icon, 4);
        Assert.True(count >= 8);
        var sizes = Enumerable.Range(0, count)
            .Select(index => icon[6 + index * 16] is 0 ? 256 : icon[6 + index * 16])
            .ToArray();
        foreach (var required in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
            Assert.Contains(required, sizes);

        var project = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "AgentSandbox.App.csproj"));
        var window = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "MainWindow.xaml.cs"));
        Assert.Contains("<ApplicationIcon>Assets\\AppIcon.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("AppWindow.SetIcon(\"Assets/AppIcon.ico\")", window, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Square44x44Logo.scale-200.png", 88, 88)]
    [InlineData("Square150x150Logo.scale-200.png", 300, 300)]
    [InlineData("StoreLogo.png", 50, 50)]
    public void PackageLogoAssetsHaveExpectedDimensions(string name, int expectedWidth, int expectedHeight)
    {
        var png = File.ReadAllBytes(RepoFile("src", "AgentSandbox.App", "Assets", name));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void DarkThemeIsTheSafeDefault()
    {
        var app = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "App.xaml"));
        Assert.Contains("RequestedTheme=\"Dark\"", app, StringComparison.Ordinal);
        Assert.Contains("#0B0E14", app, StringComparison.Ordinal);
        Assert.Contains("NavigationViewExpandedPaneBackground", app, StringComparison.Ordinal);
        Assert.Contains("NavigationPaneBackgroundBrush", app, StringComparison.Ordinal);
        Assert.Contains("#172132", app, StringComparison.Ordinal);
        Assert.Contains("NavigationViewContentGridBorderBrush", app, StringComparison.Ordinal);
        var mainPage = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "MainPage.xaml"));
        Assert.Contains("Background=\"{ThemeResource NavigationPaneBackgroundBrush}\"", mainPage, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"ShellNavigation_Loaded\"", mainPage, StringComparison.Ordinal);
        var mainPageCode = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "MainPage.xaml.cs"));
        Assert.Contains("splitView.PaneBackground = ShellNavigation.Background", mainPageCode, StringComparison.Ordinal);
        Assert.Contains("HighContrast", app, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerUninstallScopeCannotDeleteVmData()
    {
        var wix = File.ReadAllText(RepoFile("installer", "Product.wxs"));
        Assert.DoesNotContain("Multipass\\data", wix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RemoveFolder", wix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RemoveFile", wix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AgentSandbox.App.exe", wix, StringComparison.Ordinal);
        Assert.Contains("unsigned", wix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostReadinessDoesNotRequireAnElevatedFeatureQuery()
    {
        var code = File.ReadAllText(RepoFile("src", "AgentSandbox.Infrastructure", "HostPrerequisiteService.cs"));
        Assert.Contains("HypervisorPresent", code, StringComparison.Ordinal);
        Assert.Contains("Win32_OptionalFeature", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-WindowsOptionalFeature", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedHelperIsPublishedInAnIsolatedRuntimeDirectory()
    {
        var project = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "AgentSandbox.App.csproj"));
        var services = File.ReadAllText(RepoFile("src", "AgentSandbox.App", "AppServices.cs"));
        Assert.Contains("PublishDir=$(PublishDir)helper\\", project, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory, \"helper\", \"AgentSandbox.SetupHelper.exe\"", services, StringComparison.Ordinal);
    }

    private static string RepoFile(params string[] components)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentSandbox.slnx"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository root was not found.");
        return Path.Combine([directory.FullName, .. components]);
    }
}
