using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using MesIngest.Core.SeriesProjection;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace MesIngest.Watch;

internal sealed record WatchAreaProfileSelectorOption(
    string? ProfileName,
    int MesAreaCount,
    bool IsValid,
    bool IsApplied)
{
    public string DisplayText => ProfileName is null
        ? "全部 AREA"
        : IsValid
            ? $"{ProfileName} · {MesAreaCount:N0}"
            : $"{ProfileName} · 无效";

    public string AutomationName => ProfileName is null
        ? "AREA 筛选：全部 AREA"
        : IsValid
            ? $"AREA 筛选：{ProfileName}；{MesAreaCount:N0} 个 AREA"
            : $"AREA 筛选：{ProfileName}；配置无效，不能应用";
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
                                StringComparison.Ordinal),
                    })
                    .ToArray();
            }

            var selected = _dataPageAreaProfileOptions.FirstOrDefault(option =>
                    option.ProfileName is null
                        ? _areaContext.MesAreas.Count == 0
                        : string.Equals(
                            option.ProfileName,
                            _areaContext.ProfileName,
                            StringComparison.Ordinal))
                ?? _dataPageAreaProfileOptions[0];
            DemandSeriesAreaProfileSelector.ItemsSource = _dataPageAreaProfileOptions;
            ReadabilityAreaProfileSelector.ItemsSource = _dataPageAreaProfileOptions;
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
            DemandSeriesAreaProfileSelector.ItemsSource = _dataPageAreaProfileOptions;
            ReadabilityAreaProfileSelector.ItemsSource = _dataPageAreaProfileOptions;
            DemandSeriesAreaProfileSelector.SelectedIndex = 0;
            ReadabilityAreaProfileSelector.SelectedIndex = 0;
            ShowAreaProfileInfo(
                Wpf.Ui.Controls.InfoBarSeverity.Warning,
                "无法读取本机 AREA 配置",
                $"当前已应用显示范围保持不变。{exception.Message}");
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
            || selector.SelectedItem is not WatchAreaProfileSelectorOption option
            || !option.IsValid
            || IsCurrentAreaProfile(option))
        {
            return;
        }

        AreaProfileOperationTask = ApplyDataPageAreaProfileAsync(option);
    }

    private bool IsCurrentAreaProfile(WatchAreaProfileSelectorOption option) =>
        option.ProfileName is null
            ? _areaContext.MesAreas.Count == 0
            : string.Equals(
                option.ProfileName,
                _areaContext.ProfileName,
                StringComparison.Ordinal);

    private async Task ApplyDataPageAreaProfileAsync(
        WatchAreaProfileSelectorOption option)
    {
        await RunAreaProfileUiActionAsync(async () =>
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
            RenderAreaProfiles(reloadProfiles: true);
        }).ConfigureAwait(true);

        RenderDataPageAreaProfileSelectors(reloadProfiles: true);
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
        var snapshot = state.ReadabilityAudit.Snapshot;
        ReadabilityCatalogRevisionText.Text = snapshot is null
            ? "Catalog Revision —"
            : $"Catalog Revision {snapshot.Snapshot.CatalogRevision:N0}";
        AutomationProperties.SetName(
            ReadabilityCatalogRevisionPill,
            $"Host {ReadabilityCatalogRevisionText.Text}");
        ReadabilityHeaderFactsText.Text = snapshot is null
            ? $"{presentation.LocalAreaHeading} · 尚无更新时间"
            : $"{presentation.LocalAreaHeading} · 更新于 {WatchTimeDisplay.Format(snapshot.Snapshot.ProjectionCommittedAt)}";
        AutomationProperties.SetName(
            ReadabilityHeaderFactsText,
            $"资格审计 AREA 与更新时间：{ReadabilityHeaderFactsText.Text}");

        ReadabilityCompactFactsText.Text = string.Join(
            " · ",
            new[]
            {
                presentation.SnapshotFacts,
                presentation.ClientAttemptFacts,
                presentation.HostAreaScope,
                presentation.OrderSummary,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        AutomationProperties.SetName(
            ReadabilityCompactFactsText,
            $"资格审计紧凑快照事实：{ReadabilityCompactFactsText.Text}");

        var readableCount = presentation.StateFacets.FirstOrDefault(facet => string.Equals(
            facet.State,
            ExternalReadabilityStates.Readable,
            StringComparison.Ordinal))?.DemandCount ?? 0;
        var notReadableCount = presentation.StateFacets.FirstOrDefault(facet => string.Equals(
            facet.State,
            ExternalReadabilityStates.NotReadable,
            StringComparison.Ordinal))?.DemandCount ?? 0;
        ReadabilityStateAllButton.Content = snapshot is null
            ? "全部 —"
            : $"全部 {snapshot.ExactTotalDemandCount:N0}";
        ReadabilityStateReadableButton.Content = snapshot is null
            ? "外部可见 —"
            : $"外部可见 {readableCount:N0}";
        ReadabilityStateNotReadableButton.Content = snapshot is null
            ? "外部不可见 —"
            : $"外部不可见 {notReadableCount:N0}";
        ReadabilityNotReadableCountText.Text = snapshot is null
            ? "— 不可见"
            : $"{notReadableCount:N0} 不可见";
        AutomationProperties.SetName(
            ReadabilityNotReadableCountPill,
            $"Host 精确 {ReadabilityNotReadableCountText.Text}");
        ReadabilityMasterHeadingText.Text =
            $"{presentation.LocalAreaHeading}范围内的 TransportDemand";

        ReadabilityBlockerFacetSummaryText.Text = presentation.BlockerFacets.Count == 0
            ? "阻断原因精确分面：无命中"
            : "阻断原因精确分面：" + string.Join(
                " · ",
                presentation.BlockerFacets.Select(facet =>
                    $"{facet.Code} {facet.DemandCount:N0}"));
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
            ? $"{detail.DemandId} 对外可见"
            : $"{detail.DemandId} 对外不可见";
        ReadabilityDetailInfoBar.Message = isReadable
            ? "当前冻结审计快照中的全部外部可见资格检查通过。"
            : $"当前冻结审计快照的阻断条件：{detail.AllBlockersSummary}。";
        AutomationProperties.SetName(
            ReadabilityDetailInfoBar,
            $"{ReadabilityDetailInfoBar.Title}。{ReadabilityDetailInfoBar.Message}");
    }
}
