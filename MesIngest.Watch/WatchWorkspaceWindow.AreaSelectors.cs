using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using MesIngest.Core.SeriesProjection;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace MesIngest.Watch;

internal sealed record WatchAreaProfileSelectorOption(
    string? ProfileName,
    int MesAreaCount,
    bool IsValid,
    bool IsApplied);

internal sealed record WatchAreaProfileSelectorPresentation(
    WatchAreaProfileSelectorOption Option,
    string DisplayText,
    string AutomationName)
{
    public bool IsValid => Option.IsValid;
}

internal partial class WatchWorkspaceWindow
{
    private IReadOnlyList<WatchAreaProfileSelectorOption> _dataPageAreaProfileOptions = [];
    private IReadOnlyList<string> _readabilityStateDraft = [];
    private bool _isRenderingDataPageAreaProfileSelectors;

    private void InitializeDataPageAreaProfileSelectors()
    {
        DemandSeriesAreaProfileSelector.SelectionChanged +=
            OnDataPageAreaProfileSelectionChanged;
        ReadabilityAreaProfileSelector.SelectionChanged +=
            OnDataPageAreaProfileSelectionChanged;
        DemandSeriesAreaProfileSelector.DropDownOpened +=
            OnDataPageAreaProfileDropDownOpened;
        ReadabilityAreaProfileSelector.DropDownOpened +=
            OnDataPageAreaProfileDropDownOpened;
        RenderDataPageAreaProfileSelectors(reloadProfiles: true);
    }

    private void RenderDataPageAreaProfileSelectors(bool reloadProfiles = false)
    {
        if (DemandSeriesAreaProfileSelector is null
            || ReadabilityAreaProfileSelector is null
            || _areaProfileStore is null)
        {
            return;
        }

        _isRenderingDataPageAreaProfileSelectors = true;
        try
        {
            if (reloadProfiles || _dataPageAreaProfileOptions.Count == 0)
            {
                var profiles = _areaProfileStore.EnumerateProfiles()
                    .Select(summary =>
                    {
                        var profile = _areaProfileStore.Load(summary.ProfileName);
                        return new WatchAreaProfileSelectorOption(
                            summary.ProfileName,
                            profile.MesAreas.Count,
                            profile.IsValid,
                            summary.IsApplied);
                    })
                    .ToArray();
                _dataPageAreaProfileOptions =
                [
                    new WatchAreaProfileSelectorOption(
                        ProfileName: null,
                        MesAreaCount: 0,
                        IsValid: true,
                        IsApplied: _areaContext.MesAreas.Count == 0),
                    .. profiles,
                ];
            }
            else
            {
                _dataPageAreaProfileOptions = _dataPageAreaProfileOptions
                    .Select(option => option with
                    {
                        IsApplied = option.ProfileName is null
                            ? _areaContext.MesAreas.Count == 0
                            : string.Equals(
                                option.ProfileName,
                                _areaContext.ProfileName,
                                StringComparison.OrdinalIgnoreCase),
                    })
                    .ToArray();
            }

            var selectedOption = _dataPageAreaProfileOptions.FirstOrDefault(option =>
                    option.ProfileName is null
                        ? _areaContext.MesAreas.Count == 0
                        : string.Equals(
                            option.ProfileName,
                            _areaContext.ProfileName,
                            StringComparison.OrdinalIgnoreCase))
                ?? _dataPageAreaProfileOptions[0];
            var text = _displayLanguageState.Catalog.AreaFilter;
            var presentations = _dataPageAreaProfileOptions
                .Select(option => new WatchAreaProfileSelectorPresentation(
                    option,
                    text.SelectorDisplay(
                        option.ProfileName,
                        option.MesAreaCount,
                        option.IsValid),
                    text.SelectorAutomation(
                        option.ProfileName,
                        option.MesAreaCount,
                        option.IsValid)))
                .ToArray();
            var selected = presentations.Single(item => item.Option == selectedOption);
            DemandSeriesAreaProfileSelector.ItemsSource = presentations;
            ReadabilityAreaProfileSelector.ItemsSource = presentations;
            DemandSeriesAreaProfileSelector.SelectedItem = selected;
            ReadabilityAreaProfileSelector.SelectedItem = selected;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _dataPageAreaProfileOptions =
            [
                new WatchAreaProfileSelectorOption(
                    ProfileName: _areaContext.MesAreas.Count == 0
                        ? null
                        : _areaContext.ProfileName,
                    MesAreaCount: _areaContext.MesAreas.Count,
                    IsValid: true,
                    IsApplied: true),
            ];
            var option = _dataPageAreaProfileOptions[0];
            var text = _displayLanguageState.Catalog.AreaFilter;
            var presentations = new[]
            {
                new WatchAreaProfileSelectorPresentation(
                    option,
                    text.SelectorDisplay(option.ProfileName, option.MesAreaCount, option.IsValid),
                    text.SelectorAutomation(option.ProfileName, option.MesAreaCount, option.IsValid)),
            };
            DemandSeriesAreaProfileSelector.ItemsSource = presentations;
            ReadabilityAreaProfileSelector.ItemsSource = presentations;
            DemandSeriesAreaProfileSelector.SelectedIndex = 0;
            ReadabilityAreaProfileSelector.SelectedIndex = 0;
            ShowAreaProfileInfo(
                Wpf.Ui.Controls.InfoBarSeverity.Warning,
                _displayLanguageState.Catalog.AreaFilter.ReadProfilesFailed(
                    exception.Message,
                    preserveAppliedScope: true));
        }
        finally
        {
            _isRenderingDataPageAreaProfileSelectors = false;
        }
    }

