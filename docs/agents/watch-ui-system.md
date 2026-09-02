# MesIngest.Watch Fluent UI system

This file is the UI contract for `MesIngest.Watch`. Read it together with
`docs/agents/fluent-ui.md` and `docs/agents/golden-renderer.md` before changing
production XAML, WPF UI controls, UI Automation, DPI behavior, or baselines.

## Product character

MesIngest.Watch is a read-only Windows operations client, not a web dashboard.
It should feel calm, precise, native, and dense enough for incident work. The
operator must be able to answer “is ingest healthy and what needs attention?”
within ten seconds, then move from a summary to the exact task or alert without
losing context.

In Simplified Chinese mode, use Chinese for every fixed user-facing string,
including product identity, navigation, actions, explanations, operational
conclusions, field labels, table headers, filter names, tooltips, copied headers,
and accessibility text. Keep only runtime values in their production form,
including identifiers, stable codes, source values, paths, commands, JSON, and
logs; pair known codes with a Chinese meaning instead of using the code as a
label. English mode remains fully English. Do not create slash-separated
bilingual headings.

## Component boundary

- Use WPF UI 4.x controls whenever it supplies an equivalent: `FluentWindow`,
  `TitleBar`, `NavigationView`, `Button`, `ToggleSwitch`, `InfoBar`, `Card`,
  `SymbolIcon`, `InfoBadge`, dialogs, menus, and theme dictionaries.
- Native WPF layout and data primitives remain valid: `Grid`, `DockPanel`,
  `ScrollViewer`, `DataGrid`, `ItemsControl`, `TextBlock`, bindings, and
  virtualization. Do not replace them with hand-built visual imitations.
- Do not copy or restyle WPF UI control templates merely to change color,
  padding, or corner radius. Prefer WPF UI properties and theme resources.
- Use Fluent System Icons through `SymbolIcon`; do not mix emoji, arbitrary
  Unicode glyphs, bitmap icons, and Fluent icons in the same command system.
- One `FluentWindow` owns one WPF UI `TitleBar`. One WPF UI `NavigationView`
  owns primary navigation. A page owns its commands and stable scoped status;
  the window owns one overlay toast host above page content.

## Tokens

All production dimensions and colors must be referenced through named resources.
Literal values are allowed only inside the token dictionary or for one-off
geometry whose semantic purpose is documented next to the XAML.

| Token family | Required values | Use |
| --- | --- | --- |
| Space | `4`, `8`, `12`, `16`, `24`, `32` epx | `4` micro, `8` controls, `12` related groups, `16` surface padding, `24` page margin, `32` major separation |
| Radius | `4`, `8` epx | `4` controls and inline rows, `8` top-level cards and overlays |
| Control height | `32`, `36`, `40` epx | compact, default, prominent |
| Navigation | `48` compact, `220–240` open | preserve usable labels at 1440×900 |
| Type | Caption `12`, Body `14`, Body Strong `14`, Subtitle `18`, Title `28` | use the WPF UI type ramp where available; never invent per-card sizes |
| Icon | `16`, `20`, `24` epx | inline, command, navigation/status |

Neutral surfaces and text come from WPF UI theme resources. The Windows accent
resource is the only selection and primary-action color. Status resources are
semantic and reserved for meaning:

- Success: connected, compatible, healthy, current.
- Warning: degraded, stale, partial, business warning.
- Critical: connection/contract failure or active error.
- Informational: neutral progress, read-only explanation, recovery notice.

Do not tint whole pages. A status color may appear in an icon, a narrow accent,
a stable in-page state, a toast, or a compact badge, and must always be paired
with visible text and an accessible name.

## Shell and page anatomy

```text
FluentWindow
├─ integrated TitleBar: icon + localized product name + native caption buttons
├─ NavigationView
│  ├─ 概览
│  ├─ 需求系列
│  ├─ 接入告警
│  ├─ 设置
│  └─ pane footer: compact localized server connection entry
├─ active page
│  ├─ one page title + optional one-line purpose
│  ├─ page context: server, last success, automatic-refresh interval
│  ├─ compact scoped fault status beside the page title
│  ├─ page-scoped commands: query/filter actions and overflow
│  └─ page content
└─ overlay toast host: top-right of the content region below the title bar
```

