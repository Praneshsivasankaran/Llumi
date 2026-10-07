#!/usr/bin/env python3
"""Static Windows production privacy guard, run only from the developer checkout."""
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parents[1]
errors = []
for path in (root / "windows/src").rglob("*.cs"):
    source = path.read_text(encoding="utf-8-sig")
    for pattern in [r"\b(?:GetWindowText|GetWindowTextW|BitBlt|PrintWindow|SendInput|SetWindowsHookEx|GetAsyncKeyState)\b",
                    r"\b(?:HttpClient|WebClient|TelemetryClient)\b", r"\b(?:DPAPI|CryptUnprotectData)\b"]:
        if re.search(pattern, source):
            errors.append(f"{path.relative_to(root)}: forbidden collection or network API")
program = (root / "windows/src/AgentMeter/Program.cs").read_text(encoding="utf-8")
if not re.search(r"CreateProviders\(Action<string> log\).*?new CodexProvider.*?new ClaudeProvider", program, re.S):
    errors.append("Production provider factory is not the audited Codex/Claude adapters")
if "new ClaudeDesktopSource" in program or "new ClaudeCodeSource" in program:
    errors.append("Production uses a legacy cache/helper source")
if '"--probe"' in program or re.search(r"\bFile\.(?:WriteAllText|WriteAllBytes|AppendAllText|Create|OpenWrite)\b|\bJsonSerializer\.Serialize", program):
    errors.append("Production entry point must not export or persist usage snapshots")
usage = (root / "windows/src/AgentMeter.Core/Usage.cs").read_text(encoding="utf-8")
for field in ["Binding", "VerifiedBinding"]:
    if not re.search(r"\[JsonIgnore\] public AccountBinding\? " + field, usage):
        errors.append("Account continuity evidence can enter serialization")
transport = (root / "windows/src/AgentMeter.Core/ClaudeControlTransport.cs").read_text(encoding="utf-8")
for fragment in ['subtype = "get_usage", skip_behaviors = true', '"--no-session-persistence"', '"--safe-mode"', '"--strict-mcp-config"']:
    if fragment not in transport:
        errors.append("Audited usage-only Claude isolation contract changed")
if errors:
    print("\n".join(errors)); sys.exit(1)
print("PASS: Windows production privacy APIs, adapter selection, memory-only bindings and usage-only isolation")
