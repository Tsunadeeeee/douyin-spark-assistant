# Windows desktop implementation — V1

Native WinForms controls + embedded Microsoft Edge WebView2. Douyin only.
Target: Windows x64, .NET Framework 4.8, installed WebView2 Evergreen runtime.
Sources: SparkDesk.cs, ModernControls.cs, EmojiPicker.cs, engine.js.
Embedded resources: engine.js, spark.ico, demo.html only.

Retained features:
- Official login inside the existing browser profile; no copied credentials.
- All four supported streak categories and manually selected one-to-one chats.
- Default selection: recent 30-day contact AND verified mutual follow.
- Explicit check/uncheck overrides, list scroll restoration and optional daily deduplication.
- Official built-in emoji insertion and favorite-sticker click; thumbnail selection does not send.
- Same encrypted settings and attempt history at %LOCALAPPDATA%/SparkDesk.
- Single-platform layout without an unnecessary navigation rail.

Build to release first. Run build.py, test_engine.cjs, then
EXE --self-test --report <absolute JSON path>. All self-tests are isolated.
Package gate: 76 engine tests and 34 native checks. No real account messaging validation.
package.py uses explicit source and binary allowlists; output defaults to dist.

## Bracket-token built-in emoji composition
- Read exact bracket names from official icon alt/title, otherwise the official right-click preview title matched by image URL.
- No application state extraction and no private API. Never click a small emoji to discover its name.
- Built-in picker stays open for repeated selections. Each click inserts a token at native textbox selection/caret; 300-character limit applies.
- Send the textbox content once; no extra hidden selected-emoji append. Read back rendered official emoji img alt tokens in original text order.
- Preserve custom-sticker single-selection behavior.
- builtinTokenVersion=1 migrates old explicit bracket-name selections once; marker is preserved by native and JS config writes.
- Tests include omitted names, token-only messages, repeated codes, mixed multiline content, unknown bracket text and reordered-image rejection.

V1: dailyDedup defaults true; false disables only daily filtering, not identity, durable acknowledgements, or manual allowlist checks. Slogan removed. Internal assembly version 1.0.0.0.
