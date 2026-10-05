# Usage
For the macOS 1.1.3 allowance correction, [the approved allowance requirements](allowance-113.md) supersede the earlier primary-window and state rules below. Windows development remains parked.
macOS 1.1.3 shows only enabled providers in equal-width, equal-height cards aligned at headings, primary values and bars. Reserve whitespace for differing window counts rather than inventing data. Both providers off shows a Settings action. Remove the introductory allowance subtitle and signed-in-provider footer; retain observation age and refresh. Windows presentation is unchanged.

macOS navigation keeps Usage, Settings and About in the sidebar. Remove the default Show/Hide Sidebar toolbar button; retain the existing sidebar layout and navigation.

Llumi is one product with native platform implementations. Its primary destinations are Usage and Settings. Provider names are Codex and Claude Code. The Raspberry semicircular gauge identifies Llumi; system status surfaces use its monochrome variant.

Usage presents each provider's status, primary remaining percentage, slim progress, meaningful windows, reset information and observation age. Remaining means 100 minus verified used percentage. Unknown is a dash, never zero or full. Codex primary is the longest reported main/core window; extra/Spark buckets never substitute.

Claude consumer presentation recognizes only the exact normalized windows `five_hour` (5 hours) and `seven_day` (7 days / weekly), in that order. Five-hour is the primary value in Usage and the compact monitor. Weekly is secondary detail only; it never substitutes for a missing five-hour value. If only weekly is available, Usage and expanded monitor may show that detail while the compact primary stays unknown. If neither recognized window is available, show no allowance value and explain that allowance is unavailable.

Unknown, internal, experimental, model-scoped and provider-generated windows may remain in the parsed observation, but must not appear as raw labels, tooltips or accessibility text in consumer Usage, compact or expanded/hover surfaces. Do not infer their meaning from a name, prefix or duration, rename them, or promote them into a standard allowance. Use an explicit recognized-window policy rather than a denylist of observed identifiers. Diagnostics remain category-based and do not export bucket identifiers. This semantic rule applies to macOS and Windows; native surface layouts differ intentionally. Codex main/additional-window presentation is unchanged.

Show Loading, Live, Stale, Not installed, Not signed in and Unavailable. A refresh never erases the last observation age or silently makes stale data live. Past resets await a new verified reading and never imply a refill. Full window detail belongs here, not in the compact monitor.

Refresh automatically every 30 seconds. Manual Refresh enters the same per-provider scheduler. One in-flight query per provider, coalesced repeated requests, bounded time/output, cancellation and complete owned-helper cleanup are required. A slow or failed provider cannot block the other.
