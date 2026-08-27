# Selected Fluent direction: DemandSeries + readability audit

> PROTOTYPE decision record. Do not treat this XAML as production code.

## Composition

- Shell: the reviewed A direction's `NavigationView` with `LeftMinimal` compact
  rail and an expandable 224 epx pane.
- DemandSeries: every series remains browsable across Tracking, GONE, Archived,
  and visible-after-archive states. A selected series exposes its Demand
  generations, lifecycle milestones, and immutable `DemandSeriesEvent` stream.
- Readability audit: shows every current Demand, distinguishes current external
  readability from invisibility, and explains exact reasons including data
  blockers, GONE state, and archived Series.
- AREA filtering extension: both data pages expose the same local
  AreaFilterProfile selector. The selected AREA 筛选 page is **Variant A —
  master-detail editor**: a profile list remains visible on the left while the
  selected TXT file, validation state, file commands, and save/apply state fill
  the right-hand workspace.
- Variants B (status summary + edit drawer) and C (file workspace) remain only
  as rejected prototype comparisons; they are not production directions.
- Overview, read-only IngestAlert, compact Host state, and Settings remain.
- Dispatch AREA scope, station mapping, route cost, nearest task, claiming,
  suppression, and orders remain outside Watch.

## Status placement

- Application identity exists only in the integrated WPF UI `TitleBar`.
- Host connection is a persistent `NavigationView` footer entry. Expanded mode
  shows text; compact mode retains the named icon and tooltip.
- Endpoint, last success, and effective auto-refresh policy are secondary text
  under the current page title.
- New actionable failures receive one overlay toast, then remain represented by
  stable compact fault status beside the affected page title. They do not insert
  or remove a layout row.
- Read-only scope is explained in Overview and Settings, not repeated in chrome.
- Keyboard hints live in tooltips/help, not a persistent bottom strip.

## Refresh placement

- Automatic refresh is always on for Overview, VISIBLE, GONE, and IngestAlert;
  Settings contains only their intervals.
- A page header displays only the effective policy, such as `自动刷新 10 秒`.
- Normal automatic-refresh start/success updates fixed freshness/progress context
  without opening a toast or layout-participating message.
- No page exposes Refresh, Cancel-refresh, or an automatic-refresh
  enable/disable switch.
- Pagination offers Previous/Next, total pages, nearby page buttons where space
  permits, and direct numeric page jump. The production Host contract therefore
  needs stable page-index queries plus total item/page counts.

## Review matrix

| Page | Primary sample | Additional state |
| --- | --- | --- |
| 概览 | connected, alerts active | Host offline with retained successful window |
| 任务浏览 | VISIBLE selected detail | GONE selected detail and direct page jump |
| 接入告警 | 活动告警及关联任务 | 自动恢复的历史告警 (`IsActive=false`) |
| 设置 | valid Host and refresh preferences | timeout validation error |

## Production acceptance reminders

The production implementation must be rewritten against real view state, retain
semantic UI Automation names, use theme resources instead of prototype literal
colors, reflow at minimum width and DPI scales, and follow the golden renderer's
preview/approval/baseline order.

## AREA prototype verdict

User selection on 2026-08-12: **Variant A is approved as the design direction.**
This approval selects the information hierarchy only. A production implementation
must reproduce the confirmed behavior from the AREA design interview and then
receive a fresh real-window golden-machine preview approval.

## Error-search prototype verdict

User selection on 2026-08-12: **Variant A — category navigation plus evidence
detail — is approved as the design direction.** Its three columns must share a
single top and bottom edge and stretch through the page's available content
height. The floating A/B/C switcher is review-only chrome and is not part of the
production page.

## Overview prototype verdict

User selection on 2026-08-12: **Variant A — page summary cards plus cross-page
highlights — is approved as the overview design direction.** It summarizes
需求系列, 资格审计, 错误检索, AREA 筛选, and 接入告警; Host state remains compact
global status rather than the page's primary content. Variants B (attention
items first) and C (page-status table) remain rejected prototype comparisons.

This approval selects the information hierarchy only. Production UI must be
rewritten and receive a fresh golden-machine preview approval.

## Notification feedback revision

User confirmation on 2026-08-26 supersedes the earlier inline-InfoBar placement
rule; existing page hierarchy selections remain unchanged.

User selection on 2026-08-26: **Variant A — independent Fluent toast cards — is
approved as the notification presentation direction.** Variants B (grouped
activity surface) and C (compact command-first tiles) remain rejected prototype
comparisons. This approval selects the structure and interaction hierarchy only;
production UI must be rewritten and receive a fresh comparable golden-machine
preview approval.

- The window owns a non-layout overlay toast host in the content region's upper
  right below the title bar. It is about 380 epx wide, shows at most three items
  newest-first, coalesces same-source events, and becomes a top single column at
  narrow width.
- Toasts are limited to user-operation outcomes, refresh-cleared selection, a
  new fault's first occurrence, and recovery. Page toasts end on navigation;
  global Host fault notices may persist across pages. Automatic refresh does not
  repeatedly open/close a message.
- Continuing faults contract to the page title as highest severity plus current
  count and details. Closing the first toast does not clear the condition.
- Success/info lasts 3 seconds, warning 5 seconds, and operation failure or a
  new continuing fault 8 seconds. Hover/focus pauses dismissal. Error precedes
  warning, then success/info; obsolete low-severity queued items are discarded.
- Entry/exit uses roughly 180 ms opacity plus 8–12 epx vertical movement and
  smooth stack changes. Reduced-motion mode removes movement.
- Empty/unselected/detail/field-validation states replace content in stable
  regions. AREA scope confirmation and concurrent-write conflict use overlay
  `ContentDialog`; unresolved conflict continues to pause auto-save.
- Toasts do not steal focus, are keyboard operable, announce a new event once,
  use text/icon in addition to color, and honor high contrast/reduced motion.
  Minimized/background operation raises no Windows system notification and does
  not replay stale success/recovery notices on return.
- A throwaway runnable prototype must demonstrate silent auto-refresh, operation
  success, fault contraction, three-item coalescing/stacking, AREA conflict,
  narrow layout, and reduced motion before production implementation. Production
  XAML still requires a fresh comparable golden-machine preview and user approval.

## Bilingual semantics prototype verdict

User selection on 2026-08-27: **Variant A — semantic workbench — is approved as
the bilingual and value-semantics presentation direction.** Variants B (field
semantics ledger) and C (evidence narrative) remain rejected prototype
comparisons.

The selection preserves the existing eligibility-audit master-detail hierarchy.
Localized meaning is the primary reading layer; canonical field names, protocol
codes, identifiers, and raw values remain visible as secondary technical facts.
Every value receives a visible semantic anchor, absolute timestamps retain their
offset, and source-not-provided, unknown, not-applicable, not-loaded, successful
empty result, and read-failure states remain visually distinct. Switching between
Simplified Chinese and English must retain filters, selection, scroll position,
and investigation context.

This approval selects presentation structure only. Production localization must
still be rewritten against the centralized strongly typed resource catalog from
ADR-0031 and receive a fresh comparable golden-machine preview approval.
