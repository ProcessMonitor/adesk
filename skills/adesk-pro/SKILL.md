---
name: adesk-pro
description: Drive Windows desktop apps for the user through the accessibility tree — snapshot windows into JSON trees with refs, then click, type, set values, press shortcuts and scroll on those refs. Use when the user asks to open, click, fill, read, close, resize or automate a Windows desktop app, needs UI state or a window screenshot, or wants window management. Triggers on "open app", "click the button", "fill the form", "read the window", "close the app", "take a window screenshot", "desktop automation", "adesk".
---

# adesk-pro

adesk-pro reads Windows apps through UI Automation and returns JSON with refs. You act on refs, then verify by reading again — the observe-act-verify loop lives in you; the tool has no model inside.

Install steps live in [INSTALL.md](INSTALL.md) shipped with this skill. Run the binary once before letting an agent use it.

## First run

1. `adesk-pro status` — platform, session and store info.
2. `adesk-pro skills get desktop` — this guide, straight from the binary you are running.
3. `adesk-pro <command> --help` — flags for one command. This guide does not repeat flag lists.

## The loop

```
adesk-pro snapshot --app Notepad -i              # JSON tree, interactive elements carry refs
adesk-pro click @s8f3k2p9:e12                    # act on a ref
adesk-pro snapshot --app Notepad -i              # read again to verify
```

- `-i` keeps interactive elements only. For a dense app take `--skeleton` (subtrees summarized with `children_count`), then `snapshot --root @s8f3k2p9:e3 -i` to drill one region.
- `find --app X --name "Save"` locates elements by name/role without walking the whole tree yourself.
- An action that opens a dialog lists it in the result; snapshot again to see it.
- After an async step, `wait` for the window or ref state instead of a fixed sleep.

## Refs

- Refs are qualified `@<snapshotId>:e<N>` and bind to their snapshot. After the UI changes, re-snapshot.
- Before any action the element is re-identified live: scope-path descent → RuntimeId pinning → identity match. Zero matches: `STALE_REF`. Several: `AMBIGUOUS_TARGET` — take a fresh snapshot and pick a unique element.
- `find` can address repeated controls (twin buttons): it issues refs with RuntimeId pinning.

## Delivery and retry

Results and errors carry `disposition`. Retry an action only when `disposition.retry` is `safe` (nothing was delivered). Otherwise the action may have landed — read live state (`get`, `snapshot`) and decide. Never repeat a click or type because an error looked odd.

## Headed input

`press`, `mouse-click`, `hover` and `drag` move the real cursor or synthesize keys. Use them deliberately: `mouse-click` targets screen coordinates (the fallback for controls without patterns), `press --app X` scopes a shortcut to one app without focusing it manually.

## Limits

- Windows only. Sequential MCP dispatch.
- A walk that hits its budget returns `complete: false` — drill with `--root` or raise `--timeout-ms` instead of trusting a partial tree.
- `STALE_REF` errors include a diagnostic comparing live vs. saved identity fields; read it before re-snapshotting blindly.
