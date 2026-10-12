# Llumi product specification

Llumi is one product with native macOS and Windows implementations. This specification is the starting point for product changes and reviews, not a claim that every implementation has passed every acceptance scenario.

Before changing behavior:

1. Describe the user need and update the relevant shared specification.
2. Consider both platforms; implement shared behavior on both wherever applicable.
3. Record any intentional platform adaptation or remaining gap in the change description.
4. Validate each affected platform with synthetic tests; report physical, provider and distribution acceptance separately.

| Specification | Scope |
| --- | --- |
| [Usage](usage.md) | Allowance, windows, reset times, freshness and refresh |
| [Provider semantics](provider-semantics.md) | Authentication, identity, supported sources and unknown values |
| [Contextual activity](activity.md) | CLI and desktop activity without content inspection |
| [Compact monitor](compact-monitor.md) | Visibility, hover, click, motion and accessibility |
| [Settings and lifecycle](settings.md) | Preferences, startup, background operation and quit |
| [Windows usage alerts and background startup](windows-alerts-background.md) | Opt-in thresholds, snooze, protected repeat prevention and quiet launch; future Mac parity contract |
| [Privacy](privacy.md) | Data boundaries, isolation, credentials and diagnostics |
| [Background and alerts](background-alerts.md) | macOS update and shared lifecycle/alert contracts |
| [macOS 2.2.0 updater-key migration](updater-key-migration-220.md) | One-time manual installation, separate new feed and preserved update verification |

## Native platform adaptations

| Shared behavior | macOS | Windows |
| --- | --- | --- |
| Compact monitor | Notch or top edge | Movable desktop surface |
| Background controls | Menu bar | Notification-area tray |
| Native UI | SwiftUI / AppKit | Windows Forms |
| Startup | Launch at Login | StartupTask in Store package; native registration for unpackaged builds |
| Local preferences | System preferences | Per-user local storage; package LocalState for Store builds |

Platform differences should serve native behavior and accessibility while preserving the meaning of usage, status and privacy. Release availability is tracked in the [root README](../../README.md), separately from product behavior.
