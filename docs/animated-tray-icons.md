# Animated Tray Icons

Notes on whether (and how) the system-tray icon can animate, and how it would fit this app.

## Short answer

Yes — but you animate it yourself. The Windows notification area has **no built-in concept of an
animated icon**. A `NotifyIcon` displays exactly one static `HICON` at a time. To animate, you swap
`NotifyIcon.Icon` on a `System.Windows.Forms.Timer` tick, selecting/drawing the next frame each
time. The shell just shows whatever the current handle points to.

## Mechanism

- Add a UI-thread `System.Windows.Forms.Timer`. On each `Tick`, set `_trayIcon.Icon = nextFrame`.
- Keep the frame rate modest — **~8–15 fps (66–125 ms)** is plenty. The tray is low priority;
  faster just wastes CPU/GDI churn for no visible benefit.

## The GDI-handle trap (the real gotcha here)

`MakeCircleIcon` creates `HICON`s tracked in `_ownedHandles` and freed with `DestroyIcon` in
`Dispose`. If you generate a fresh icon every tick and don't free it, you leak a handle ~10× per
second — the process will exhaust GDI handles within minutes. Two safe options:

1. **Pre-render frames once**, store them in an array, and cycle the array on each tick. No per-tick
   allocation, no leak. Best for a fixed loop (e.g. a pulsing "working" dot). **Preferred.**
2. **Generate per-tick but free the previous frame** immediately after swapping (`DestroyIcon` on
   the outgoing handle once it is no longer the active icon). More error-prone; only worth it if
   frames are computed live.

## Thread / state fit

Animation must be driven from the UI thread, same as `ApplyState`. The listener already marshals
state changes through `_marshal` → `ApplyState`, so the natural design is: `ApplyState`
starts/stops/reconfigures the animation timer for the current state (start a pulse on Working, stop
on Done and show a static frame). **Don't touch the timer or icon from the listener thread.**

## Per-state ideas

| State | Color | Suggested animation |
|---|---|---|
| Working | `#E53935` red | Pulse brightness, or a small rotating arc — signals activity. |
| Waiting | `#FFB300` yellow | Slow blink — draws attention to a permission prompt. |
| Done | `#43A047` green | Static, no animation. |

## Caveat

Animated tray icons can be visually noisy; some users find them annoying. A **slow** pulse/blink
reads as "status," whereas a fast spinner reads as "notification spam." Favor slow, subtle motion.

## Implementation sketch (option 1)

- Pre-render N frames per animated state in `TrayContext` init, tracking every `HICON` in
  `_ownedHandles` so `Dispose` frees them.
- Add a `Timer` field; `ApplyState` sets the active frame array + index and starts/stops the timer.
- On `Tick`, advance the index and assign `_trayIcon.Icon = frames[index]`.

## Re: the Claude mascot SVG animations

Source: [Codrops — Reverse-engineering Claude AI's mascot animations with SVG and GSAP](https://tympanus.net/codrops/2026/05/05/reverse-engineering-claude-ais-mascot-animations-with-svg-and-gsap/)

What it is: four Claude mascots built from SVG `<rect>` elements, animated with **GSAP** in a
browser/React DOM. A hybrid of continuous GSAP tweens and frame-by-frame sprite swapping (flag-wave,
weightlifting use distinct SVG drawings whose visibility toggles).

### Technical adaptation path

- **Cannot be used directly.** SVG + GSAP is browser/DOM tech; `NotifyIcon` only accepts a static
  `HICON` per frame. You'd have to **rasterize each animation to a sequence of small frames**
  (16/20/24 px for DPI) — e.g. play the SVG headless and capture frames — then load them as
  pre-rendered `HICON`s and cycle them (the option-1 pattern above).
- **Legibility risk:** these are detailed multi-rect illustrations. At **16×16 px they turn to
  mush** — fine detail and the sprite frames won't read. Tray icons want bold, simple silhouettes.

### Licensing caveat (the bigger issue)

- The article grants **no license**, and the author reverse-engineered the mascot from social clips
  without permission to redistribute.
- The mascot is **Anthropic's branded character / trade dress**. Shipping it in a third-party tool
  is a trademark/copyright risk the blog cannot authorize.
- The **animation techniques** (GSAP tweens, sprite-swap, `<rect>` construction) are fine to learn
  from and reimplement; the **mascot designs themselves** are not ours to use.

### Recommendation

Borrow the *technique*, not the *asset*. Use the pre-rendered-frame approach with an **original,
simple shape** (pulsing/blinking dot or a minimal abstract glyph) that stays legible at 16 px — it
sidesteps the IP question and reads better in the tray. If the official mascot is genuinely wanted,
the clean path is to **request brand-asset permission from Anthropic** rather than lift it from the
article.
