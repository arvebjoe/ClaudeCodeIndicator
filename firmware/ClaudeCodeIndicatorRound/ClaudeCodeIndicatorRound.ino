// ClaudeCodeIndicatorRound — ESP32-2424S012 (ESP32-C3 + 1.28" round GC9A01)
// ---------------------------------------------------------------------------
// Pairs with the Windows tray app (ClaudeCodeIndicator), same HTTP API as the
// matrix firmware plus two Codex fields:
//
//   GET http://<esp-ip>/state?value=<working|waiting|done>&rgb=<hex>
//         [&spent=<usd>&limit=<usd>]          Claude monthly usage credits
//         [&codex=<working|waiting|done>]     Codex state (from Codex hooks)
//         [&cused=<0-100>]                    Codex usage, percent used
//
// Screen (see render.h for details):
//   left half ring  = Claude budget left (orange), right half ring = Codex
//   budget left (light blue); both drain from the top as budget is spent and
//   go dim when unknown. In the middle, Clawd (Claude) and the Codex cloud,
//   each animated by its own state:
//     done    → idle     Clawd blinks now and then; Codex floats, cursor blinks
//     working → working  Clawd scuttles with legs walking; Codex "types"
//     waiting → waiting  both hop with a "!" bubble — they need you
//   Under them: dollars left for Claude, percent left for Codex.
//
// Board: ESP32-2424S012 ("ESP32-C3 1.28 inch 240x240 round display", with or
// without CST816 touch — touch is not used). Pins below are the commonly
// reported ones for this board; check them against your board if the screen
// stays dark.
//
// Arduino IDE setup:
//   1. Boards Manager: "esp32" by Espressif (2.0.x or 3.x)
//   2. Library Manager: install "GFX Library for Arduino" (Arduino_GFX, by
//      moononournation)
//   3. Tools > Board > esp32 > "ESP32C3 Dev Module"
//   4. Tools > USB CDC On Boot: "Enabled"   (Serial monitor over USB-C)
//   5. Tools > Flash Size: 4MB
//   6. Copy secrets.example.h to secrets.h, fill in WiFi, flash. The IP shows
//      at the bottom of the screen for a minute (and on Serial @115200);
//      enter it in the tray app's "Set ESP32 address…".
// ---------------------------------------------------------------------------

#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <Arduino_GFX_Library.h>
#include "render.h"

// ----------------------------------------------------------------- CONFIG ---
#include "secrets.h"   // copy secrets.example.h to secrets.h and fill it in (gitignored)
const char* WIFI_SSID = WIFI_SSID_VALUE;
const char* WIFI_PASS = WIFI_PASS_VALUE;

// ESP32-2424S012 display wiring (GC9A01 over SPI, no reset line).
#define PIN_SCLK  6
#define PIN_MOSI  7
#define PIN_DC    2
#define PIN_CS    10
#define PIN_BL    3

// Backlight 0–255 (PWM). Full brightness is glaring on a desk; ~120 is calm.
const uint8_t BRIGHTNESS = 120;

// 0–3 quarter turns. Pick the one that puts the USB-C port where you want it.
const uint8_t ROTATION = 0;

// Animation frame interval (ms). ~30 fps.
const uint32_t FRAME_MS = 33;

// How long the IP stays on screen after connecting (ms).
const uint32_t SHOW_IP_MS = 60000;

const char* MDNS_HOSTNAME = "claude-indicator";
// ----------------------------------------------------------------------------

Arduino_DataBus* bus = new Arduino_ESP32SPI(PIN_DC, PIN_CS, PIN_SCLK, PIN_MOSI, GFX_NOT_DEFINED);
Arduino_GFX* gfx = new Arduino_GC9A01(bus, GFX_NOT_DEFINED /* RST */, ROTATION, true /* IPS */);
WebServer server(80);

// Pushes render.h's off-screen rectangles to the panel.
struct GfxSink : ui::Sink {
  void blit(int x, int y, const uint16_t* buf, int w, int h) override {
    gfx->draw16bitRGBBitmap(x, y, const_cast<uint16_t*>(buf), w, h);
  }
} sink;

// Buffers: the animated stage (~33 KB), ring strips, and labels.
static uint16_t stageBuf[ui::STAGE_W * ui::STAGE_H];
static uint16_t stripBuf[ui::SCREEN * 16];
static uint16_t labelBuf[ui::STATUS_W * ui::LABEL_H];

// What the tray app last told us. Everything here is touched only from
// loop() (the WebServer handler runs inside server.handleClient()).
ui::Activity g_claude = ui::Activity::Idle;
ui::Activity g_codex  = ui::Activity::Idle;
ui::RGB      g_waitColor = ui::WAIT_DEFAULT;
float g_spent = 0, g_limit = 0;      // limit <= 0 → unknown
float g_codexUsed = -1;              // < 0 → unknown

// What's currently drawn, to redraw rings/labels only when they change.
bool  g_ringsDirty = true;
float g_drawnSpent = -2, g_drawnLimit = -2, g_drawnCodex = -2;
char  g_status[24] = "";
char  g_drawnStatus[24] = "-";
uint32_t g_connectedAt = 0;
bool  g_wasConnected = false;

ui::Activity parseActivity(const String& v) {
  if (v == "working") return ui::Activity::Working;
  if (v == "waiting") return ui::Activity::Waiting;
  return ui::Activity::Idle;   // done / idle / unknown
}

