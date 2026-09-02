---
paths:
  - "MesIngest.Watch/**"
  - "MesIngest.Watch.Prototype/**"
  - "MesIngest.Watch.FluentPrototype/**"
  - "MesIngest.Watch.UiTests/**"
  - "**/*.xaml"
---

# Watch UI work

Loaded when Claude reads `MesIngest.Watch` sources, its prototypes, its UI
tests, or any XAML. The tier rules in `CLAUDE.md` still apply; these are the
parts that only matter once UI is in play.

## Prototype-authoritative implementation

When a ticket or user points to an existing prototype, selected variant, or
prototype-generated result, the selected prototype is the design authority for
layout and interaction unless the user explicitly asks to depart from it.

Before editing production UI:

1. Locate the actual final prototype source, not just its spec or screenshots.
   Search the working tree, all Git refs/history (`git log --all`,
   `git ls-tree`), registered worktrees, and saved evidence. Do not conclude
   that the source is absent merely because it is not on the current branch.
2. Run the selected prototype when possible and read its relevant XAML and
   code-behind. Identify the exact selected variant/commit before implementing.
3. Write a concrete prototype-to-production mapping for window hierarchy,
   grids, dimensions, spacing, controls, states, and interactions. Implement
   that structure with production data and terminology instead of designing a
   merely spec-compliant alternative.

`PROTOTYPE`, `throwaway`, “do not promote this XAML directly”, and “must not
be a production dependency” mean that prototype projects, fake data, and
prototype-only vocabulary must not ship. They do **not** mean that agents may
ignore or redesign an accepted prototype's XAML structure.

For visual approval, capture the production UI at the same meaningful state and
comparable viewport as the accepted prototype, then inspect them side by side.
A green test run or a successfully generated screenshot is not evidence of
visual parity. If the referenced prototype source or selected state still
cannot be found after the searches above, stop and ask the user before inventing
a replacement design.

## Golden WPF renderer

Any ticket that changes `MesIngest.Watch` UI, XAML, Wpf.Ui controls, layout,
UI Automation, DPI behavior, or visual baselines must read
`docs/agents/fluent-ui.md` and `docs/agents/golden-renderer.md` before
implementation and validation. The Fluent rules are mandatory acceptance
criteria, not optional visual guidance.

- Formal WPF visual validation runs on `win11-01` (the factory server's Hyper-V,
  `ssh vm01`), driven by the `golden-renderer` GitHub Actions runner in session 1.
  Dispatch `.github/workflows/golden-renderer.yml`. `ssh vm01` itself lands in
  session 0, where WPF cannot render.
- Keep the calibrated VM at 1920x1080 and 100% / 96 DPI. Do not use RDP or
  Hyper-V Enhanced Session. Use a disposable offline clone for 125%/150% DPI.
- Never overwrite visual baselines before sending the candidates to the
  repository owner. Preserve red evidence and leave no residual test processes.

## Tier 2 and 3 mechanics

- Pick the narrowest suite that covers the change. `-Suite all` is not a
  default, and `-Class`/`-Method` narrow one suite further; see
  `docs/agents/golden-renderer.md`.
- `-Runs 3` is step 5 of the approval order in `docs/agents/golden-renderer.md`.
  It belongs to baseline promotion, not to implementation.
