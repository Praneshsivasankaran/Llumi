# Windows setup illustrations

These locally bundled assets are synthetic illustrations, not recordings of an account, a real installation or a provider response. They execute no commands and make no network requests.

`windows/tools/generate-setup-guides.py` adapts the layout and timing of `llumi-macos-1.1.3:scripts/macos/generate-setup-demos.py` using Windows PowerShell commands and window artwork. Each GIF has 100 frames at 100 ms per frame, loops every 10 seconds, and has a matching first-frame PNG for reduced motion. `SetupAnimation` also stops its timer when hidden or disposed.

The generator uses installed Segoe UI and Consolas fonts as local rendering inputs; font files are not bundled. Regenerate on Windows with Python and Pillow. The generated guides are embedded resources and do not require Python or Pillow at runtime.

The illustrative sign-in screen contains no account identity, credentials or usage. Installation time is explicitly illustrative. The actual setup controls remain read-only command fields with Copy buttons and an official setup-help link.
