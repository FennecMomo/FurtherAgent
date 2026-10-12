# Desktop theme visual QA — 0.12.0

Status: passed for the implemented WPF theme scope.

Reference: user attachment `codex-clipboard-92881b11-cc26-4f64-ac8b-66580bd9e917.png`, supplied 2026-10-12. Target is the existing Windows client, not a separate web prototype.

## Comparison and adjustments

Compared the source image and actual WPF XAML renders in the same inspection pass. At 1214×984 logical pixels the 60 px rail, 288 px sidebar, 50 px top bar, white conversation surface and rounded composer follow the screenshot proportions. Source text, scroll position and project order differ in the fixture; no pixel-perfect equivalence claim is made.

First render showed undersized text, an overly tall composer, and legacy dropdown gradients. Increased UI text to 16 px and chat text to 17 px, reduced composer minimum height, and added rounded native editable/noneditable dropdown templates. Final inspection covered both themes, narrow layout and model dialog. Removed duplicate TextBox padding and kept compact dropdown padding responsive to its explicit size.

## Evidence

Generated locally by the opt-in `--themes` WPF check:

- `artifacts/theme-0.12.0/codex.png`: main Codex-style conversation.
- `artifacts/theme-0.12.0/original.png`: original palette and layout after switching with a draft.
- `artifacts/theme-0.12.0/codex-narrow.png`: 940×650; composer remains visible, sidebar and conversation scroll independently.
- `artifacts/theme-0.12.0/codex-narrow-running.png`: long model name, delivery selector, stop and interrupt controls fit by wrapping the model controls.
- `artifacts/theme-0.12.0/codex-settings.png`: theme choice and existing connection settings.
- `artifacts/theme-0.12.0/codex-model.png`: model and mode overlay, editable model text.

Icons use the installed Segoe Fluent Icons font. Each navigation control invokes an existing action. Unsupported Codex voice/notification features were not added. The FurtherAgent workspace and connection actions remain visible. Long workspace names ellipsize and retain tooltips.

Checks confirm draft, message source and attachment-toolbar identity survive theme changes, and custom model text survives retemplating. Original WPF/persistence checks remain applicable. Rendered fixtures do not validate physical window dragging, taskbar interaction or remote task execution; installed-process readiness is checked separately during desktop deployment.