- Never repeat application identity, endpoint, and connection state in a second
  full-width app banner below the title bar.
- Keep connection status in the `NavigationView` pane footer as a compact,
  persistent Host entry. The expanded pane exposes endpoint and contract state;
  compact mode retains a named icon and tooltip. Announce a new actionable
  connection failure once through the overlay toast host, then keep it available
  in the affected page's compact fault status with a Settings/details action.
- Put page freshness beside the page title as secondary context. Do not create
  a global bottom status strip for Host, freshness, read-only mode, or keyboard
  hints; those facts belong to the navigation footer, page context, settings,
  tooltips, or help respectively.
- Page commands stay in a stable top-right command zone. On contraction, keep
  the page's primary task command visible and move secondary commands into overflow.
- Every data view refreshes automatically. Configure only its interval in
  Settings; there is no enable/disable switch. The active page shows the
  effective interval as passive context (`自动刷新 · 10 秒`) and exposes no manual
  Refresh command. User actions such as query, sort, paging, or Host change may
  still start an immediate read for their own result.

## Feedback contract

- Normal initial load and automatic-refresh start/success do not create a toast
  or insert/remove a message row. They update fixed progress/freshness context.
- Toasts are reserved for user-initiated operation outcomes, a selection cleared
  by a refresh, a new fault's first occurrence, and recovery. The same active
  fault is coalesced and is neither re-shown nor re-announced on every refresh.
- A continuing fault contracts after its first toast to the affected page title:
  highest-severity icon, visible severity text, current count, and an accessible
  details affordance. Closing a toast never clears that status. Global Host state
  remains in the navigation footer and may survive page navigation.
- Page-scoped toasts end when their page is left. Global fault toasts may remain.
  Background/minimized operation does not use Windows system notifications and
  does not replay stale success or recovery messages when the window returns; an
  extant fault may be summarized once.
- The host occupies the content region's top-right below the title bar, is about
  380 epx wide, and shows at most three items with newest first. At narrow widths
  it becomes one top-aligned column with safe side margins. It never changes page
  measure or covers navigation/title-bar controls.
- Coalesce same-source events and prioritize error, warning, then success/info.
  Obsolete success/info items may be discarded instead of replayed from a queue;
  warnings and errors remain represented by their stable fault status.
- Success/info stays for 3 seconds, warning for 5 seconds, and operation failure
  or a continuing fault's first notice for 8 seconds. Hover or keyboard focus
  pauses dismissal. Every toast can be closed; long/sensitive exception text,
  credentials, full URLs, and stacks stay in details rather than the toast.
- Toast entry/exit uses roughly 180 ms opacity plus 8–12 epx vertical movement,
  and stack changes move smoothly. Reduced-motion mode removes movement and may
  switch directly or use opacity only.
- Empty results, no selection, detail conclusions, and field validation replace
  content in stable regions; they are not transient toasts. Scope confirmation
  and AREA concurrent-write conflict use overlay `ContentDialog` choices and do
  not move page content. Conflict dismissal leaves auto-save paused.
- Toasts do not steal focus. They expose keyboard action/close affordances and a
  non-color text/icon meaning. Screen readers announce a new notice once, not on
  every render of the same fault; high contrast and Windows reduced-motion
  settings remain authoritative.

## Page contracts

### 概览

Prioritize, in order: Host access and contract, latest poll health, active
`IngestAlert`, then VISIBLE `TransportDemand`. Summary values link to their
default queries and use the total count supplied by the Host contract.

### 任务浏览

Use a stable master-detail workspace. Filters may be a collapsible left pane or
a compact command surface, but the selected row's details stay outside the
horizontal table scroll. At desktop width, place the task list above the current
task detail so both use the full content width. Separate the seven MES input fields from local
projection fields. VISIBLE/GONE, page/total, stale state, and
last success remain visible without hover. Pagination supports Previous/Next
and a direct numeric page jump; the Host provides stable page-index queries and
total item/page counts.

### 接入告警

