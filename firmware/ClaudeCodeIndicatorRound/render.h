// render.h — everything that draws pixels for ClaudeCodeIndicatorRound.
// ---------------------------------------------------------------------------
// Pure C++ with no Arduino or display-library dependencies, so the same code
// can be compiled on a PC to render preview PNGs (see tools/preview.cpp).
// The sketch hands it a Sink that pushes RGB565 rectangles to the LCD.
//
// Screen layout (240x240 round GC9A01):
//
//   - two half rings at the edge: left = Claude usage (orange), right = Codex
//     usage (light blue). Each is a fuel gauge: the lit part is the budget
//     left, filling from the bottom up; spent budget goes dim from the top.
//   - the centre "stage" holds the two avatars: Clawd (Claude Code's pixel
//     crab) on the left, the Codex cloud with its >_ face on the right.
//   - under each avatar, the remaining budget as text ($ for Claude, % for
//     Codex), and a status line at the bottom (WIFI…, then the IP briefly).
//
// The rings and labels are redrawn only when their values change; the stage
// is redrawn every frame from an off-screen buffer, so animation never
// flickers.
// ---------------------------------------------------------------------------
#pragma once
#include <stdint.h>
#include <string.h>
#include <math.h>

namespace ui {

// ------------------------------------------------------------------ basics --

enum class Activity : uint8_t { Idle, Working, Waiting };

constexpr int SCREEN = 240;

constexpr uint16_t rgb565(uint8_t r, uint8_t g, uint8_t b) {
  return (uint16_t)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
}

struct RGB { uint8_t r, g, b; };

inline uint16_t to565(RGB c) { return rgb565(c.r, c.g, c.b); }

// a*(1-t) + b*t, t in 0..1
inline RGB mix(RGB a, RGB b, float t) {
  if (t <= 0) return a;
  if (t >= 1) return b;
  return RGB{ (uint8_t)(a.r + (b.r - a.r) * t + 0.5f),
              (uint8_t)(a.g + (b.g - a.g) * t + 0.5f),
              (uint8_t)(a.b + (b.b - a.b) * t + 0.5f) };
}

// Palette. Claude orange matches the tray app's Working color.
constexpr RGB BG            = {0, 0, 0};
constexpr RGB CLAUDE        = {0xD3, 0x73, 0x55};
constexpr RGB CLAUDE_EYE    = {0x14, 0x0C, 0x0A};
constexpr RGB CODEX         = {0x7C, 0xC4, 0xFF};
constexpr RGB CODEX_FACE    = {0x0E, 0x22, 0x3C};
constexpr RGB WAIT_DEFAULT  = {0xE5, 0x39, 0x35};   // tray "waiting" red
constexpr RGB WHITE         = {0xFF, 0xFF, 0xFF};
constexpr RGB STATUS_GREY   = {0x80, 0x80, 0x80};
constexpr float TRACK_LEVEL = 0.18f;                 // dim part of a ring

// Where pixels go. On the device this wraps gfx->draw16bitRGBBitmap().
struct Sink {
  virtual void blit(int x, int y, const uint16_t* buf, int w, int h) = 0;
  virtual ~Sink() {}
};

// An off-screen RGB565 rectangle that maps to (x0, y0) on the screen.
struct Canvas {
  int x0, y0, w, h;
  uint16_t* buf;

  void fill(uint16_t c) { for (int i = 0; i < w * h; i++) buf[i] = c; }

  void rect(int x, int y, int rw, int rh, uint16_t c) {
    int xa = x < 0 ? 0 : x, ya = y < 0 ? 0 : y;
    int xb = x + rw > w ? w : x + rw, yb = y + rh > h ? h : y + rh;
    for (int yy = ya; yy < yb; yy++)
      for (int xx = xa; xx < xb; xx++) buf[yy * w + xx] = c;
  }

