# Windows desktop implementation — V1

Native WinForms controls + embedded Microsoft Edge WebView2. Douyin only.
Target: Windows x64, .NET Framework 4.8, installed WebView2 Evergreen runtime.
Sources: SparkDesk.cs, ModernControls.cs, EmojiPicker.cs, BuiltinEmojiCache.cs, engine.js.
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
Package gate: 81 engine tests and 44 native checks. No real account messaging validation.
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

## 2026-11-11 completion, scrolling and cache update
- Native sending modes are text/emoji and favorite sticker; legacy text/builtin configurations migrate to the merged choice.
- Engine completion summaries distinguish sent-click counts, daily skips, other skips and missing selected identities. Stopping never emits successful completion.
- RecipientList is a double-buffered, pixel-scrolled custom control that paints only visible rows. A time-aware easing timer coalesces wheel input; the native scrollbar and keyboard remain usable. Respect the Windows client-area animation setting.
- Batch refreshes suppress repeated repaints and reset scroll position. Log height doubles from 66 to 132 logical pixels; append text instead of rebuilding it on every line.
- BuiltinEmojiCache persists public bracket tokens and HTTPS thumbnail URLs at %LOCALAPPDATA%/SparkDesk/builtin-emoji-cache.json. Startup loads it independently of browser readiness.
- Merge by token, append new tokens, refresh known thumbnails, retain old tokens missing from later scans. Reject custom/account metadata. Atomic replace with backup; corrupt input is never overwritten.
- The emoji picker shares the main browser environment/profile, including when opened before the main page initializes. Tests use only isolated temporary profiles and mock catalogs.
- Actual runtime cache files are excluded from source and binary allowlists. Cache tests cover persistence, reopening, append-only merges, duplicate/invalid data, corruption and empty rereads.