    private void OnDataPageAreaProfileDropDownOpened(object? sender, EventArgs e) =>
        RenderDataPageAreaProfileSelectors(reloadProfiles: true);

    private void OnDataPageAreaProfileSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRenderingDataPageAreaProfileSelectors
            || sender is not ComboBox selector
            || selector.SelectedItem is not WatchAreaProfileSelectorPresentation presentation
            || presentation.Option is not { } option
            || !option.IsValid
            || IsCurrentAreaProfile(option))
        {
            return;
        }

        var focusToPreserve = Keyboard.FocusedElement;
        CloseAreaProfileFileOperation(restoreInvokerFocus: false);
        AreaProfileOperationTask = ApplyDataPageAreaProfileAsync(option, focusToPreserve);
    }

    private bool IsCurrentAreaProfile(WatchAreaProfileSelectorOption option) =>
        option.ProfileName is null
            ? _areaContext.MesAreas.Count == 0
            : string.Equals(
                option.ProfileName,
                _areaContext.ProfileName,
                StringComparison.OrdinalIgnoreCase);

    private async Task ApplyDataPageAreaProfileAsync(
        WatchAreaProfileSelectorOption option,
        IInputElement? focusToPreserve)
    {
        var renderedCurrentOperation = false;
        await RunAreaProfileUiActionAsync(async operation =>
        {
            WatchAppliedAreaFilterProfile applied;
            if (option.ProfileName is null)
            {
                applied = _areaProfileStore.ApplyAllAreas().CurrentApplied;
            }
            else
            {
                var result = _areaProfileStore.Apply(option.ProfileName);
                if (!result.Applied)
                {
                    throw new InvalidOperationException(
                        ProjectAreaDiagnostics(result.Diagnostics));
                }

                _selectedAreaProfileName = result.Draft.ProfileName;
                _areaProfileDraft = result.Draft;
                _areaProfileDraftIsDirty = false;
                applied = result.CurrentApplied;
            }

            await ApplyAreaContextAsync(
                    applied.ToDisplayContext(),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (!IsCurrentAreaProfileOperation(operation))
            {
                return;
            }

            RenderAreaProfiles(reloadProfiles: true);
            renderedCurrentOperation = true;
        }).ConfigureAwait(true);

        if (renderedCurrentOperation)
        {
            RenderDataPageAreaProfileSelectors(reloadProfiles: true);
        }

        if (focusToPreserve is UIElement { IsVisible: true, IsEnabled: true } element
            && ReferenceEquals(Keyboard.FocusedElement, this))
        {
            element.Focus();
        }
    }

    private IReadOnlyList<string> ReadReadabilityStateDraft() =>
        _readabilityStateDraft;

    private void SetReadabilityStateDraft(IReadOnlyList<string> values)
    {
        _readabilityStateDraft = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allSelected = _readabilityStateDraft.Count == 0;
        var readableSelected = _readabilityStateDraft.Count == 1
            && string.Equals(
                _readabilityStateDraft[0],
                MesIngest.Core.SeriesProjection.ExternalReadabilityStates.Readable,
                StringComparison.Ordinal);
        var notReadableSelected = _readabilityStateDraft.Count == 1
            && string.Equals(
                _readabilityStateDraft[0],
                MesIngest.Core.SeriesProjection.ExternalReadabilityStates.NotReadable,
                StringComparison.Ordinal);
        ReadabilityStateAllButton.Appearance = allSelected
            ? ControlAppearance.Primary
            : ControlAppearance.Transparent;
        ReadabilityStateReadableButton.Appearance = readableSelected
                    ? ControlAppearance.Primary
                    : ControlAppearance.Transparent;
        ReadabilityStateNotReadableButton.Appearance = notReadableSelected
                    ? ControlAppearance.Primary
                    : ControlAppearance.Transparent;
        SetSegmentSelectionStatus(ReadabilityStateAllButton, allSelected);
        SetSegmentSelectionStatus(ReadabilityStateReadableButton, readableSelected);
        SetSegmentSelectionStatus(ReadabilityStateNotReadableButton, notReadableSelected);
    }

    private void OnReadabilityStateSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: string value })
        {
            SetReadabilityStateDraft(string.IsNullOrWhiteSpace(value) ? [] : [value]);
            UpdateReadabilityClearFilterState();
        }
    }

    private void RenderSelectedReadabilityAudit(
        WatchReadabilityAuditPresentation presentation,
        WatchV2WorkspaceState state)
    {
        var text = _displayLanguageState.Catalog.ReadabilityAudit;
        var common = _displayLanguageState.Catalog.Common;
        var snapshot = state.ReadabilityAudit.Snapshot;
        ReadabilityCatalogRevisionText.Text = snapshot is null
            ? text.RevisionNotLoaded
            : $"Catalog Revision {snapshot.Snapshot.CatalogRevision:N0}";
        AutomationProperties.SetName(
            ReadabilityCatalogRevisionPill,
            text.RevisionAutomation(ReadabilityCatalogRevisionText.Text));
        var readabilityHeaderFullFacts = string.Join(
            " · ",
            new[]
            {
                text.LocalAreaHeader(presentation.LocalAreaHeading),
                presentation.LocalAreaDetail,
                presentation.HostAreaScope,
                presentation.SnapshotFacts,
                presentation.ClientAttemptFacts,
                presentation.OrderSummary,
                presentation.HostFilterSummary,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var conciseHostAreaScope = FormatConciseHostAreaScope(
            snapshot?.Filter.Normalize().MesAreas,
            presentation.HasSnapshot);
        var readabilityFreshness = snapshot is null
            ? _displayLanguageState.Catalog.Common.NotLoaded
            : _displayLanguageState.Catalog.FormatAbsoluteTime(snapshot.Snapshot.ProjectionCommittedAt);
        ReadabilityHeaderFactsText.Text =
            text.HeaderFacts(
                presentation.LocalAreaHeading,
                conciseHostAreaScope,
                readabilityFreshness);
        ReadabilityHeaderFactsText.ToolTip = readabilityHeaderFullFacts;
        AutomationProperties.SetHelpText(
            ReadabilityHeaderFactsText,
            readabilityHeaderFullFacts);
        AutomationProperties.SetName(
            ReadabilityHeaderFactsText,
            text.HeaderAutomation(readabilityHeaderFullFacts));

        var compactSnapshotFacts = string.Join(
            " · ",
            new[]
            {
                text.SnapshotReference(snapshot?.SnapshotReference),
                presentation.SnapshotFacts,
                presentation.ClientAttemptFacts,
                presentation.HostAreaScope,
                presentation.OrderSummary,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var readableCount = presentation.StateFacets.FirstOrDefault(facet => string.Equals(
            facet.State,
            ExternalReadabilityStates.Readable,
            StringComparison.Ordinal))?.DemandCount ?? 0;
        var notReadableCount = presentation.StateFacets.FirstOrDefault(facet => string.Equals(
            facet.State,
            ExternalReadabilityStates.NotReadable,
            StringComparison.Ordinal))?.DemandCount ?? 0;
        ReadabilityStateAllButton.Content = snapshot is null
            ? text.StateCount(text.All, common.NotLoaded)
            : text.StateCount(text.All, $"{snapshot.ExactTotalDemandCount:N0}");
        ReadabilityStateReadableButton.Content = snapshot is null
            ? text.StateCount(text.Readable, common.NotLoaded)
            : text.StateCount(text.Readable, $"{readableCount:N0}");
        ReadabilityStateNotReadableButton.Content = snapshot is null
            ? text.StateCount(text.NotReadable, common.NotLoaded)
            : text.StateCount(text.NotReadable, $"{notReadableCount:N0}");
        ReadabilityNotReadableCountText.Text = snapshot is null
            ? text.StateCount(common.NotLoaded, text.NotReadable)
            : text.StateCount($"{notReadableCount:N0}", text.NotReadable);
        AutomationProperties.SetName(
            ReadabilityNotReadableCountPill,
            text.ExactCountAutomation(ReadabilityNotReadableCountText.Text));
        ReadabilityMasterHeadingText.Text = text.MasterHeading;

        ReadabilityBlockerFacetSummaryText.Text = text.BlockerFacetSummary(
            presentation.BlockerFacets,
            snapshot is not null);
        ReadabilityCompactFactsText.Text = string.Join(
            " · ",
            new[]
            {
                text.CompactOverlap,
                compactSnapshotFacts,
                ReadabilityBlockerFacetSummaryText.Text,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        AutomationProperties.SetName(
            ReadabilityCompactFactsText,
            text.CompactAutomation(ReadabilityCompactFactsText.Text));
        AutomationProperties.SetHelpText(
            ReadabilityCompactFactsText,
            ReadabilityCompactFactsText.Text);
        AutomationProperties.SetHelpText(
            ReadabilityFilterPanel,
            ReadabilityCompactFactsText.Text);
        ToolTipService.SetToolTip(
            ReadabilityFilterPanel,
            ReadabilityCompactFactsText.Text);
        AutomationProperties.SetName(
            ReadabilityBlockerFacetSummaryText,
            ReadabilityBlockerFacetSummaryText.Text);

        if (presentation.Detail is not { } detail)
        {
            return;
        }

        var isReadable = string.Equals(
            detail.ExternalReadabilityState,
            ExternalReadabilityStates.Readable,
            StringComparison.Ordinal);
        ReadabilityDetailInfoBar.IsOpen = true;
        ReadabilityDetailInfoBar.Severity = isReadable
            ? Wpf.Ui.Controls.InfoBarSeverity.Success
            : Wpf.Ui.Controls.InfoBarSeverity.Warning;
        ReadabilityDetailInfoBar.Title = isReadable
            ? text.DetailConclusion(detail.DemandId, readable: true)
            : text.DetailConclusion(detail.DemandId, readable: false);
        ReadabilityDetailInfoBar.Message = text.DetailMessage(
            isReadable,
            detail.AllBlockersSummary);
        AutomationProperties.SetName(
            ReadabilityDetailInfoBar,
            text.DetailInfoAutomation(
                ReadabilityDetailInfoBar.Title,
                ReadabilityDetailInfoBar.Message));
    }
}