  void push(Sink& s) const { s.blit(x0, y0, buf, w, h); }
};

// ------------------------------------------------------------- pixel art --

// Draws a char grid. Each cell becomes a ux*uy block. '.' and ' ' are
// transparent; other chars are looked up in `keys`/`colors` (same order).
inline void drawArt(Canvas& cv, const char* const* rows, int nRows, int x, int y,
                    int ux, int uy, const char* keys, const uint16_t* colors) {
  for (int r = 0; r < nRows; r++) {
    const char* row = rows[r];
    for (int c = 0; row[c]; c++) {
      const char* k = strchr(keys, row[c]);
      if (row[c] == '.' || row[c] == ' ' || !k) continue;
      cv.rect(x + c * ux, y + r * uy, ux, uy, colors[k - keys]);
    }
  }
}

// --------------------------------------------------------------- the font --

// 5x7 glyphs, one byte per row, bit 4 = leftmost column. Only what we print.
struct Glyph { char ch; uint8_t rows[7]; };
static const Glyph FONT[] = {
  {'0', {0x0E,0x11,0x13,0x15,0x19,0x11,0x0E}},
  {'1', {0x04,0x0C,0x04,0x04,0x04,0x04,0x0E}},
  {'2', {0x0E,0x11,0x01,0x02,0x04,0x08,0x1F}},
  {'3', {0x1F,0x02,0x04,0x02,0x01,0x11,0x0E}},
  {'4', {0x02,0x06,0x0A,0x12,0x1F,0x02,0x02}},
  {'5', {0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E}},
  {'6', {0x06,0x08,0x10,0x1E,0x11,0x11,0x0E}},
  {'7', {0x1F,0x01,0x02,0x04,0x08,0x08,0x08}},
  {'8', {0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E}},
  {'9', {0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C}},
  {'.', {0x00,0x00,0x00,0x00,0x00,0x0C,0x0C}},
  {'$', {0x04,0x0F,0x14,0x0E,0x05,0x1E,0x04}},
  {'%', {0x18,0x19,0x02,0x04,0x08,0x13,0x03}},
  {'-', {0x00,0x00,0x00,0x1F,0x00,0x00,0x00}},
  {'!', {0x04,0x04,0x04,0x04,0x04,0x00,0x04}},
  {'?', {0x0E,0x11,0x01,0x02,0x04,0x00,0x04}},
  {'k', {0x10,0x10,0x12,0x14,0x18,0x14,0x12}},
  {'W', {0x11,0x11,0x11,0x15,0x15,0x15,0x0A}},
  {'I', {0x0E,0x04,0x04,0x04,0x04,0x04,0x0E}},
  {'F', {0x1F,0x10,0x10,0x1E,0x10,0x10,0x10}},
  {' ', {0,0,0,0,0,0,0}},
};

inline const Glyph* glyph(char c) {
  for (const Glyph& g : FONT) if (g.ch == c) return &g;
  return nullptr;
}

// Width in pixels of `s` at `scale` (glyph 5 wide + 1 spacing).
inline int textWidth(const char* s, int scale) {
  int n = (int)strlen(s);
  return n ? (n * 6 - 1) * scale : 0;
}

inline void drawText(Canvas& cv, const char* s, int x, int y, int scale, uint16_t c) {
  for (; *s; s++, x += 6 * scale) {
    const Glyph* g = glyph(*s);
    if (!g) continue;
    for (int r = 0; r < 7; r++)
      for (int col = 0; col < 5; col++)
        if (g->rows[r] & (0x10 >> col)) cv.rect(x + col * scale, y + r * scale, scale, scale, c);
  }
}

// ----------------------------------------------------------------- rings --

constexpr float RING_OUTER = 118.5f;
constexpr float RING_INNER = 106.5f;
constexpr float RING_GAP_DEG = 7.0f;   // gap at 12 and 6 o'clock between halves

inline float clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

// Colour of screen pixel (x, y) for the rings, anti-aliased.
// leftLeft/rightLeft: fraction 0..1 of budget left, or <0 if unknown (dim).
inline RGB ringPixel(int x, int y, float leftLeft, float rightLeft) {
  const float cx = SCREEN / 2.0f, cy = SCREEN / 2.0f;
  float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
  float d2 = dx * dx + dy * dy;
  if (d2 < (RING_INNER - 1) * (RING_INNER - 1) || d2 > (RING_OUTER + 1) * (RING_OUTER + 1))
    return BG;
  float d = sqrtf(d2);
  float cover = clamp01(d - RING_INNER + 0.5f) * clamp01(RING_OUTER - d + 0.5f);
  if (cover <= 0) return BG;

  // Angle from 6 o'clock (0°) up the side to 12 o'clock (180°).
  float theta = acosf(dy / d) * (180.0f / 3.14159265f);
  const float span = 180.0f - 2 * RING_GAP_DEG;
  const float pxPerDeg = d * 3.14159265f / 180.0f;
  float along = theta - RING_GAP_DEG;                       // degrees into the arc
  cover *= clamp01(along * pxPerDeg + 0.5f) * clamp01((span - along) * pxPerDeg + 0.5f);
  if (cover <= 0) return BG;

  bool left = dx < 0;
  RGB full = left ? CLAUDE : CODEX;
  float frac = left ? leftLeft : rightLeft;
  RGB track = mix(BG, full, TRACK_LEVEL);
  RGB c = track;
  if (frac >= 0) {
    float lit = clamp01((frac * span - along) * pxPerDeg + 0.5f);
    c = mix(track, full, lit);
  }
  return mix(BG, c, cover);
}

// Redraws the whole screen's ring layer, in horizontal strips of `stripH`
// rows using `strip` (SCREEN*stripH pixels). This also blanks everything
// inside the rings, so redraw labels and the stage afterwards.
inline void drawRings(Sink& s, uint16_t* strip, int stripH, float claudeLeft, float codexLeft) {
  for (int y0 = 0; y0 < SCREEN; y0 += stripH) {
    int h = (y0 + stripH > SCREEN) ? SCREEN - y0 : stripH;
    for (int y = 0; y < h; y++)
      for (int x = 0; x < SCREEN; x++)
        strip[y * SCREEN + x] = to565(ringPixel(x, y0 + y, claudeLeft, codexLeft));
    s.blit(0, y0, strip, SCREEN, h);
  }
}

// ----------------------------------------------------------------- stage --

// The centre area with both avatars. Must stay inside RING_INNER and above
// LABEL_Y (the stage is repainted every frame and would erase the labels).
constexpr int STAGE_X = 40, STAGE_Y = 52, STAGE_W = 160, STAGE_H = 100;
constexpr int SLOT_W = STAGE_W / 2;            // one avatar per half
constexpr int UNIT = 4;                        // screen px per art pixel
constexpr int GROUND = 92;                     // stage y the feet stand on

// Labels under the avatars, and the status line.
constexpr int LABEL_Y = 154, LABEL_H = 16, LABEL_W = 76;
constexpr int STATUS_Y = 178, STATUS_H = 10, STATUS_W = 120;

// Triangle wave in -1..1 with the given period.
inline float tri(uint32_t t, uint32_t period) {
  float p = (float)(t % period) / period;
  return p < 0.5f ? p * 4 - 1 : 3 - p * 4;
}

// Hop height in px (0 on the ground) for the waiting animation.
inline int hop(uint32_t t, uint32_t period, int height) {
  float p = (float)(t % period) / period;      // 0..1, airborne for the first 60%
  if (p > 0.6f) return 0;
  float q = p / 0.6f;
  return (int)(4 * q * (1 - q) * height + 0.5f);
}

// "!" speech bubble in the state colour, its bottom-left corner at (x, y).
inline void drawBubble(Canvas& cv, int x, int y, RGB color) {
  static const char* const B[] = {
    ".#####.",
    "#######",
    "###.###",
    "###.###",
    "###.###",
    "#######",
    "###.###",
    ".#####.",
    "##.....",
    "#......",
  };
  const uint16_t cols[] = { to565(color) };
  // Paint the white "!" first so the bubble's holes show it.
  cv.rect(x + 3 * 2, y - 10 * 2 + 2 * 2, 2, 5 * 2, to565(WHITE));
  drawArt(cv, B, 10, x, y - 10 * 2, 2, 2, "#", cols);
}

// ---- Clawd ------------------------------------------------------------------
// 18x10 art pixels, from claude_full.png / the Claude Code welcome banner:
// a wide block body, two tall eyes just above the arms, arms sticking out of
// both sides, four short legs in two pairs.

struct ClawdPose {
  bool eyesClosed = false;
  int  eyeShift = 0;        // -1/0/+1 art pixels: look left/ahead/right
  int  liftedPair = -1;     // walking: 0 = legs 3&12 lifted, 1 = legs 5&14
  bool armsUp = false;      // waiting: arms raised one row (a little cheer)
};

inline void drawClawd(Canvas& cv, int x, int y, const ClawdPose& p) {
  char rows[10][19];
  for (int r = 0; r < 10; r++) { memset(rows[r], '.', 18); rows[r][18] = 0; }
  for (int r = 0; r <= 7; r++) for (int c = 2; c <= 15; c++) rows[r][c] = '#';
  int armTop = p.armsUp ? 3 : 4;
  for (int r = armTop; r <= armTop + 1; r++)
    for (int c : {0, 1, 16, 17}) rows[r][c] = '#';
  if (!p.eyesClosed)
    for (int r = 2; r <= 3; r++) {
      rows[r][4 + p.eyeShift] = 'o';
      rows[r][13 + p.eyeShift] = 'o';
    }
  const int legs[4] = {3, 5, 12, 14};
  for (int i = 0; i < 4; i++) {
    bool lifted = (p.liftedPair == 0 && (i == 0 || i == 2)) ||
                  (p.liftedPair == 1 && (i == 1 || i == 3));
    rows[8][legs[i]] = '#';
    if (!lifted) rows[9][legs[i]] = '#';
  }
  const char* ptrs[10];
  for (int r = 0; r < 10; r++) ptrs[r] = rows[r];
  const uint16_t cols[] = { to565(CLAUDE), to565(CLAUDE_EYE) };
  // Art pixels are a bit taller than wide, like the reference image.
  drawArt(cv, ptrs, 10, x, y, UNIT, UNIT + 1, "#o", cols);
}

constexpr int CLAWD_W = 18 * UNIT, CLAWD_H = 10 * (UNIT + 1);

// Idle: stands still and blinks now and then (a double blink every third
// time). Working: scuttles side to side, legs alternating, eyes looking where
// it's going, blinking. Waiting: hops with its arms up and a "!" bubble.
inline void drawClaudeSlot(Canvas& cv, Activity a, uint32_t t, RGB waitColor) {
  ClawdPose p;
  int dx = 0, lift = 0;
  uint32_t blinkCycle = t % 4200;
  bool blink = blinkCycle < 130 || ((t / 4200) % 3 == 2 && blinkCycle > 260 && blinkCycle < 390);

  switch (a) {
    case Activity::Idle:
      p.eyesClosed = blink;
      break;
    case Activity::Working: {
      float s = tri(t, 2600);                         // -1..1
      dx = (int)(s * 7);
      bool goingRight = (t % 2600) < 1300;
      p.eyeShift = goingRight ? 1 : -1;
      p.liftedPair = (t / 140) % 2;
      lift = p.liftedPair == 0 ? 1 : 0;               // tiny body bob per step
      p.eyesClosed = blink;
      break;
    }
    case Activity::Waiting:
      lift = hop(t, 800, 12);
      p.armsUp = lift > 3;
      break;
  }
  int x = (SLOT_W - CLAWD_W) / 2 + dx;
  int y = GROUND - CLAWD_H - lift;
  drawClawd(cv, x, y, p);
  if (a == Activity::Waiting) drawBubble(cv, x + CLAWD_W - 10, y - 4, waitColor);
}

// ---- Codex ------------------------------------------------------------------
// 18x14 art pixels: a soft cloud (the shape of the Codex app icon) with a
// terminal prompt for a face — a ">" chevron and a "_" cursor.

static const char* const CODEX_BODY[14] = {
  "....#####.........",
  "...#######.####...",
  "..##############..",
  "..##############..",
  ".################.",
  ".################.",
  "##################",
  "##################",
  "##################",
  "##################",
  "##################",
  ".################.",
  "..##############..",
  "...############...",
};
constexpr int CODEX_W = 18 * UNIT, CODEX_H = 14 * UNIT;

// Face, drawn on top of the body. `cursor` = how many of the four cursor
// cells are lit as typed text (working), or -1 for a plain "_" cursor,
// -2 for none (cursor blink off).
inline void drawCodexFace(Canvas& cv, int x, int y, int cursor, bool squint) {
  uint16_t f = to565(CODEX_FACE);
  // ">" chevron, 2 art pixels thick, rows 5..9 (squint = flattened).
  static const int CHEV[5][2] = {{4, 5}, {5, 6}, {6, 7}, {5, 6}, {4, 5}};
  for (int i = 0; i < 5; i++) {
    if (squint && (i == 0 || i == 4)) continue;
    for (int c = CHEV[i][0]; c <= CHEV[i][1]; c++)
      cv.rect(x + c * UNIT, y + (5 + i) * UNIT, UNIT, UNIT, f);
  }
  if (cursor == -1) {
    cv.rect(x + 9 * UNIT, y + 9 * UNIT, 4 * UNIT, UNIT, f);           // "_"
  } else if (cursor >= 0) {
    // typed "text": small blocks on the baseline, plus a cursor bar after
    for (int i = 0; i < cursor && i < 4; i++)
      cv.rect(x + (9 + i) * UNIT, y + 8 * UNIT + 2, UNIT - 1, UNIT + 2, f);
    int cx = 9 + (cursor < 4 ? cursor : 4);
    if (cx <= 13) cv.rect(x + cx * UNIT, y + 9 * UNIT, UNIT, UNIT, f);
  }
}

// Idle: floats gently, cursor blinking. Working: bobs faster and "types" —
// characters appear one by one after the prompt, then clear. Waiting: hops,
// prompt squints, cursor solid, with the "!" bubble.
inline void drawCodexSlot(Canvas& cv, Activity a, uint32_t t, RGB waitColor) {
  int lift = 0, cursor = -1;
  bool squint = false;
  switch (a) {
    case Activity::Idle:
      lift = (int)((tri(t, 3200) + 1) * 1.5f + 0.5f);   // 0..3 px float
      cursor = ((t / 600) % 2) ? -1 : -2;
      break;
    case Activity::Working:
      lift = (t / 220) % 2;
      cursor = (int)((t / 160) % 7);                    // 0..4 typed, hold 2 frames
      if (cursor > 4) cursor = 4;
      break;
    case Activity::Waiting:
      lift = hop(t + 400, 800, 12);                     // off-beat with Clawd
      squint = lift > 3;
      cursor = -1;
      break;
  }
  int x = SLOT_W + (SLOT_W - CODEX_W) / 2;
  int y = GROUND - CODEX_H - lift;
  const uint16_t cols[] = { to565(CODEX) };
  drawArt(cv, CODEX_BODY, 14, x, y, UNIT, UNIT, "#", cols);
  drawCodexFace(cv, x, y, cursor, squint);
  if (a == Activity::Waiting) drawBubble(cv, x + CODEX_W - 10, y, waitColor);
}

// One full stage frame (both avatars) into `cv` (STAGE_W x STAGE_H).
inline void drawStage(Canvas& cv, Activity claude, Activity codex, uint32_t t, RGB waitColor) {
  cv.fill(to565(BG));
  drawClaudeSlot(cv, claude, t, waitColor);
  drawCodexSlot(cv, codex, t, waitColor);
}

// ---------------------------------------------------------------- labels --

// Remaining Claude budget as "$779" (or "$1.2k"), "--" if unknown.
inline void formatDollars(char* out, float spent, float limit) {
  if (limit <= 0) { strcpy(out, "--"); return; }
  float left = limit - spent;
  if (left < 0) left = 0;
  int v;
  if (left >= 9999.5f) {
    v = (int)(left / 1000 + 0.5f);
    char* p = out; *p++ = '$';
    char tmp[8]; int n = 0;
    do { tmp[n++] = '0' + v % 10; v /= 10; } while (v);
    while (n) *p++ = tmp[--n];
    *p++ = 'k'; *p = 0;
    return;
  }
  v = (int)(left + 0.5f);
  char* p = out; *p++ = '$';
  char tmp[8]; int n = 0;
  do { tmp[n++] = '0' + v % 10; v /= 10; } while (v);
  while (n) *p++ = tmp[--n];
  *p = 0;
}

// Remaining Codex budget as "92%", "--" if unknown (usedPct < 0).
inline void formatPercent(char* out, float usedPct) {
  if (usedPct < 0) { strcpy(out, "--"); return; }
  int v = (int)(100 - usedPct + 0.5f);
  if (v < 0) v = 0;
  if (v > 100) v = 100;
  char* p = out; char tmp[4]; int n = 0;
  do { tmp[n++] = '0' + v % 10; v /= 10; } while (v);
  while (n) *p++ = tmp[--n];
  *p++ = '%'; *p = 0;
}

// Draws one centred label in a scratch canvas and pushes it.
inline void pushLabel(Sink& s, uint16_t* scratch, int cxScreen, int y, int w, int h,
                      const char* text, int scale, RGB color) {
  Canvas cv{cxScreen - w / 2, y, w, h, scratch};
  cv.fill(to565(BG));
  drawText(cv, text, (w - textWidth(text, scale)) / 2, (h - 7 * scale) / 2, scale, to565(color));
  cv.push(s);
}

// Both budget labels. scratch needs LABEL_W*LABEL_H pixels.
inline void drawLabels(Sink& s, uint16_t* scratch, float spent, float limit, float codexUsedPct) {
  char buf[12];
  formatDollars(buf, spent, limit);
  pushLabel(s, scratch, STAGE_X + SLOT_W / 2, LABEL_Y, LABEL_W, LABEL_H, buf, 2,
            limit > 0 ? CLAUDE : mix(BG, CLAUDE, 0.5f));
  formatPercent(buf, codexUsedPct);
  pushLabel(s, scratch, STAGE_X + SLOT_W + SLOT_W / 2, LABEL_Y, LABEL_W, LABEL_H, buf, 2,
            codexUsedPct >= 0 ? CODEX : mix(BG, CODEX, 0.5f));
}

// Small grey line near the bottom: "WIFI..." while connecting, the IP for a
// while after. Empty string clears it. scratch needs STATUS_W*STATUS_H pixels.
inline void drawStatus(Sink& s, uint16_t* scratch, const char* text) {
  pushLabel(s, scratch, SCREEN / 2, STATUS_Y, STATUS_W, STATUS_H, text, 1, STATUS_GREY);
}

// Fraction of budget left for the rings, or -1 when unknown.
inline float claudeLeft(float spent, float limit) {
  return limit > 0 ? clamp01(1 - spent / limit) : -1;
}
inline float codexLeft(float usedPct) {
  return usedPct >= 0 ? clamp01(1 - usedPct / 100) : -1;
}

}  // namespace ui

static_assert(ui::STAGE_Y + ui::STAGE_H <= ui::LABEL_Y, "stage would overwrite the labels");
