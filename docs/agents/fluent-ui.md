# MesIngest.Watch Fluent UI rules

These rules apply to every change to `MesIngest.Watch` window chrome, navigation,
page layout, controls, status presentation, themes, and visual baselines. They
adapt Microsoft's Windows 11 guidance to the WPF UI controls used by this repo.
Read this file together with `docs/agents/golden-renderer.md` before changing UI.

## Shell hierarchy

- Use one application-identity layer only: the window title bar owns the app
  icon/name, drag region, and caption buttons. Do not repeat the app logo, name,
  endpoint, or subtitle in a second full-width header immediately below it.
- A page may have one page header. Put its title at the start and its
  page-specific commands at the end or in an adjacent compact command row. Do
  not insert an app-level banner between the title bar and the page/navigation
  layout.
- Keep these layers visually distinct and in this order: integrated title bar,
  primary navigation + page header, page content, optional overlay feedback.
  This is the repo's prescribed interpretation of Microsoft's modern desktop
  shell (integrated title bar, sidebar navigation, and stable scoped status)
  and the separate `NavigationView.Header` page-title role.
- For the current MesIngest shell, automatic refresh is always enabled and only
  its per-view interval is configured in Settings. Do not expose manual Refresh
  or auto-refresh enable/disable commands in page headers. Present global Host
  state compactly in the `NavigationView` footer and page freshness beneath the
  page title. Normal automatic-refresh start/success is silent except for that
  fixed freshness context. A new connection failure may raise one overlay toast,
  then remains discoverable as stable scoped fault status without changing page
  layout.

