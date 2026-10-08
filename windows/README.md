# Llumi for Windows

Native Windows Forms implementation of Llumi, using .NET 10.

**Status:** Llumi 2.0.2.0 is published in Microsoft Store. [Download from Microsoft Store](https://apps.microsoft.com/detail/9NV153Q5K5MQ). No direct Windows download is offered; that channel requires a separate distribution review.

- [Build, test and run](../docs/windows/BUILD.md)
- [Windows 2.0.3 patch preparation](../docs/windows/PATCH-2.0.3.md)
- [Shared product specification](../docs/product-spec/README.md)
- [Privacy](../PRIVACY.md)
- [Distribution requirements](../docs/DISTRIBUTION.md)
- [Third-party notices](../THIRD-PARTY-NOTICES.md)

`src/AgentMeter` contains the native UI and lifecycle integration. `src/AgentMeter.Core` contains provider retrieval, parsing, refresh scheduling and activity detection. `tests/` uses synthetic fixtures. The current application invokes separately installed provider tools and contains no Claude Agent SDK or Node bridge.

The frozen Store release is 2.0.2 / 2.0.2.0. See the [release baseline](../docs/releases/2026-09-launch.md) for its exact source and package checksum; current main is not the binary provenance.