Use a master-detail queue with lifecycle and severity visible in text. Keep the
six production codes verbatim. A relationship inspector must distinguish exact
`DemandId`, business key, task-type scope, and global scope. Alerts are read-only;
do not imply acknowledge, assign, or resolve actions. `IsActive=false` and
`ResolvedAt` mean the ingest condition disappeared automatically; label this
history as `已恢复`, never as an operator action.

### Filters, tables, and commands

- Do not spend page width repeating an “applied query” sentence. Enable
  `清空条件` only when the draft differs from the default query; otherwise keep it
  disabled. Applying succeeds by updating the result and freshness context.
- Header and cell text share the same horizontal inset and column width. A
  header must visually start on the same axis as every value beneath it.
- Every documented sortable field uses a clickable `DataGrid` column header,
  exposes the current ascending/descending glyph, and requests server-side sort
  across the full filtered result—not merely the loaded page.
- Accent fill represents a selected state or current page. One-shot commands
  use neutral/secondary appearance and do not retain an accent state.

### 设置

Use grouped WPF UI cards or card expanders for Host, timeout, per-view refresh,
and local display/layout preferences. “应用” is scoped to connection settings;
field validation remains in its stable field region, while a submitted operation
may also raise one concise toast and return focus to the invalid field. No
telemetry, diagnostics, or export settings enter the V2 surface.

## Density and responsive behavior

- Design and approve first at 1440×900, then verify expansion at 2560×1440.
- Support a 720 epx minimum width with `NavigationView` compact/minimal behavior.
- Prefer a 40–44 epx data-row height and a 36–40 epx header. Do not reduce body
  text below 12 epx to fit more columns.
- At 1440×900, preserve navigation, page title/status, filters, pagination,
  table, and selected detail. Horizontal scrolling is acceptable for the data table.
- At 125% and 150% DPI, reflow or scroll; never clip commands, status text,
  caption buttons, focus visuals, or the selected-detail boundary.

## Interaction and accessibility

- Every icon-only command has a tooltip, stable `AutomationProperties.Name`,
  and a visible keyboard focus state.
- Minimum interactive target is 32×32 epx; primary commands default to 36 epx.
- Status is never color-only. Text contrast is at least 4.5:1. High contrast may
  replace materials and custom colors without losing grouping or selection.
- Loading preserves the last successful window and updates fixed non-blocking
  state. Failure follows the feedback contract above; empty state is a deliberate
  stable page state, not a blank `DataGrid` or a transient toast.
- Use absolute local time as the primary value, with offset available for copy.
  Relative time is secondary only.

## Materials and motion

- Mica is the window/title/navigation base when supported; use the WPF UI theme
  fallback otherwise. Content cards remain neutral and opaque enough to read.
- Acrylic is limited to transient flyouts and menus. Never use it as the main
  content canvas or for adjacent permanent panes.
- Motion explains navigation, expansion, progress, or state change. Keep it
  short and disable nonessential animation when the OS requests reduced motion.
- Do not use gradients, glow, glass borders, decorative charts, or large status
  color fields unless they convey information that text and alignment cannot.

## Pull-request gate

- [ ] WPF UI equivalents are used; custom control templates have a documented necessity.
- [ ] All spacing, radii, colors, and control heights resolve through tokens/theme resources.
- [ ] Exactly one app identity layer, one page title, and one primary navigation model exist.
- [ ] Light, inactive, high-contrast, 1440×900, 2560×1440, 125%, and 150% behavior is reviewed.
- [ ] Keyboard, focus, tooltip, UIA name, selection, empty, loading, stale, partial, and error states are reviewed.
- [ ] Automatic refresh causes no page reflow or repeated toast/UIA announcement;
      fault coalescing, dismissal, recovery, navigation, and overflow are reviewed.
- [ ] Table headers align with cell values, and sortable headers expose direction and server-side behavior.
- [ ] Clear-filter buttons are disabled at defaults; one-shot commands are neutral rather than selection blue.
- [ ] Pagination exposes accurate totals and arbitrary numeric jump backed by stable Host page-index queries.
- [ ] Golden renderer rules were followed, and the user approved real VM previews before baseline promotion.