Sources: [Microsoft: keep app identity in the title bar only](https://learn.microsoft.com/en-us/windows/apps/develop/ui/windows-app-sdk-app-structure#set-up-a-custom-title-bar),
[Microsoft: NavigationView header and pane footer](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview),
[WPF UI: NavigationView anatomy](https://wpfui.lepo.co/documentation/navigation-view.html).

## Title bar and window behavior

- Use `Wpf.Ui.Controls.FluentWindow` and WPF UI's `TitleBar`; keep
  `ExtendsContentIntoTitleBar` enabled. Do not build a parallel fake caption bar.
- Prefer WPF UI's measured/default title-bar layout. Windows documents 32 epx
  as the standard bar and 48 epx for integrated title-bar layouts; do not add a
  second row to compensate for an arbitrary title-bar height.
- Keep a reliable non-interactive drag region beside the caption controls. Empty
  title-bar space and non-interactive title text must drag the window; double
  click must maximize/restore; right click must expose the system menu.
- Keep minimize, maximize/restore, and close visible and fully functional. Their
  hover, pressed, active/inactive, keyboard, and UI Automation states are part of
  the feature, not optional polish.
- The title bar must blend into the window material and respond to light, dark,
  inactive, and high-contrast states. Do not hard-code a white title strip.

Sources: [Microsoft: title bar design](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design),
[Microsoft: TitleBar control](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/title-bar),
[WPF UI: TitleBar](https://wpfui.lepo.co/api/Wpf.Ui.Controls.TitleBar.html),
[WPF UI: FluentWindow](https://wpfui.lepo.co/api/Wpf.Ui.Controls.FluentWindow.html).

## Commands and status

- Put common, discoverable commands near the top of the relevant page on desktop.
  Align content/title to the left and primary commands to the right. Keep the
  most important commands visible; move secondary commands to overflow as width
  contracts. Keep commands that recur across pages in a consistent location.
- Use concise action labels and Fluent system icons. Do not mix unrelated
  actions, identity text, status indicators, and navigation into one banner.
- Persistent status must be compact, stable, and adjacent to its scope. A dot or
  color may reinforce state, but visible text and an accessible name must carry
  the meaning (`已连接`, `正在连接`, `已断开`). Never communicate state by color
  alone.
- Separate feedback by behavior rather than severity alone. Empty/unselected/
  validation/detail states replace content in a stable region; blocking choices
  use `ContentDialog`; transient user-operation outcomes and first-occurrence or
  recovery notices use the window's overlay toast host; continuing faults use a
  compact status beside the affected page title. None of these surfaces may
  repeatedly insert/remove an `Auto` layout row during automatic refresh.
- Use progress controls only while work is actually underway; a small background
  activity can use fixed text or a compact indicator rather than perpetual
  animation or a toast.

Sources: [Microsoft: CommandBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar),
[Microsoft: status messaging with InfoBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/windows-app-sdk-app-structure#use-infobar-for-status-messages),
[Microsoft: progress controls](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls),
[Microsoft: accessibility overview](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview).

## Layout, spacing, and typography

- Use effective pixels and an 8/12/16/24 spacing rhythm. Default to 8 epx
  between related controls, 12 epx between content groups, 16 epx for inner
  surface gutters, and 24 epx for a desktop page's outer content margin. Use 12
  epx outer margins only in compact/minimal navigation layouts.
- Alignment must explain grouping: shared left edges for titles/body content,
  consistent card gutters, and equal gaps for peer controls. Do not use borders
  or extra bars where whitespace and alignment express the same hierarchy.
- Use the Windows/WPF UI theme type ramp instead of ad-hoc font sizes. Use one
  UI family (`Segoe UI Variable` where available, with the platform fallback),
  page-title style once per page, Body Strong for section/card headings, Body
  for primary values, and Caption for constrained metadata.
- Avoid redundant labels and repeated headings. Each level must add information:
  window identity, navigation location, page title, section title, body.

Sources: [Microsoft: content layout and spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics),
[Microsoft: typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography),
[Microsoft: NavigationView content margins](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview).

## Materials, color, and geometry

- Use theme resources supplied by WPF UI for window, navigation, card, text,
  stroke, accent, and state colors. Hard-coded colors require a documented
  semantic reason plus light, dark, and high-contrast validation.
- Use Mica/backdrop treatment for the main window/title/navigation base layer
  when supported, with a theme fallback. Do not cover the window,
  `NavigationView`, or page root with an opaque decorative background; use
  theme card brushes for content surfaces. Reserve Acrylic for transient
  surfaces such as flyouts and menus; do not place adjacent Acrylic panes or use
  desktop Acrylic as a large content background.
- Preserve Windows 11 geometry: approximately 8 epx for top-level/overlay
  containers, 4 epx for in-page controls and list backplates, and 0 where
  touching straight edges must join. Do not stack rounded cards inside rounded
  cards without a hierarchy reason.
- Accent color identifies selection and primary action; it is not decoration.
  Neutral surfaces should dominate. Status colors must retain their conventional
  meaning and remain secondary to text/icon semantics.
- In the current Watch design, reserve accent-filled buttons for persistent
  selection (`VISIBLE/GONE`, active/restored alert view, current page). Commands
  that execute once (`应用条件`, `跳转`, `打开详情`) use neutral/secondary or
  transparent appearance; pressing a command must not make it look selected.

Sources: [Microsoft: materials](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials),
[Microsoft: Acrylic usage](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic),
[Microsoft: Windows 11 geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry),
[Microsoft: accessible text contrast](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements).

## Review checklist

- [ ] There is exactly one application-identity/title layer.
- [ ] The page has at most one page title; commands are scoped to that page.
- [ ] App-wide connection state does not consume a full-width header row.
- [ ] Title-bar drag, right-click, double-click, resize, caption buttons, keyboard,
      and UI Automation behavior still work.
- [ ] Layout uses the 8/12/16/24 rhythm and remains coherent at supported window
      sizes and DPI values.
- [ ] Typography uses the theme type ramp and exposes a clear title/section/body/
      caption hierarchy.
- [ ] Theme resources replace decorative hard-coded colors; light, dark,
      inactive, and high-contrast states remain legible.
- [ ] Status meaning is available as text/icon/UIA, not color alone; text contrast
      is at least 4.5:1.
- [ ] Automatic refresh does not open/close a layout-participating message row;
      transient feedback overlays content and continuing faults have stable status.
- [ ] Corner radii and materials match the semantic layer; Acrylic is transient.
- [ ] The golden-renderer suites and explicit user preview/approval steps in
      `docs/agents/golden-renderer.md` are complete before baseline promotion.
