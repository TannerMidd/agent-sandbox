using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSandbox.Application;
using AgentSandbox.App.ViewModels;
using AgentSandbox.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AgentSandbox.App;

public sealed partial class MainPage : Page
{
    private bool settingsLoaded;
    private bool updatingSandboxPicker;
    private readonly DispatcherTimer resourceUsageTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        resourceUsageTimer.Tick += ResourceUsageTimer_Tick;
        ViewModel.SetupRequested += async (_, _) => await ShowSetupAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeCommand.ExecuteAsync(null);
        SelectCurrentSandboxInPicker();
        LoadSettingsControls();
        resourceUsageTimer.Start();
        await ShowAvailableReleaseAsync(force: false);
    }

    private async void ResourceUsageTimer_Tick(object? sender, object e) => await ViewModel.RefreshResourceUsageAsync();

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        resourceUsageTimer.Stop();
        ViewModel.Dispose();
    }

    private void ShellNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        // NavigationView's expanded visual state assigns its internal SplitView pane
        // after normal resource lookup, so apply the product surface directly there.
        if (FindVisualChild<SplitView>(ShellNavigation) is { } splitView)
            splitView.PaneBackground = ShellNavigation.Background;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer?.Tag as string) ?? "Dashboard";
        ViewModel.PageTitle = tag switch { "Recovery" => "Snapshots & Recovery", _ => tag };
        DashboardPanel.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = tag == "Files" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPanel.Visibility = tag == "Recovery" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = tag == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Files" && ViewModel.CanOperateSandbox && ViewModel.GuestEntries.Count == 0)
            _ = ViewModel.LoadGuestFilesCommand.ExecuteAsync(null);
    }

    private async Task ShowSetupAsync()
    {
        try
        {
            switch (ViewModel.CurrentSetupState)
            {
                case SetupState.HyperVRequired: await ShowHyperVSetupAsync(); break;
                case SetupState.RebootRequired: await ShowRebootAsync(); break;
                case SetupState.MultipassRequired:
                case SetupState.StorageRequired: await ShowMultipassSetupAsync(); break;
                case SetupState.ResourceConfiguration:
                case SetupState.Welcome:
                case SetupState.CheckingHost: await ShowResourceSetupAsync(); break;
                case SetupState.Provisioning:
                case SetupState.InstallingPresets:
                    await MessageAsync("Setup in progress", "The current provisioning operation is resumable. Keep this window open while the VM is prepared.");
                    break;
                case SetupState.NeedsReview:
                    var legacy = await ViewModel.InspectLegacyImportAsync();
                    if (legacy is not null &&
                        ViewModel.Sandboxes.Count == 0 &&
                        !ViewModel.CurrentSettings.ImportedLegacyInstance &&
                        !string.Equals(ViewModel.CurrentSettings.InstanceName, legacy.InstanceName, StringComparison.Ordinal))
                        await ShowLegacyImportAsync(legacy);
                    else
                        ShellNavigation.SelectedItem = DiagnosticsNavigationItem;
                    break;
            }
        }
        catch (Exception exception) { await MessageAsync("Setup needs attention", exception.Message); }
    }

    private async Task ShowHyperVSetupAsync()
    {
        var dialog = Dialog("Enable Hyper-V",
            "Agent Sandbox uses the Windows Hyper-V platform to isolate the Ubuntu VM. This compiled, allow-listed operation requests UAC and may require a restart. Save your work first.",
            "Enable Hyper-V", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var response = await ViewModel.EnableHyperVAsync();
        if (response.RebootRequired) await ShowRebootAsync();
    }

    private async Task ShowRebootAsync()
    {
        var dialog = Dialog("Restart required",
            "Windows must restart before setup can continue. Agent Sandbox has saved the setup state and will resume safely the next time it opens.",
            "Restart now", "Later");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var start = new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"), UseShellExecute = false };
        start.ArgumentList.Add("/r"); start.ArgumentList.Add("/t"); start.ArgumentList.Add("0");
        Process.Start(start);
    }

    private async Task ShowMultipassSetupAsync()
    {
        var release = App.Services.MultipassInstaller.Release;
        var storageBox = new TextBox { Header = "Optional fresh storage directory", PlaceholderText = "Leave blank for Canonical's default" };
        var choose = new Button { Content = "Choose empty NTFS folder…", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += async (_, _) => { var folder = await PickFolderAsync(); if (folder is not null) storageBox.Text = folder.Path; };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"Agent Sandbox will download Multipass {release.Version} from Canonical's immutable GitHub release, verify the pinned SHA-256 and Authenticode publisher, then request UAC for installation. Existing Multipass installations and instances are never migrated or replaced.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(storageBox); content.Children.Add(choose);
        content.Children.Add(new TextBlock { Text = "Storage changes are allowed only for a fresh installation and an empty, local NTFS directory.", Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        var dialog = Dialog("Install Canonical Multipass", content, "Download & install", "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var progress = new Progress<OperationProgress>(item => ViewModel.OperationLabel = item.Percent is null ? item.Phase : $"{item.Phase} • {item.Percent}%");
        await ViewModel.InstallMultipassAsync(string.IsNullOrWhiteSpace(storageBox.Text) ? null : storageBox.Text.Trim(), progress);
        await ShowResourceSetupAsync();
    }

    private async Task ShowResourceSetupAsync(string? requestedName = null)
    {
        var host = await ViewModel.InspectHostAsync();
        var legacy = requestedName is null ? await ViewModel.InspectLegacyImportAsync() : null;
        if (!host.CanProvision)
        {
            if (legacy is not null) await ShowLegacyImportAsync(legacy);
            else ShellNavigation.SelectedItem = DiagnosticsNavigationItem;
            return;
        }

        var storageRoot = Path.GetPathRoot(ViewModel.CurrentSettings.StoragePath ?? AppContext.BaseDirectory)!;
        var recommendation = ResourceProfile.Recommend(Environment.ProcessorCount, host.AvailableMemoryBytes, new DriveInfo(storageRoot).AvailableFreeSpace);
        var name = new TextBox
        {
            Header = "VM name",
            Text = requestedName ?? ViewModel.CurrentSettings.InstanceName,
            PlaceholderText = "agent-sandbox-project"
        };
        var cpu = Number("Virtual CPUs", recommendation.CpuCount, 2, 8);
        var memory = Number("Memory (GiB)", recommendation.MemoryGiB, 4, 16);
        var disk = Number("Disk (GiB)", recommendation.DiskGiB, 30, 2048);
        var presetPanel = new StackPanel { Spacing = 6 };
        var presetChecks = new List<CheckBox>();
        foreach (var preset in ViewModel.Presets)
        {
            var check = new CheckBox { Content = $"{preset.DisplayName}  {preset.Version}", Tag = preset.Id, IsChecked = ViewModel.CurrentSettings.SelectedPresetIds.Contains(preset.Id) };
            presetChecks.Add(check); presetPanel.Children.Add(check);
        }
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Development boundary", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "The VM isolates development tools from normal Windows work, but it is not a hardened hostile-code boundary. Agent credentials are entered only inside the guest terminal; host credential stores are never copied.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        panel.Children.Add(name);
        var resources = new Grid { ColumnSpacing = 10 };
        resources.ColumnDefinitions.Add(new ColumnDefinition()); resources.ColumnDefinitions.Add(new ColumnDefinition()); resources.ColumnDefinitions.Add(new ColumnDefinition());
        resources.Children.Add(cpu); Grid.SetColumn(memory, 1); resources.Children.Add(memory); Grid.SetColumn(disk, 2); resources.Children.Add(disk);
        panel.Children.Add(resources);
        panel.Children.Add(new TextBlock { Text = "Optional pinned agent presets", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(presetPanel);

        var dialog = Dialog(requestedName is null ? "Create your agent sandbox" : "Create another sandbox", panel, "Provision Ubuntu 24.04", "Cancel");
        if (legacy is not null)
        {
            dialog.SecondaryButtonText = "Import agent-dev";
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "Existing agent-dev found", Message = $"Import preserves its name, data, storage, and {legacy.SnapshotNames.Count} snapshot(s)." });
        }
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary && legacy is not null) { await ViewModel.ImportLegacyAsync(legacy); SelectCurrentSandboxInPicker(); return; }
        if (result != ContentDialogResult.Primary) return;
        var profile = new ResourceProfile((int)cpu.Value, (int)memory.Value, (int)disk.Value);
        var errors = profile.Validate(Environment.ProcessorCount, host.TotalMemoryBytes, new DriveInfo(storageRoot).AvailableFreeSpace);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        var selected = presetChecks.Where(item => item.IsChecked == true).Select(item => (string)item.Tag).ToArray();
        var progress = new Progress<OperationProgress>(item => ViewModel.OperationLabel = item.Percent is null ? item.Phase : $"{item.Phase} • {item.Percent}%");
        await ViewModel.ProvisionAsync(name.Text.Trim(), profile, selected, progress);
        SelectCurrentSandboxInPicker();
        await MessageAsync("Agent Sandbox is ready", $"{name.Text.Trim()} passed its health checks, created the clean snapshot, and installed your selected presets.");
    }

    private async Task ShowLegacyImportAsync(LegacyImportCandidate legacy)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"The existing {legacy.InstanceName} VM is {legacy.State.ToString().ToLowerInvariant()} and has {legacy.SnapshotNames.Count} snapshot(s).",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Import only associates Agent Sandbox with this exact VM. Its name, storage, snapshots, files, and configuration are preserved; nothing is renamed, migrated, or rebuilt.",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        });
        if (await Dialog("Use your existing sandbox?", panel, "Import agent-dev", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.ImportLegacyAsync(legacy);
        SelectCurrentSandboxInPicker();
        await MessageAsync("Existing sandbox connected", $"Agent Sandbox now manages {legacy.InstanceName}. Your VM and its data were not changed.");
    }

    private async void ChooseHostFolder_Click(object sender, RoutedEventArgs e) { var folder = await PickFolderAsync(); if (folder is not null) ViewModel.NavigateHost(folder.Path); }
    private void HostUp_Click(object sender, RoutedEventArgs e) { var parent = Directory.GetParent(ViewModel.HostPath); if (parent is not null) ViewModel.NavigateHost(parent.FullName); }
    private void HostList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (HostList.SelectedItem is HostFileItem { Kind: "Folder" } item) ViewModel.NavigateHost(item.FullPath); }
    private async void GuestList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (GuestList.SelectedItem is GuestFileEntry item) await TryAsync(() => ViewModel.NavigateGuestAsync(item)); }
    private async void GuestUp_Click(object sender, RoutedEventArgs e) => await TryAsync(ViewModel.NavigateGuestUpAsync);

    private async void GuestRoot_Click(object sender, RoutedEventArgs e)
    {
        var next = ViewModel.GuestRootId == GuestRoots.Work ? GuestRoots.System : GuestRoots.Work;
        await TryAsync(async () =>
        {
            await ViewModel.SetGuestRootAsync(next);
            GuestRootButton.Content = next == GuestRoots.Work ? "System (read-only)" : "Back to workspace";
        });
    }

    private async void GuestSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        await TryAsync(() => ViewModel.SearchGuestAsync(args.QueryText, HiddenFilesToggle.IsChecked == true));

    private async void HiddenFiles_Click(object sender, RoutedEventArgs e) =>
        await TryAsync(() => ViewModel.SearchGuestAsync(GuestSearchBox.Text, HiddenFilesToggle.IsChecked == true));

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var paths = HostList.SelectedItems.Cast<HostFileItem>().Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add("*"); InitializePicker(picker);
            paths = (await picker.PickMultipleFilesAsync()).Select(item => item.Path).ToArray();
        }
        if (paths.Length > 0 && await SelectedConflictAsync() is { } conflict) await TryAsync(() => ViewModel.UploadAsync(paths, conflict));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var items = GuestList.SelectedItems.Cast<GuestFileEntry>().Where(item => item.Kind is "file" or "directory").ToArray();
        if (items.Length == 0) { await MessageAsync("Choose guest items", "Select one or more regular files or folders to download."); return; }
        var folder = await PickFolderAsync();
        if (folder is not null && await SelectedConflictAsync() is { } conflict) await TryAsync(() => ViewModel.DownloadAsync(items, folder.Path, conflict));
    }

    private async void GuestList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var paths = (await e.DataView.GetStorageItemsAsync()).Where(item => item is StorageFile or StorageFolder).Select(item => item.Path).ToArray();
        if (paths.Length > 0 && await SelectedConflictAsync() is { } conflict) await TryAsync(() => ViewModel.UploadAsync(paths, conflict));
    }

    private void GuestList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var names = e.Items.Cast<GuestFileEntry>().Where(item => item.Kind is "file" or "directory").Select(item => item.Name).ToArray();
        e.Data.SetText("agent-sandbox-guest:" + JsonSerializer.Serialize(names));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private async void HostList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var text = await e.DataView.GetTextAsync();
        const string prefix = "agent-sandbox-guest:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return;
        var names = JsonSerializer.Deserialize<string[]>(text[prefix.Length..]) ?? [];
        var items = ViewModel.GuestEntries.Where(item => names.Contains(item.Name, StringComparer.Ordinal) && item.Kind is "file" or "directory").ToArray();
        if (items.Length > 0 && await SelectedConflictAsync() is { } conflict)
            await TryAsync(() => ViewModel.DownloadAsync(items, ViewModel.HostPath, conflict));
    }

    private async void GuestNew_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "new-project" };
        var kind = new ComboBox { Header = "Type", ItemsSource = new[] { "Folder", "File" }, SelectedIndex = 0 };
        var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(kind); panel.Children.Add(name);
        if (await Dialog("Create item", panel, "Create", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = kind.SelectedIndex == 0 ? "mkdir" : "createFile", RelativePath = ViewModel.GuestItemPath(name.Text.Trim()) }));
    }

    private async void GuestRename_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { } item) return;
        var name = new TextBox { Header = "New name", Text = item.Name };
        if (await Dialog("Rename item", name, "Rename", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "rename", RelativePath = ViewModel.GuestItemPath(item.Name), DestinationPath = ViewModel.GuestItemPath(name.Text.Trim()), Expected = Expect(item) }));
    }

    private async void GuestDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { } item) return;
        var copyName = item.Kind == "directory" ? item.Name + " copy" : Path.GetFileNameWithoutExtension(item.Name) + " copy" + Path.GetExtension(item.Name);
        var name = new TextBox { Header = "Copy name", Text = copyName };
        if (await Dialog("Duplicate item", name, "Duplicate", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "copy", RelativePath = ViewModel.GuestItemPath(item.Name), DestinationPath = ViewModel.GuestItemPath(name.Text.Trim()), Expected = Expect(item) }));
    }

    private async void GuestMove_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { } item) return;
        var destination = new TextBox { Header = "Workspace-relative destination", PlaceholderText = "projects/my-app/" + item.Name, Text = string.Join('/', ViewModel.GuestItemPath(item.Name)) };
        if (await Dialog("Move item", destination, "Move", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        var parts = destination.Text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        GuestPathPolicy.ValidateComponents(parts);
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "move", RelativePath = ViewModel.GuestItemPath(item.Name), DestinationPath = parts, Expected = Expect(item) }));
    }

    private async void GuestTrash_Click(object sender, RoutedEventArgs e)
    {
        var items = GuestList.SelectedItems.Cast<GuestFileEntry>().ToArray();
        if (items.Length == 0) return;
        if (await Dialog("Move to trash?", $"Move {items.Length} selected item(s) to Agent Sandbox trash? They can be restored later.", "Move to trash", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await TryAsync(async () =>
        {
            foreach (var item in items)
                await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "trash", RelativePath = ViewModel.GuestItemPath(item.Name), Expected = Expect(item) }, refresh: false);
            await ViewModel.LoadGuestFilesAsync();
        });
    }

    private async void GuestEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { Kind: "file" } item) return;
        await TryAsync(async () =>
        {
            var response = await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "readText", RelativePath = ViewModel.GuestItemPath(item.Name) }, refresh: false);
            var editor = new TextBox { Text = response.Content ?? "", AcceptsReturn = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), MinWidth = 760, MinHeight = 430, TextWrapping = TextWrapping.NoWrap };
            var readOnly = ViewModel.GuestRootId == GuestRoots.System;
            editor.IsReadOnly = readOnly;
            if (readOnly) { await Dialog($"View {item.Name} (read-only)", editor, null, "Close").ShowAsync(); return; }
            if (await Dialog($"Edit {item.Name}", editor, "Save", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
            var source = response.Entries.Single();
            var content = PreserveNewlineStyle(editor.Text, response.Content ?? "");
            await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "writeText", RelativePath = ViewModel.GuestItemPath(item.Name), Content = content, Expected = Expect(source) });
        });
    }

    private async void GuestArchive_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { } item) return;
        var name = new TextBox { Header = "Archive name", Text = item.Name + ".zip" };
        if (await Dialog("Create archive", name, "Create", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "archive", RelativePath = ViewModel.GuestItemPath(item.Name), DestinationPath = ViewModel.GuestItemPath(name.Text.Trim()) }));
    }

    private async void GuestExtract_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { Kind: "file" } item) return;
        var defaultName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(item.Name));
        var name = new TextBox { Header = "Destination folder", Text = defaultName };
        if (await Dialog("Extract archive", name, "Extract", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "extract", RelativePath = ViewModel.GuestItemPath(item.Name), DestinationPath = ViewModel.GuestItemPath(name.Text.Trim()) }));
    }

    private async void GuestMode_Click(object sender, RoutedEventArgs e)
    {
        if (SingleGuestSelection() is not { } item) return;
        var mode = new TextBox { Header = "Owner/group/other mode", Text = Convert.ToString(item.Mode & 0x1FF, 8), PlaceholderText = "644" };
        if (await Dialog("Change permissions", mode, "Apply", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(mode.Text) || mode.Text.Any(ch => ch is < '0' or > '7')) { await MessageAsync("Invalid mode", "Use three octal digits, such as 644 or 755."); return; }
        var parsed = Convert.ToInt32(mode.Text, 8);
        await TryAsync(() => ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "chmod", RelativePath = ViewModel.GuestItemPath(item.Name), Mode = parsed, Expected = Expect(item) }));
    }

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SnapshotInfo snapshot) return;
        var exact = $"{snapshot.InstanceName}.{snapshot.Name}";
        var confirmation = new TextBox { Header = $"Type {exact} to confirm", PlaceholderText = exact };
        if (await Dialog("Restore snapshot destructively?", confirmation, "Restore", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        if (!string.Equals(confirmation.Text, exact, StringComparison.Ordinal)) { await MessageAsync("Confirmation did not match", "No changes were made."); return; }
        await TryAsync(() => ViewModel.RestoreSnapshotAsync(snapshot));
    }

    private async void ReviewTrash_Click(object sender, RoutedEventArgs e) => await ShowTrashAsync();

    private async Task ShowTrashAsync()
    {
        await TryAsync(async () =>
        {
            GuestFileResponse listing;
            try { listing = await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "list", RelativePath = [".agent-sandbox", "trash"] }, refresh: false); }
            catch (IOException) { await MessageAsync("Trash is empty", "No recoverable items are currently in Agent Sandbox trash."); return; }
            var items = new List<TrashDisplay>();
            foreach (var metadata in listing.Entries.Where(entry => entry.Kind == "file" && entry.Name.EndsWith(".json", StringComparison.Ordinal)))
            {
                var data = await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "readText", RelativePath = [".agent-sandbox", "trash", metadata.Name] }, refresh: false);
                using var doc = JsonDocument.Parse(data.Content ?? "{}");
                items.Add(new TrashDisplay(doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("name").GetString()!, doc.RootElement.GetProperty("deletedAt").GetInt64()));
            }
            if (items.Count == 0) { await MessageAsync("Trash is empty", "No recoverable items are currently in Agent Sandbox trash."); return; }
            var list = new ListView { ItemsSource = items, SelectionMode = ListViewSelectionMode.Single, MinWidth = 560, MinHeight = 260, DisplayMemberPath = nameof(TrashDisplay.Display) };
            var dialog = Dialog("Agent Sandbox trash", list, "Restore selected", "Close");
            dialog.SecondaryButtonText = "Permanently purge selected";
            var result = await dialog.ShowAsync();
            if (list.SelectedItem is not TrashDisplay selected || result == ContentDialogResult.None) return;
            if (result == ContentDialogResult.Primary)
                await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "restore", RelativePath = [selected.Id] });
            else if (result == ContentDialogResult.Secondary)
            {
                var typed = new TextBox { Header = $"Type {selected.Name} to permanently delete", PlaceholderText = selected.Name };
                if (await Dialog("Permanent deletion", typed, "Purge forever", "Cancel").ShowAsync() == ContentDialogResult.Primary && string.Equals(typed.Text, selected.Name, StringComparison.Ordinal))
                    await ViewModel.GuestOperationAsync(new GuestFileRequest { Operation = "purge", RelativePath = [selected.Id] });
            }
        });
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        var exact = ViewModel.CurrentSettings.InstanceName;
        var typed = new TextBox { Header = $"Type {exact} to delete and rebuild", PlaceholderText = exact };
        if (await Dialog("Rebuild sandbox?", typed, "Delete and rebuild", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        if (!string.Equals(typed.Text, exact, StringComparison.Ordinal)) { await MessageAsync("Confirmation did not match", "No changes were made."); return; }
        await TryAsync(async () => { await ViewModel.RebuildSandboxAsync(exact); SelectCurrentSandboxInPicker(); });
    }

    private async void DeleteSandbox_Click(object sender, RoutedEventArgs e)
    {
        var exact = ViewModel.CurrentSettings.InstanceName;
        var typed = new TextBox { Header = $"Type {exact} to permanently delete", PlaceholderText = exact };
        if (await Dialog("Delete VM permanently?", typed, "Delete VM", "Cancel").ShowAsync() != ContentDialogResult.Primary) return;
        if (!string.Equals(typed.Text, exact, StringComparison.Ordinal)) { await MessageAsync("Confirmation did not match", "No changes were made."); return; }
        await TryAsync(async () => { await ViewModel.DeleteSandboxAsync(exact); SelectCurrentSandboxInPicker(); });
    }

    private async void CreateSandbox_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CurrentSettings.IsReady) { await ShowSetupAsync(); return; }
        var number = 2;
        string name;
        do name = $"agent-sandbox-{number++}";
        while (ViewModel.Sandboxes.Any(item => string.Equals(item.InstanceName, name, StringComparison.Ordinal)));
        await TryAsync(() => ShowResourceSetupAsync(name));
    }

    private async void SandboxPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSandboxPicker || SandboxPicker.SelectedItem is not SandboxConfiguration selected) return;
        await TryAsync(async () =>
        {
            await ViewModel.SelectSandboxAsync(selected.InstanceName);
            SelectCurrentSandboxInPicker();
            if (FilesPanel.Visibility == Visibility.Visible && ViewModel.CanOperateSandbox)
                await ViewModel.LoadGuestFilesAsync();
        });
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is null) return;
        await TryAsync(async () => { var path = await ViewModel.ExportDiagnosticsAsync(folder.Path); await MessageAsync("Diagnostic bundle exported", path); });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var theme = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        await TryAsync(async () =>
        {
            await ViewModel.SavePreferencesAsync(theme, ReducedMotionSwitch.IsOn, UpdatesSwitch.IsOn, AdvancedBrowseSwitch.IsOn, ReleaseRepositoryBox.Text.Trim());
            RequestedTheme = theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
            await MessageAsync("Settings saved", "Appearance, privacy, update, and guest-browsing preferences were saved locally.");
        });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var theme = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        await ViewModel.SavePreferencesAsync(theme, ReducedMotionSwitch.IsOn, UpdatesSwitch.IsOn, AdvancedBrowseSwitch.IsOn, ReleaseRepositoryBox.Text.Trim());
        await ShowAvailableReleaseAsync(force: true);
    }

    private async Task ShowAvailableReleaseAsync(bool force)
    {
        try
        {
            var release = await ViewModel.CheckForUpdateAsync(force);
            if (release is null)
            {
                if (force) await MessageAsync("You’re up to date", "No newer GitHub release is available, or no repository has been configured.");
                return;
            }
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = $"Agent Sandbox {release.Version} is available.", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = release.Notes, TextWrapping = TextWrapping.Wrap, MaxHeight = 260 });
            if (await Dialog("Update available", body, "View release", "Later").ShowAsync() == ContentDialogResult.Primary)
                await Windows.System.Launcher.LaunchUriAsync(release.ReleasePage);
        }
        catch (Exception exception)
        {
            if (force) await MessageAsync("Update check failed", exception.Message);
        }
    }

    private async void EmbeddedTerminal_Click(object sender, RoutedEventArgs e)
    {
        await TryAsync(async () =>
        {
            await using var session = await App.Services.Terminal.OpenEmbeddedAsync(ViewModel.CurrentSettings.InstanceName);
            var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"), MinWidth = 820, MinHeight = 420, TextWrapping = TextWrapping.NoWrap };
            var input = new TextBox { PlaceholderText = "Type a command and press Enter" };
            var send = new Button { Content = "Send", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.Children.Add(input); Grid.SetColumn(send, 1); row.Children.Add(send);
            var panel = new Grid { RowSpacing = 8 }; panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.Children.Add(output); Grid.SetRow(row, 1); panel.Children.Add(row);
            async Task SendAsync() { if (string.IsNullOrEmpty(input.Text)) return; var bytes = Encoding.UTF8.GetBytes(input.Text + "\n"); input.Text = ""; await session.Input.WriteAsync(bytes); await session.Input.FlushAsync(); }
            send.Click += async (_, _) => await SendAsync();
            input.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await SendAsync(); } };
            _ = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await session.Output.ReadAsync(buffer);
                    if (count == 0) break;
                    var chunk = Encoding.UTF8.GetString(buffer, 0, count);
                    App.DispatcherQueue.TryEnqueue(() => { output.Text += chunk; output.SelectionStart = output.Text.Length; });
                }
            });
            await Dialog("Embedded guest terminal", panel, null, "Close").ShowAsync();
        });
    }

    private void SelectCurrentSandboxInPicker()
    {
        updatingSandboxPicker = true;
        SandboxPicker.SelectedItem = ViewModel.Sandboxes.FirstOrDefault(item =>
            string.Equals(item.InstanceName, ViewModel.CurrentSettings.InstanceName, StringComparison.Ordinal));
        updatingSandboxPicker = false;
    }

    private void LoadSettingsControls()
    {
        if (settingsLoaded) return;
        settingsLoaded = true;
        var value = ViewModel.CurrentSettings;
        ThemePicker.SelectedIndex = value.Theme switch { "Dark" => 0, "Light" => 1, _ => 2 };
        ReducedMotionSwitch.IsOn = value.ReducedMotion;
        UpdatesSwitch.IsOn = value.CheckForUpdates;
        AdvancedBrowseSwitch.IsOn = value.AdvancedGuestBrowsing;
        ReleaseRepositoryBox.Text = value.ReleaseRepository ?? "";
        RequestedTheme = value.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
    }

    private GuestFileEntry? SingleGuestSelection()
    {
        if (GuestList.SelectedItems.Count == 1) return (GuestFileEntry)GuestList.SelectedItem;
        _ = MessageAsync("Choose one item", "Select exactly one guest item for this action.");
        return null;
    }

    private static GuestFileExpectation Expect(GuestFileEntry item) => new(item.Kind, item.Size, item.ModifiedNanoseconds, item.Mode);
    private async Task<FileConflictPolicy?> SelectedConflictAsync()
    {
        var name = (ConflictPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Fail";
        var policy = Enum.Parse<FileConflictPolicy>(name, ignoreCase: true);
        if (policy != FileConflictPolicy.Overwrite) return policy;
        var typed = new TextBox { Header = "Type OVERWRITE to replace exact destination items", PlaceholderText = "OVERWRITE" };
        if (await Dialog("Confirm overwrite", typed, "Continue", "Cancel").ShowAsync() != ContentDialogResult.Primary || !string.Equals(typed.Text, "OVERWRITE", StringComparison.Ordinal))
            return null;
        return policy;
    }
    private static string PreserveNewlineStyle(string edited, string original)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : original.Contains('\r') ? "\r" : "\n";
        return edited.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newline, StringComparison.Ordinal);
    }
    private static NumberBox Number(string header, double value, double minimum, double maximum) => new() { Header = header, Value = value, Minimum = minimum, Maximum = maximum, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    private ContentDialog Dialog(string title, object content, string? primary, string close)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary };
        if (!string.IsNullOrWhiteSpace(primary)) dialog.PrimaryButtonText = primary;
        return dialog;
    }

    private async Task MessageAsync(string title, string message) => await Dialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 720 }, null, "Close").ShowAsync();
    private async Task TryAsync(Func<Task> action) { try { await action(); } catch (Exception exception) { await MessageAsync("Action needs attention", exception.Message); } }
    private static void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
    private static async Task<StorageFolder?> PickFolderAsync() { var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary }; picker.FileTypeFilter.Add("*"); InitializePicker(picker); return await picker.PickSingleFolderAsync(); }

    private sealed record TrashDisplay(string Id, string Name, long DeletedAt)
    {
        public string Display => $"{Name}  •  {DateTimeOffset.FromUnixTimeSeconds(DeletedAt).ToLocalTime():g}";
    }
}