// Parse one hex byte from s[i..i+1]; -1 on bad input.
int hexByte(const String& s, int i) {
  auto nib = [](char c) -> int {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
  };
  int hi = nib(s.charAt(i)), lo = nib(s.charAt(i + 1));
  return (hi < 0 || lo < 0) ? -1 : (hi << 4) | lo;
}

void handleState() {
  String value = server.arg("value");
  g_claude = parseActivity(value);

  // The tray sends its "waiting" red with the waiting state; use it for the
  // "!" bubbles so the colour follows the tray palette.
  String rgb = server.arg("rgb");
  if (value == "waiting" && rgb.length() >= 6) {
    int r = hexByte(rgb, 0), g = hexByte(rgb, 2), b = hexByte(rgb, 4);
    if (r >= 0 && g >= 0 && b >= 0) g_waitColor = ui::RGB{(uint8_t)r, (uint8_t)g, (uint8_t)b};
  }

  // Missing fields mean "unknown" (tracking off / no reading yet).
  g_codex = server.hasArg("codex") ? parseActivity(server.arg("codex")) : ui::Activity::Idle;
  g_limit = server.hasArg("limit") ? server.arg("limit").toFloat() : 0;
  g_spent = server.hasArg("spent") ? server.arg("spent").toFloat() : 0;
  g_codexUsed = server.hasArg("cused") ? server.arg("cused").toFloat() : -1;

  Serial.printf("[/state] value=%s codex=%s spent=%.2f limit=%.2f cused=%.1f\n",
                value.c_str(), server.arg("codex").c_str(), g_spent, g_limit, g_codexUsed);
  server.send(200, "text/plain", "ok");
}

void handleRoot() {
  String html =
    "<html><body style='font-family:sans-serif'>"
    "<h2>Claude Code Indicator (round display)</h2>"
    "<p>Endpoint: <code>/state?value=&lt;working|waiting|done&gt;&amp;rgb=&lt;hex&gt;"
    "[&amp;spent=&lt;usd&gt;&amp;limit=&lt;usd&gt;][&amp;codex=&lt;working|waiting|done&gt;]"
    "[&amp;cused=&lt;percent&gt;]</code></p>"
    "</body></html>";
  server.send(200, "text/html", html);
}

// Redraws rings, labels and status line if anything they show has changed.
void drawStatic() {
  bool valuesChanged = g_spent != g_drawnSpent || g_limit != g_drawnLimit ||
                       g_codexUsed != g_drawnCodex;
  if (g_ringsDirty || valuesChanged) {
    // Rings blank the middle too, so labels and status follow.
    ui::drawRings(sink, stripBuf, 16, ui::claudeLeft(g_spent, g_limit), ui::codexLeft(g_codexUsed));
    ui::drawLabels(sink, labelBuf, g_spent, g_limit, g_codexUsed);
    g_drawnSpent = g_spent; g_drawnLimit = g_limit; g_drawnCodex = g_codexUsed;
    g_ringsDirty = false;
    g_drawnStatus[0] = '-'; g_drawnStatus[1] = 0;   // force status redraw
  }
  if (strcmp(g_status, g_drawnStatus) != 0) {
    ui::drawStatus(sink, labelBuf, g_status);
    strcpy(g_drawnStatus, g_status);
  }
}

// Keeps WiFi up without blocking the animation, and sets the status line.
void tickWiFi() {
  static uint32_t lastAttempt = 0;
  bool up = WiFi.status() == WL_CONNECTED;
  if (up && !g_wasConnected) {
    g_wasConnected = true;
    g_connectedAt = millis();
    Serial.print("WiFi connected. IP address: ");
    Serial.println(WiFi.localIP());
    Serial.println("Enter this IP in the tray app's \"Set ESP32 address…\".");
    if (MDNS.begin(MDNS_HOSTNAME)) {
      MDNS.addService("http", "tcp", 80);
      Serial.printf("mDNS: http://%s.local/\n", MDNS_HOSTNAME);
    }
  } else if (!up) {
    if (g_wasConnected) Serial.println("WiFi lost, reconnecting…");
    g_wasConnected = false;
    if (millis() - lastAttempt > 30000) {      // retry from scratch every 30 s
      lastAttempt = millis();
      WiFi.disconnect();
      WiFi.begin(WIFI_SSID, WIFI_PASS);
    }
  }

  if (!up)                                         strcpy(g_status, "WIFI...");
  else if (millis() - g_connectedAt < SHOW_IP_MS)  strncpy(g_status, WiFi.localIP().toString().c_str(), sizeof g_status - 1);
  else                                             g_status[0] = 0;
}

void setup() {
  Serial.begin(115200);
  delay(200);

  if (!gfx->begin(80000000)) Serial.println("gfx->begin() failed");
  gfx->fillScreen(0x0000);
  pinMode(PIN_BL, OUTPUT);
  analogWrite(PIN_BL, BRIGHTNESS);

  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  Serial.printf("Connecting to WiFi \"%s\"\n", WIFI_SSID);

  server.on("/", handleRoot);
  server.on("/state", handleState);
  server.onNotFound([]() { server.send(404, "text/plain", "not found"); });
  server.begin();
}

void loop() {
  server.handleClient();
  tickWiFi();
  drawStatic();

  static uint32_t lastFrame = 0;
  uint32_t now = millis();
  if (now - lastFrame >= FRAME_MS) {
    lastFrame = now;
    ui::Canvas cv{ui::STAGE_X, ui::STAGE_Y, ui::STAGE_W, ui::STAGE_H, stageBuf};
    ui::drawStage(cv, g_claude, g_codex, now, g_waitColor);
    cv.push(sink);
  }
}
