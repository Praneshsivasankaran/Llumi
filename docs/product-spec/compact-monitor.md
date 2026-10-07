# Compact monitor

Windows 2.1.3 locally adopts the macOS 1.1.3 product contract in [Windows catch-up requirements](windows-213.md), which supersedes conflicting earlier Windows allowance, setup and provider-selection rules below. Intentional native differences remain.

The compact monitor is contextual and optional. Its content is the same across platforms: provider logo plus primary percentage; one or both providers according to activity. Full names remain available to accessibility. Unknown is a dash; stale values have an explicit indicator and a stale detail state.

Hover expands a small surface containing provider identity, primary percentage, slim progress, primary reset and status when needed. Claude shows both five-hour and weekly percentages/resets in expanded details; compact always uses five-hour with no weekly fallback. Other usage windows stay in Usage. Click opens/focuses the single existing main window. No duplicate main windows or monitors.

Placement adapts to the platform's available screen edge or corner. Respect work areas, scaling and display changes. A lightweight translucent native surface must retain opaque, readable text and a safe opaque fallback. Brief expand/collapse transitions honor reduced-motion preferences; no permanent render loop.
