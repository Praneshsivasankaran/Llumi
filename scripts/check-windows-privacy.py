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
                    r"\b(?:HttpClient|WebClient|TelemetryClient)\b"]:
        if re.search(pattern, source):
            errors.append(f"{path.relative_to(root)}: forbidden collection or network API")
    # Local opt-in alert state is the only audited use of Windows user protection.
    # Provider adapters still cannot decrypt credentials or use direct HTTP clients.
    if re.search(r"\b(?:DPAPI|CryptProtectData|CryptUnprotectData|ProtectedData)\b", source) and path.relative_to(root).as_posix() != "windows/src/AgentMeter/AlertLedgerStore.cs":
        errors.append(f"{path.relative_to(root)}: forbidden credential decryption API")
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
alert_store = (root / "windows/src/AgentMeter/AlertLedgerStore.cs").read_text(encoding="utf-8")
for fragment in ['HMACSHA256.HashData', 'CryptProtectData(ref source, null, 0, 0, 0, 1',
                 'CryptUnprotectData(ref source, 0, 0, 0, 0, 1', 'File.Replace', 'MaximumBytes']:
    if fragment not in alert_store:
        errors.append("Audited private alert-state protection contract changed")
if re.search(r"\b(?:UsageSnapshot|ProviderState|AccountBinding|EnvironmentVariable|LocalMachine)\b", alert_store):
    errors.append("Alert persistence must not accept provider snapshots or raw account bindings")
if errors:
    print("\n".join(errors)); sys.exit(1)
print("PASS: Windows privacy APIs, provider isolation, memory-only raw bindings and protected alert deduplication")
