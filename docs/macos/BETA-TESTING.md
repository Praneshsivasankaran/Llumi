# macOS physical acceptance checklist

For future Llumi candidates, record the hardware and combinations actually tested: Apple Silicon and Intel, notched and non-notched Macs, different macOS versions, and built-in/external displays. Please try whichever Codex CLI/Desktop and Claude Code CLI/Desktop surfaces you already use. No purchase or extra account is needed.

## A short check

1. Follow the [installation notes](INSTALL.md). Llumi 1.1.1 is signed and notarized; future candidates require their own acceptance. Keep Gatekeeper enabled.
2. Open Llumi. Confirm allowance loads for your installed, authenticated Codex and Claude Code tools. Missing tools should show a clear status. Compare with your provider's own usage reading where available.
3. Open an interactive Codex CLI session, or bring Codex Desktop frontmost with a visible window. Check that the notch/top-edge monitor appears.
4. Hover for percentage/reset details, then click to open the Usage window.
5. Exit the CLI session or switch away from the desktop app. The monitor should hide when no supported session remains. A CLI left open still counts.
6. Repeat with Claude Code. Claude Desktop can trigger activity; standalone Claude Code signed into the same subscription supplies the allowance.
7. Restart Llumi with a session already open. Check detection, usage, menu-bar controls, and Settings.
8. Optionally connect an external display or change scaling. Check that the monitor remains on-screen.
9. Optionally sleep/wake the Mac. Check that monitoring and usage recover.
10. [Report a bug](https://github.com/Praneshsivasankaran/Llumi/issues/new/choose) with expected/actual behavior and short reproduction steps.

Include Mac model (not serial number), architecture, macOS version, notch yes/no, external display yes/no, Llumi version, provider version, and CLI/Desktop/both. Note which checks you tried; you do not need to cover every combination.

Never upload provider authentication files, credentials, API keys, tokens, cookies, private prompts/responses, source code, or full terminal history. Review screenshots and logs before attaching them. If an unrelated privacy prompt appears, do not grant access; report the action that triggered it. Security issues belong in [private vulnerability reporting](../../SECURITY.md).
