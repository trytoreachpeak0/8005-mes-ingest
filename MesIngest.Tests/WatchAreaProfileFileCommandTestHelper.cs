using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MesIngest.Watch;

namespace MesIngest.Tests;

internal static class WatchAreaProfileFileCommandTestHelper
{
    public static MenuItem FindSelected(
        WatchWorkspaceWindow window,
        string automationId) => Find(window, profileName: null, automationId);

    public static MenuItem Find(
        WatchWorkspaceWindow window,
        string? profileName,
        string automationId)
    {
        var list = Assert.IsType<ListBox>(window.FindName("AreaProfileList"));
        list.UpdateLayout();
        var row = profileName is null
            ? Assert.IsType<WatchAreaFilterProfilePresentationRow>(list.SelectedItem)
            : Assert.Single(
                list.Items.Cast<WatchAreaFilterProfilePresentationRow>(),
                candidate => candidate.ProfileName == profileName);
        var item = Assert.IsType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromItem(row));
        var menu = Assert.IsType<ContextMenu>(item.ContextMenu);
        menu.PlacementTarget = item;
        menu.IsOpen = true;
        DrainDispatcher(window.Dispatcher);
        return Assert.Single(
            menu.Items.Cast<MenuItem>(),
            menuItem => string.Equals(
                AutomationProperties.GetAutomationId(menuItem),
                automationId,
                StringComparison.Ordinal));
    }

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
