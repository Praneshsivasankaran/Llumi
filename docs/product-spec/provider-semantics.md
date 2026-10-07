# Provider semantics

Windows 2.1.0 locally adopts the macOS 1.1.3 product contract in [Windows catch-up requirements](windows-210.md), which supersedes conflicting earlier Windows allowance, setup and provider-selection rules below. Intentional native differences remain.

A normalized reading carries provider, window identity, duration where known, remaining percentage, reset instant, observed instant, freshness and provider status. Identity continuity is verified in memory; account or plan changes invalidate incompatible observations. No account identifiers belong in presentation or logs.

Codex allowance comes from structured provider-owned rate-limit retrieval. Main/core and additional buckets remain separate. A missing main window cannot be synthesized or replaced by Spark. Never assume a five-hour window exists.

Claude Code is the allowance source. The user installs and signs in through it. Validate first-party subscription authentication before and after each read and match the initialized usage session to that identity. Usage checks send no inference prompt. Nonzero model usage/cost/API duration or an incompatible account invalidates the reading. Claude Desktop can contribute activity, but its caches and conversations are never allowance sources.

Unknown fields remain unknown. Malformed envelopes, duplicate identity fields and ambiguous account scopes fail closed. Retaining stale usage requires reverified compatible identity. No automatic provider installation, API-key field, credential extraction or provider authentication inside Llumi.

Claude compact presentation uses only the verified `five_hour` window, never weekly or a model-specific fallback. Expanded details show five-hour and weekly (`seven_day`) independently with their verified resets. Missing/invalid five-hour remains unknown even when weekly is available. Codex selection is unchanged.
