using MesIngest.Watch;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MesIngest.Tests;

/// <summary>
/// Repro for Watch crash during XAML load: FilterStatus SelectionChanged fires
/// OnFilterChanged before later-named controls (grids, GoneWindowHours, …) exist.
/// Also covers ticket 08 clipboard wiring on both grids.
/// </summary>
public class MainWindowStartupTests
{
    [Fact]
    public void MainWindow_ctor_completes_without_null_reference()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options);
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA constructor thread did not finish");
        Assert.Null(caught);
    }

    [Fact]
    public void MainWindow_attaches_clipboard_copy_to_both_grids()
    {
        Exception? caught = null;
        var ok = false;

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options);
                var demands = (DataGrid)window.FindName("DemandsGrid");
                var alerts = (DataGrid)window.FindName("AlertsGrid");

                var demandHeaders = demands.ContextMenu.Items
                    .OfType<MenuItem>()
                    .Select(i => i.Header?.ToString() ?? "")
                    .ToArray();
                var alertHeaders = alerts.ContextMenu.Items
                    .OfType<MenuItem>()
                    .Select(i => i.Header?.ToString() ?? "")
                    .ToArray();

                if (demands.SelectionUnit != DataGridSelectionUnit.CellOrRowHeader
                    || alerts.SelectionUnit != DataGridSelectionUnit.CellOrRowHeader
                    || demands.ClipboardCopyMode != DataGridClipboardCopyMode.None
                    || alerts.ClipboardCopyMode != DataGridClipboardCopyMode.None
                    || !demandHeaders.SequenceEqual(
                        new[] { "复制单元格", "复制整行", "复制整行（含列名）" })
                    || !alertHeaders.SequenceEqual(
                        new[] { "查看详情", "复制单元格", "复制整行", "复制整行（含列名）" })
                    || !demands.CommandBindings.OfType<CommandBinding>()
                        .Any(b => ReferenceEquals(b.Command, ApplicationCommands.Copy))
                    || !alerts.CommandBindings.OfType<CommandBinding>()
                        .Any(b => ReferenceEquals(b.Command, ApplicationCommands.Copy)))
                {
                    throw new InvalidOperationException(
                        "Clipboard wiring incomplete on Demands/Alerts grids."
                        + $" demands=[{string.Join(",", demandHeaders)}]"
                        + $" alerts=[{string.Join(",", alertHeaders)}]");
                }

                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(caught);
        Assert.True(ok);
    }

    [Fact]
    public void Every_visible_alert_column_is_user_sortable()
    {
        Exception? caught = null;
        var ok = false;

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var window = new MainWindow(
                    new MesIngestApiClient(http),
                    new WatchOptions
                    {
                        BaseUrl = "http://127.0.0.1:9",
                        RefreshSeconds = 60,
                    });
                var alerts = (DataGrid)window.FindName("AlertsGrid");
                var disabledHeaders = alerts.Columns
                    .Where(column => !column.CanUserSort)
                    .Select(column => column.Header?.ToString() ?? "")
                    .ToArray();

                if (!alerts.CanUserSortColumns || disabledHeaders.Length != 0)
                {
                    throw new InvalidOperationException(
                        $"All visible Alerts columns must sort; disabled=[{string.Join(",", disabledHeaders)}]");
                }

                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.True(thread.Join(0), "STA constructor thread did not finish");
        Assert.Null(caught);
        Assert.True(ok);
    }

    [Fact]
    public void Alert_view_details_menu_and_double_click_open_same_detail_window()
    {
        Exception? caught = null;
        var ok = false;

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options);
                window.Show();
                window.UpdateLayout();

                var alerts = (DataGrid)window.FindName("AlertsGrid");
                var alert = new WatchAlertDto(
                    AlertId: "alert-pathway-1",
                    Code: "REAPPEAR_AFTER_GONE",
                    Severity: "WARNING",
                    TaskType: "DIE_TO_OVEN",
                    Sublot: "S1",
                    DemandId: "new-id",
                    Message: "reappeared",
                    Details: """{"previousDemandId":"old-id","newDemandId":"new-id"}""",
                    FirstSeenAt: null,
                    LastSeenAt: null,
                    OccurrenceCount: 1,
                    IsActive: true,
                    ResolvedAt: null,
                    CreatedAt: null);
                alerts.ItemsSource = new[] { alert };
                alerts.SelectedItem = alert;

                var viewDetails = alerts.ContextMenu.Items
                    .OfType<MenuItem>()
                    .Single(i => i.Header?.ToString() == "查看详情");
                viewDetails.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                var afterMenu = window.OwnedWindows.OfType<AlertDetailWindow>().ToList();
                if (afterMenu.Count != 1 || afterMenu[0].AlertId != "alert-pathway-1")
                {
                    throw new InvalidOperationException(
                        $"Menu 查看详情 did not open expected detail: count={afterMenu.Count}");
                }

                afterMenu[0].Close();
                if (window.OwnedWindows.OfType<AlertDetailWindow>().Any())
                {
                    throw new InvalidOperationException("Detail window still open after Close");
                }

                alerts.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = alerts,
                    });

                var afterDbl = window.OwnedWindows.OfType<AlertDetailWindow>().ToList();
                if (afterDbl.Count != 1 || afterDbl[0].AlertId != "alert-pathway-1")
                {
                    throw new InvalidOperationException(
                        $"Double-click did not open expected detail: count={afterDbl.Count}");
                }

                afterDbl[0].Close();

                // Enter shares OpenSelectedAlertDetail with menu/double-click (PreviewKeyDown).
                alerts.RaiseEvent(new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)
                            ?? throw new InvalidOperationException("No PresentationSource"),
                        Environment.TickCount,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                        Source = alerts,
                    });

                var afterEnter = window.OwnedWindows.OfType<AlertDetailWindow>().ToList();
                if (afterEnter.Count != 1 || afterEnter[0].AlertId != "alert-pathway-1")
                {
                    throw new InvalidOperationException(
                        $"Enter did not open expected detail: count={afterEnter.Count}");
                }

                afterEnter[0].Close();
                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        Assert.Null(caught);
        Assert.True(ok);
    }

    [Fact]
    public void MainWindow_applies_saved_pane_ratio_and_uses_star_rows_with_splitter()
    {
        Exception? caught = null;
        var ok = false;
        var prefsPath = Path.Combine(Path.GetTempPath(), $"watch-layout-ui-{Guid.NewGuid():N}.json");
        File.WriteAllText(prefsPath, """{"version":1,"demandShare":0.55}""");

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options, layoutPreferencesPath: prefsPath);
                var demandsRow = (RowDefinition)window.FindName("DemandsRow");
                var alertsRow = (RowDefinition)window.FindName("AlertsRow");
                var splitter = (GridSplitter)window.FindName("PanesSplitter");

                if (demandsRow.Height.IsStar != true
                    || alertsRow.Height.IsStar != true
                    || Math.Abs(demandsRow.Height.Value - 0.55) > 1e-9
                    || Math.Abs(alertsRow.Height.Value - 0.45) > 1e-9
                    || demandsRow.MinHeight < 1
                    || alertsRow.MinHeight < 1
                    || splitter is null
                    || splitter.ResizeDirection != GridResizeDirection.Rows)
                {
                    throw new InvalidOperationException(
                        $"Pane layout mismatch: demand={demandsRow.Height}, alert={alertsRow.Height}");
                }

                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        try
        {
            Assert.Null(caught);
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(prefsPath))
            {
                File.Delete(prefsPath);
            }
        }
    }

    [Fact]
    public void MainWindow_double_click_splitter_resets_to_default_ratio()
    {
        Exception? caught = null;
        var ok = false;
        var prefsPath = Path.Combine(Path.GetTempPath(), $"watch-layout-ui-{Guid.NewGuid():N}.json");
        File.WriteAllText(prefsPath, """{"version":1,"demandShare":0.4}""");

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options, layoutPreferencesPath: prefsPath);
                var demandsRow = (RowDefinition)window.FindName("DemandsRow");
                var alertsRow = (RowDefinition)window.FindName("AlertsRow");
                var splitter = (GridSplitter)window.FindName("PanesSplitter");

                splitter.RaiseEvent(new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = splitter,
                    });

                if (Math.Abs(demandsRow.Height.Value - 0.7) > 1e-9
                    || Math.Abs(alertsRow.Height.Value - 0.3) > 1e-9
                    || !demandsRow.Height.IsStar
                    || !alertsRow.Height.IsStar)
                {
                    throw new InvalidOperationException(
                        $"Reset failed: demand={demandsRow.Height}, alert={alertsRow.Height}");
                }

                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        try
        {
            Assert.Null(caught);
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(prefsPath))
            {
                File.Delete(prefsPath);
            }
        }
    }

    [Fact]
    public void MainWindow_close_persists_current_pane_ratio()
    {
        Exception? caught = null;
        var ok = false;
        var prefsPath = Path.Combine(Path.GetTempPath(), $"watch-layout-ui-{Guid.NewGuid():N}.json");
        File.WriteAllText(prefsPath, """{"version":1,"demandShare":0.7}""");

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options, layoutPreferencesPath: prefsPath);
                window.Width = 1000;
                window.Height = 700;
                window.Show();
                window.UpdateLayout();

                var demandsRow = (RowDefinition)window.FindName("DemandsRow");
                var alertsRow = (RowDefinition)window.FindName("AlertsRow");
                demandsRow.Height = new GridLength(0.65, GridUnitType.Star);
                alertsRow.Height = new GridLength(0.35, GridUnitType.Star);
                window.UpdateLayout();
                window.Close();

                var saved = WatchLayoutPreferences.LoadDemandShare(prefsPath);
                if (Math.Abs(saved - 0.65) > 0.05)
                {
                    throw new InvalidOperationException($"Expected ~0.65 saved, got {saved}");
                }

                ok = true;
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        try
        {
            Assert.Null(caught);
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(prefsPath))
            {
                File.Delete(prefsPath);
            }
        }
    }

    [Fact]
    public void MainWindow_drag_completed_converts_absolute_rows_back_to_stars()
    {
        Exception? caught = null;
        var ok = false;
        var prefsPath = Path.Combine(Path.GetTempPath(), $"watch-layout-ui-{Guid.NewGuid():N}.json");

        var thread = new Thread(() =>
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };
                var client = new MesIngestApiClient(http);
                var options = new WatchOptions
                {
                    BaseUrl = "http://127.0.0.1:9",
                    RefreshSeconds = 60,
                };
                var window = new MainWindow(client, options, layoutPreferencesPath: prefsPath);
                window.Width = 1000;
                window.Height = 700;
                window.Show();
                window.UpdateLayout();

                var demandsRow = (RowDefinition)window.FindName("DemandsRow");
                var alertsRow = (RowDefinition)window.FindName("AlertsRow");
                var splitter = (GridSplitter)window.FindName("PanesSplitter");

                // Simulate post-drag Absolute heights (WPF GridSplitter behavior).
                demandsRow.Height = new GridLength(390);
                alertsRow.Height = new GridLength(210);
                window.UpdateLayout();

                splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false)
                {
                    RoutedEvent = Thumb.DragCompletedEvent,
                    Source = splitter,
                });

                if (!demandsRow.Height.IsStar
                    || !alertsRow.Height.IsStar
                    || Math.Abs(demandsRow.Height.Value - 0.65) > 0.02
                    || Math.Abs(alertsRow.Height.Value - 0.35) > 0.02)
                {
                    throw new InvalidOperationException(
                        $"Expected star 0.65/0.35 after drag, got demand={demandsRow.Height}, alert={alertsRow.Height}");
                }

                ok = true;
                window.Close();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        try
        {
            Assert.Null(caught);
            Assert.True(ok);
        }
        finally
        {
            if (File.Exists(prefsPath))
            {
                File.Delete(prefsPath);
            }
        }
    }
}
