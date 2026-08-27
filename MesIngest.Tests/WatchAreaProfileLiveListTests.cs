using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

/// <summary>
/// UI composition seam for the live directory sync: the real production window,
/// a manually driven directory event source, and an injected clock.
/// </summary>
[Collection("WpfDesktop")]
public sealed class WatchAreaProfileLiveListTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_area_page_no_longer_offers_a_manual_reload_command() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            Assert.Null(window.FindName("AreaProfileReloadButton"));
            Assert.Null(window.FindName("AreaProfileFileReloadButton"));
            Assert.NotNull(window.FindName("AreaProfileOpenDirectoryButton"));
        });

    [Fact]
    public void All_areas_stays_first_through_search_and_directory_refreshes() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var search = Assert.IsType<TextBox>(window.FindName("AreaProfileSearchInput"));

            Assert.Collection(
                AllRows(list),
                row =>
                {
                    Assert.True(row.IsAllAreas);
                    Assert.Equal("全部 AREA（不筛选）", row.ProfileName);
                },
                row => Assert.Equal("西区", row.ProfileName));

            search.Text = "不存在的配置";
            Assert.True(Assert.Single(AllRows(list)).IsAllAreas);

            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.True(Assert.Single(AllRows(list)).IsAllAreas);

            search.Clear();
            Assert.Equal(
                ["全部 AREA（不筛选）", "东区", "西区"],
                AllRows(list).Select(row => row.ProfileName));

            File.Delete(Path.Combine(directoryPath, "东区.txt"));
            events.RaiseDeleted("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);
            Assert.Equal(
                ["全部 AREA（不筛选）", "西区"],
                AllRows(list).Select(row => row.ProfileName));
        });

    [Fact]
    public void One_shared_apply_command_lives_in_the_left_card_and_all_areas_has_no_file_commands() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("AreaFilterNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();

            var masterCard = Assert.IsType<Wpf.Ui.Controls.Card>(
                window.FindName("AreaProfileMasterCard"));
            var editorCard = Assert.IsType<Wpf.Ui.Controls.Card>(
                window.FindName("AreaProfileEditorCard"));
            var apply = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("AreaProfileApplyButton"));
            Assert.True(IsVisualDescendantOf(apply, masterCard));
            Assert.False(IsVisualDescendantOf(apply, editorCard));
            Assert.Null(window.FindName("AreaApplyAllAreasButton"));

            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            list.UpdateLayout();
            var allAreas = Assert.Single(AllRows(list), row => row.IsAllAreas);
            Assert.False(allAreas.CanSaveAs);
            Assert.False(allAreas.CanRenameOrDelete);
            var item = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(allAreas));
            Assert.Null(item.ContextMenu);
        });

    [Fact]
    public void All_areas_uses_the_shared_applied_state_when_it_is_the_current_scope() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var allAreas = Assert.Single(AllRows(list), row => row.IsAllAreas);
            Assert.Equal(allAreas, list.SelectedItem);
            Assert.True(allAreas.IsApplied);
            Assert.Contains("当前应用", allAreas.AutomationName, StringComparison.Ordinal);

            var apply = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("AreaProfileApplyButton"));
            Assert.Equal("已应用", apply.Content);
            Assert.False(apply.IsEnabled);
        });

    [Fact]
    public void Selecting_all_areas_does_not_present_it_as_an_invalid_txt_file() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            Assert.Equal(
                "全部 AREA（不筛选）",
                Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    window.FindName("AreaProfileFileTitleText")).Text);
            Assert.Equal(
                "不限制显示范围",
                Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    window.FindName("AreaProfileValidCountText")).Text);
            Assert.Equal(
                "不对应 TXT 文件",
                Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    window.FindName("AreaProfileDiskStateText")).Text);
            Assert.True(Assert.IsType<TextBox>(
                window.FindName("AreaProfileEditor")).IsReadOnly);
        });

    [Fact]
    public void File_commands_live_on_the_left_card_and_each_profile_context_menu() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("AreaFilterNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();

            var masterCard = Assert.IsType<Wpf.Ui.Controls.Card>(
                window.FindName("AreaProfileMasterCard"));
            var editorCard = Assert.IsType<Wpf.Ui.Controls.Card>(
                window.FindName("AreaProfileEditorCard"));
            var openDirectory = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("AreaProfileOpenDirectoryButton"));
            var create = Assert.IsType<Wpf.Ui.Controls.Button>(
                window.FindName("AreaProfileNewButton"));
            var confirmationPanel = Assert.IsType<Border>(
                window.FindName("AreaProfileFileOperationPanel"));

            Assert.True(IsVisualDescendantOf(openDirectory, masterCard));
            Assert.True(IsVisualDescendantOf(create, masterCard));
            Assert.True(IsVisualDescendantOf(confirmationPanel, masterCard));
            Assert.False(IsVisualDescendantOf(openDirectory, editorCard));
            Assert.Null(window.FindName("AreaProfileSaveAsButton"));
            Assert.Null(window.FindName("AreaProfileRenameButton"));
            Assert.Null(window.FindName("AreaProfileDeleteButton"));

            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            list.UpdateLayout();
            var row = Assert.Single(Rows(list));
            var item = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(row));
            var menu = Assert.IsType<ContextMenu>(item.ContextMenu);
            Assert.Collection(
                menu.Items.Cast<MenuItem>(),
                menuItem => AssertMenuItem(
                    menuItem,
                    "另存为",
                    "AreaProfileSaveAsMenuItem"),
                menuItem => AssertMenuItem(
                    menuItem,
                    "重命名",
                    "AreaProfileRenameMenuItem"),
                menuItem => AssertMenuItem(
                    menuItem,
                    "删除",
                    "AreaProfileDeleteMenuItem"));
        });

    [Fact]
    public void A_context_menu_command_targets_its_own_row_and_keeps_the_confirmation_flow() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("AreaFilterNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            Assert.Equal(
                "西区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);

            var rename = FileCommand(
                window,
                "东区",
                "AreaProfileRenameMenuItem");
            Assert.True(rename.IsEnabled);
            rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(
                "东区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal(
                "东区",
                Assert.IsType<TextBox>(window.FindName("AreaProfileTargetNameInput")).Text);
            Assert.Contains(
                "重命名“东区.txt”",
                Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                    window.FindName("AreaProfileFileOperationPromptText")).Text,
                StringComparison.Ordinal);
            Assert.Equal(
                Visibility.Visible,
                Assert.IsType<Border>(
                    window.FindName("AreaProfileFileOperationPanel")).Visibility);
        });

    [Fact]
    public void English_language_reprojects_area_file_operations_editor_uia_and_confirmation_copy() =>
        RunWithAreaProfileWindow((window, _, _, _) =>
        {
            window.DisplayLanguageState.ApplyCommitted(WatchDisplayLanguage.English);
            window.UpdateLayout();
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            Assert.Equal(
                "Current AREA scope: All AREA (no filter)",
                AutomationProperties.GetName(Assert.IsAssignableFrom<FrameworkElement>(
                    window.FindName("AreaProfileFileTitleText"))));
            Assert.Equal(
                "Valid AREA configuration count: Display scope is unrestricted",
                AutomationProperties.GetName(Assert.IsAssignableFrom<FrameworkElement>(
                    window.FindName("AreaProfileValidCountText"))));
            Assert.Equal(
                "AREA profile validation: Shows all AREA values; no TXT filter is applied",
                AutomationProperties.GetName(Assert.IsAssignableFrom<FrameworkElement>(
                    window.FindName("AreaProfileValidationSummaryText"))));
            Assert.Equal(
                "AREA profile save state: No TXT file",
                AutomationProperties.GetName(Assert.IsAssignableFrom<FrameworkElement>(
                    window.FindName("AreaProfileDiskStateText"))));
            var dataPageSelector = Assert.IsType<ComboBox>(
                window.FindName("DemandSeriesAreaProfileSelector"));
            var westAreaOption = Assert.Single(
                dataPageSelector.Items.OfType<WatchAreaProfileSelectorPresentation>(),
                item => item.Option.ProfileName == "西区");
            Assert.Equal("西区 · 2", westAreaOption.DisplayText);
            Assert.Equal("AREA filter: 西区; 2 AREAs", westAreaOption.AutomationName);
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            var prompt = Assert.IsAssignableFrom<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileFileOperationPromptText"));
            var panel = Assert.IsType<Border>(window.FindName("AreaProfileFileOperationPanel"));
            var confirm = Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("AreaProfileFileOperationConfirmButton"));
            var cancel = Assert.IsAssignableFrom<ButtonBase>(
                window.FindName("AreaProfileFileOperationCancelButton"));

            Assert.IsAssignableFrom<ButtonBase>(window.FindName("AreaProfileNewButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("Name the new AREA profile", prompt.Text);
            Assert.Equal("Confirm name", confirm.Content);
            Assert.Equal("Confirm new AREA profile name", AutomationProperties.GetName(confirm));
            Assert.DoesNotMatch("[\\u3400-\\u9fff]", prompt.Text);

            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FileCommand(window, "西区", "AreaProfileSaveAsMenuItem")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal("Save the AREA profile as a new file", prompt.Text);
            Assert.Equal("Save as", confirm.Content);
            Assert.Equal(
                "Confirm saving the AREA TXT profile as a new file",
                AutomationProperties.GetName(confirm));
            Assert.DoesNotMatch("[\\u3400-\\u9fff]", prompt.Text);

            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FileCommand(window, "西区", "AreaProfileRenameMenuItem")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal("Rename “西区.txt”", prompt.Text);
            Assert.Equal("Rename", confirm.Content);
            Assert.Equal("Confirm rename 西区.txt", AutomationProperties.GetName(confirm));
            Assert.Equal(prompt.Text, AutomationProperties.GetName(panel));
            Assert.DoesNotContain("重命名", prompt.Text, StringComparison.Ordinal);

            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FileCommand(window, "西区", "AreaProfileDeleteMenuItem")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.StartsWith("Confirm deletion of “西区.txt”", prompt.Text, StringComparison.Ordinal);
            Assert.Equal("Confirm delete", confirm.Content);
            Assert.Equal("Confirm delete 西区.txt", AutomationProperties.GetName(confirm));
            Assert.Equal(prompt.Text, AutomationProperties.GetName(panel));
            Assert.DoesNotContain("删除", prompt.Text, StringComparison.Ordinal);
        });

    [Fact]
    public void A_profile_dropped_into_the_directory_joins_the_list_without_any_user_action() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));

            WriteProfile(directoryPath, "东区", "B2-2\nB2-3");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["东区", "西区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal(2, Assert.Single(Rows(list), row => row.ProfileName == "东区").MesAreaCount);
        });

    [Fact]
    public void A_profile_removed_from_the_directory_leaves_the_list_without_any_user_action() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.Equal(2, Rows(list).Count);

            File.Delete(Path.Combine(directoryPath, "东区.txt"));
            events.RaiseDeleted("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
        });

    [Fact]
    public void A_selected_profile_that_remains_deleted_leaves_the_list_without_a_reload_error() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");

            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DeleteConfirmationWindow);

            Assert.Empty(Rows(list));
            Assert.False(
                infoBar.IsOpen
                && infoBar.Title.Contains("无法", StringComparison.Ordinal));
        });

    [Fact]
    public void An_externally_renamed_selected_profile_follows_the_new_name_and_resorts_without_a_deleted_warning() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var title = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileFileTitleText"));
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            WriteProfile(directoryPath, "北区", "B2-2");
            events.RaiseCreated("北区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            var originalContent = editor.Text;

            File.Move(
                Path.Combine(directoryPath, "西区.txt"),
                Path.Combine(directoryPath, "东区.txt"));
            events.RaiseRenamed("东区.txt", "西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["东区", "北区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal(
                "东区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal("东区.txt", title.Text);
            Assert.Equal(originalContent, editor.Text);
            Assert.False(infoBar.IsOpen && infoBar.Title.Contains("删除", StringComparison.Ordinal));
        });

    [Fact]
    public void An_editor_atomic_replace_keeps_the_selected_row_visible_and_reloads_the_clean_editor() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileInfoBar"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");

            File.Delete(Path.Combine(directoryPath, "西区.txt"));
            events.RaiseDeleted("西区.txt");
            clock.Advance(
                WatchAreaFilterProfileStore.DeleteConfirmationWindow
                    - TimeSpan.FromMilliseconds(50));

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal("A1-1\nA1-2", editor.Text);

            WriteProfile(directoryPath, "西区", "B2-2\nB2-3");
            events.RaiseCreated("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
            Assert.Equal("B2-2\nB2-3", editor.Text);
            Assert.False(infoBar.IsOpen && infoBar.Title.Contains("删除", StringComparison.Ordinal));
        });

    [Fact]
    public void An_externally_renamed_profile_keeps_the_local_dirty_buffer_under_the_new_name() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var title = Assert.IsType<Wpf.Ui.Controls.TextBlock>(
                window.FindName("AreaProfileFileTitleText"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Text += "\n# 本机尚未保存";
            var dirtyBuffer = editor.Text;

            File.Move(
                Path.Combine(directoryPath, "西区.txt"),
                Path.Combine(directoryPath, "东区.txt"));
            events.RaiseRenamed("东区.txt", "西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                "东区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal("东区.txt", title.Text);
            Assert.Equal(dirtyBuffer, editor.Text);
        });

    [Fact]
    public void A_live_list_refresh_keeps_the_selected_profile_the_unsaved_draft_and_the_focus() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Text += "\n# 未保存的本机草稿";
            var unsavedDraft = editor.Text;
            editor.Focus();
            Assert.Same(editor, FocusManager.GetFocusedElement(window));

            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(2, Rows(list).Count);
            Assert.Equal(
                "西区",
                Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
                    .ProfileName);
            Assert.Equal(unsavedDraft, editor.Text);
            Assert.Same(editor, FocusManager.GetFocusedElement(window));
        });

    [Fact]
    public void Companion_files_dropped_next_to_a_profile_never_join_the_list() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));

            File.WriteAllText(
                Path.Combine(directoryPath, ".西区.txt.swp"),
                "B2-2",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(directoryPath, "西区.txtbackup"),
                "B2-2",
                new UTF8Encoding(false));
            events.RaiseCreated(".西区.txt.swp");
            events.RaiseCreated("西区.txtbackup");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(
                ["西区"],
                Rows(list).Select(row => row.ProfileName));
        });

    [Fact]
    public void An_external_rewrite_refreshes_a_clean_editor_on_the_ui_thread_without_losing_its_view() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("AreaFilterNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.Height = 720;
            editor.Height = 120;
            editor.Width = 500;
            editor.ApplyTemplate();
            window.UpdateLayout();
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Focus();
            editor.CaretIndex = editor.GetCharacterIndexFromLineIndex(50) + 84;
            for (var index = 0; index < 4; index++)
            {
                EditingCommands.SelectLeftByCharacter.Execute(null, editor);
            }
            Assert.Equal(4, editor.SelectionLength);
            editor.ScrollToLine(45);
            editor.UpdateLayout();
            var editorScrollViewer = Assert.IsAssignableFrom<ScrollViewer>(
                FindVisualDescendant<ScrollViewer>(editor));
            editorScrollViewer.ScrollToHorizontalOffset(160);
            editor.UpdateLayout();
            var expectedCaret = editor.CaretIndex;
            var expectedSelectionStart = editor.SelectionStart;
            var expectedSelectionLength = editor.SelectionLength;
            var expectedHorizontalOffset = editorScrollViewer.HorizontalOffset;
            var expectedVerticalOffset = editorScrollViewer.VerticalOffset;
            Assert.True(expectedHorizontalOffset > 0);
            Assert.True(expectedVerticalOffset > 0);
            var textChangeCount = 0;
            var everyTextChangeWasOnTheUiThread = true;
            editor.TextChanged += (_, _) =>
            {
                textChangeCount++;
                everyTextChangeWasOnTheUiThread &= window.Dispatcher.CheckAccess();
            };
            var externalContent = ProfileContent("B2", 80);

            WriteProfile(directoryPath, "西区", externalContent);
            Task.Run(() =>
            {
                events.RaiseChanged("西区.txt");
                clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            }).GetAwaiter().GetResult();
            DrainDispatcher(window.Dispatcher);

            Assert.Equal(externalContent, editor.Text);
            Assert.Equal(80, Assert.Single(Rows(list), row => row.ProfileName == "西区").MesAreaCount);
            Assert.Equal(expectedCaret, editor.CaretIndex);
            Assert.Equal(expectedSelectionStart, editor.SelectionStart);
            Assert.Equal(expectedSelectionLength, editor.SelectionLength);
            Assert.Equal(expectedHorizontalOffset, editorScrollViewer.HorizontalOffset, precision: 3);
            Assert.Equal(expectedVerticalOffset, editorScrollViewer.VerticalOffset, precision: 3);
            Assert.Equal(1, textChangeCount);
            Assert.True(everyTextChangeWasOnTheUiThread);
            Assert.Same(editor, FocusManager.GetFocusedElement(window));
        }, ProfileContent("A1", 80));

    [Fact]
    public void An_external_save_with_identical_bytes_does_not_reload_the_editor() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            editor.Select(3, 4);
            var expectedCaret = editor.CaretIndex;
            var expectedSelectionStart = editor.SelectionStart;
            var expectedSelectionLength = editor.SelectionLength;
            var textChangeCount = 0;
            editor.TextChanged += (_, _) => textChangeCount++;

            WriteProfile(directoryPath, "西区", editor.Text);
            events.RaiseChanged("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);

            Assert.Equal(0, textChangeCount);
            Assert.Equal(expectedCaret, editor.CaretIndex);
            Assert.Equal(expectedSelectionStart, editor.SelectionStart);
            Assert.Equal(expectedSelectionLength, editor.SelectionLength);
        });

    [Fact]
    public void A_locked_external_write_reports_an_error_only_after_the_editor_retries_are_exhausted() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            var editor = Assert.IsType<TextBox>(window.FindName("AreaProfileEditor"));
            var notifications = Assert.IsType<ItemsControl>(
                window.FindName("NotificationItemsControl"));
            list.SelectedItem = Assert.Single(Rows(list), row => row.ProfileName == "西区");
            var originalContent = editor.Text;
            using var writer = ExclusiveFileWriter.WriteUtf8AndHold(
                Path.Combine(directoryPath, "西区.txt"),
                "B2-2");

            events.RaiseChanged("西区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.False(window.AreaProfileOperationTask.IsCompleted);
            for (var index = 0;
                 index < WatchAreaFilterProfileStore.ExternalReadRetryDelays.Count;
                 index++)
            {
                clock.Advance(WatchAreaFilterProfileStore.ExternalReadRetryDelays[index]);
                if (index + 1 < WatchAreaFilterProfileStore.ExternalReadRetryDelays.Count)
                {
                    Assert.DoesNotContain(
                        notifications.Items.Cast<object>(),
                        item => NotificationTitle(item) == "无法完成 AREA 配置操作");
                }
            }

            PumpUntilCompleted(window.Dispatcher, window.AreaProfileOperationTask);

            Assert.Equal(originalContent, editor.Text);
            Assert.Contains(
                notifications.Items.Cast<object>(),
                item => NotificationTitle(item) == "无法完成 AREA 配置操作");
        });

    [Fact]
    public void An_unwatchable_directory_is_reported_instead_of_pretending_the_list_is_live() =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-area-live-list-{Guid.NewGuid():N}");
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", "A1-1\nA1-2");
            var events = new ManualAreaProfileDirectoryEventSource { FailNextStart = true };
            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
                    new WatchOptions
                    {
                        BaseUrl = "http://127.0.0.1:5088",
                        RenderingMode = WatchRenderingMode.SoftwareOnly,
                    },
                    connectionPreferencesPath: Path.Combine(root, "connection.json"),
                    workspacePreferencesPath: Path.Combine(root, "workspace.json"),
                    timeProvider: new ManualTimerTimeProvider(StartedAt),
                    areaFilterProfilesDirectoryPath: areaProfilesPath,
                    areaProfileDirectoryEventSource: events);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                try
                {
                    Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                            window.FindName("AreaFilterNavigationItem"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    var infoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                        window.FindName("AreaProfileDirectoryWatchInfoBar"));
                    Assert.True(infoBar.IsOpen);
                    Assert.Equal("AREA 配置目录监视已降级", infoBar.Title);
                    Assert.Contains("可能不是最新", infoBar.Message, StringComparison.Ordinal);
                    Assert.Equal(
                        ["西区"],
                        Rows(Assert.IsType<ListBox>(window.FindName("AreaProfileList")))
                            .Select(row => row.ProfileName));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });

    [Fact]
    public void A_deleted_directory_shows_persistent_degradation_and_clears_it_after_recovery() =>
        RunWithAreaProfileWindow((window, directoryPath, _, clock) =>
        {
            var watchInfoBar = Assert.IsType<Wpf.Ui.Controls.InfoBar>(
                window.FindName("AreaProfileDirectoryWatchInfoBar"));
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));

            Directory.Delete(directoryPath, recursive: true);
            clock.Advance(WatchAreaFilterProfileStore.DirectoryWatchHealthCheckInterval);

            Assert.True(watchInfoBar.IsOpen);
            Assert.Equal("AREA 配置目录监视已降级", watchInfoBar.Title);
            Assert.Contains("可能不是最新", watchInfoBar.Message, StringComparison.Ordinal);
            Assert.Empty(Rows(list));

            Directory.CreateDirectory(directoryPath);
            WriteProfile(directoryPath, "东区", "B2-2");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryWatchRecoveryInterval);

            Assert.False(watchInfoBar.IsOpen);
            Assert.Equal(["东区"], Rows(list).Select(row => row.ProfileName));
        });

    [Fact]
    public void A_directory_change_already_queued_when_the_window_closes_is_ignored() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            Exception? dispatcherException = null;
            DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
            {
                dispatcherException = args.Exception;
                args.Handled = true;
            };
            window.Dispatcher.UnhandledException += handler;
            try
            {
                var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
                list.SelectedItem = Assert.Single(
                    Rows(list),
                    row => row.ProfileName == "西区");
                WriteProfile(directoryPath, "西区", "B2-2");
                Task.Run(() =>
                {
                    events.RaiseChanged("西区.txt");
                    clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
                }).GetAwaiter().GetResult();

                window.Close();
                DrainDispatcher(window.Dispatcher);

                Assert.Null(dispatcherException);
                Assert.True(events.IsDisposed);
            }
            finally
            {
                window.Dispatcher.UnhandledException -= handler;
            }
        });

    [Fact]
    public void Leaving_the_area_page_stops_watching_returning_rescans_and_closing_disposes() =>
        RunWithAreaProfileWindow((window, directoryPath, events, clock) =>
        {
            var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
            Assert.True(events.IsStarted);

            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("OverviewNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.False(events.IsStarted);
            Assert.Equal(1, events.StopCount);
            WriteProfile(directoryPath, "东区", "B2-2");
            events.RaiseCreated("东区.txt");
            clock.Advance(WatchAreaFilterProfileStore.DirectoryChangeDebounceWindow);
            Assert.DoesNotContain(Rows(list), row => row.ProfileName == "东区");

            Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                    window.FindName("AreaFilterNavigationItem"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(events.IsStarted);
            Assert.Equal(2, events.StartCount);
            Assert.Contains(Rows(list), row => row.ProfileName == "东区");

            window.Close();
            Assert.True(events.IsDisposed);
        });

    private static IReadOnlyList<WatchAreaFilterProfilePresentationRow> Rows(ListBox list) =>
        [.. AllRows(list).Where(row => !row.IsAllAreas)];

    private static IReadOnlyList<WatchAreaFilterProfilePresentationRow> AllRows(ListBox list) =>
        [.. list.Items.Cast<WatchAreaFilterProfilePresentationRow>()];

    private static void AssertMenuItem(
        MenuItem menuItem,
        string expectedHeader,
        string expectedAutomationId)
    {
        Assert.Equal(expectedHeader, menuItem.Header);
        Assert.Equal(expectedAutomationId, AutomationProperties.GetAutomationId(menuItem));
    }

    private static MenuItem FileCommand(
        WatchWorkspaceWindow window,
        string profileName,
        string automationId) => WatchAreaProfileFileCommandTestHelper.Find(
            window,
            profileName,
            automationId);

    private static bool IsVisualDescendantOf(
        DependencyObject candidate,
        DependencyObject ancestor)
    {
        for (var current = candidate;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteProfile(string directoryPath, string profileName, string content) =>
        File.WriteAllText(
            Path.Combine(directoryPath, $"{profileName}.txt"),
            content,
            new UTF8Encoding(false));

    private static string ProfileContent(string areaPrefix, int lineCount) =>
        string.Join(
            '\n',
            [
                .. Enumerable.Range(1, lineCount).Select(index =>
                    $"# {index:D2} {new string('x', 160)}"),
                .. Enumerable.Range(1, lineCount).Select(index => $"{areaPrefix}-{index}"),
            ]);

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntilCompleted(Dispatcher dispatcher, Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () => frame.Continue = false),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
        DrainDispatcher(dispatcher);
    }

    private static string? NotificationTitle(object item) =>
        item.GetType().GetProperty("Title")?.GetValue(item) as string;

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate)
            {
                return candidate;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void RunWithAreaProfileWindow(
        Action<WatchWorkspaceWindow,
            string,
            ManualAreaProfileDirectoryEventSource,
            ManualTimerTimeProvider> assert,
        string initialContent = "A1-1\nA1-2") =>
        StaTestRunner.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"watch-area-live-list-{Guid.NewGuid():N}");
            var areaProfilesPath = Path.Combine(root, "area-filters");
            Directory.CreateDirectory(areaProfilesPath);
            WriteProfile(areaProfilesPath, "西区", initialContent);
            var clock = new ManualTimerTimeProvider(StartedAt);
            var events = new ManualAreaProfileDirectoryEventSource();
            try
            {
                using var composition = WatchV2ApplicationComposition.Create(
                    new WatchOptions
                    {
                        BaseUrl = "http://127.0.0.1:5088",
                        SharedSecret = "external-only",
                        RequestTimeoutSeconds = 30,
                        RenderingMode = WatchRenderingMode.SoftwareOnly,
                    },
                    connectionPreferencesPath: Path.Combine(root, "connection.json"),
                    workspacePreferencesPath: Path.Combine(root, "workspace.json"),
                    timeProvider: clock,
                    areaFilterProfilesDirectoryPath: areaProfilesPath,
                    areaProfileDirectoryEventSource: events);
                var window = composition.CreateMainWindow(initializeOnLoaded: false);
                try
                {
                    window.Show();
                    DrainDispatcher(window.Dispatcher);
                    Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(
                            window.FindName("AreaFilterNavigationItem"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(areaProfilesPath, events.StartedDirectoryPath);
                    assert(window, areaProfilesPath, events, clock);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
}
