// ClaudeCodeIndicatorMatrix — ESP32-S3-Matrix 8x8 RGB indicator firmware
// ---------------------------------------------------------------------------
// Pairs with the Windows tray app (ClaudeCodeIndicator). The tray app fires:
//
//     GET http://<esp-ip>/state?value=<working|waiting|done>&rgb=<hexcolor>
//
// e.g.  GET http://192.168.0.123/state?value=working&rgb=D37355
//
//   value : semantic state — chooses the LED effect (eye-blink / blink / solid)
//   rgb   : 6-digit hex, no '#' — the exact tray palette color for this state
//
// Instead of a single pixel, this board renders the Claude mascot as 8x8
// pixel art (derived from claude_full.png in the repo root), tinted in the
// state color. The eyes are unlit pixels, so they stay black on the matrix.
//
//   working : solid mascot (tray sends Claude orange), eyes blink shut
//   waiting : whole mascot blinks (tray sends red) — needs your feedback
//   done    : solid mascot (tray sends green) — idle
//
// Board: ESP32-S3-Matrix (Waveshare / SpotPear, ESP32-S3FH4R2) with an
// onboard 8x8 WS2812 RGB matrix on GPIO14.
//
// Arduino IDE setup:
//   1. Library Manager: install "Adafruit NeoPixel"
//   2. Tools > Board > esp32 > "ESP32S3 Dev Module"
//   3. Tools > USB CDC On Boot: "Enabled"   (Serial monitor over USB)
//   4. Tools > Flash Size: 4MB / PSRAM: "QSPI PSRAM"   (ESP32-S3FH4R2)
//   5. Fill in WIFI_SSID / WIFI_PASS below, flash, open Serial Monitor @115200,
//      note the printed IP, then enter it in the tray app's "Set ESP32 address…".
// ---------------------------------------------------------------------------

#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <Adafruit_NeoPixel.h>

// ----------------------------------------------------------------- CONFIG ---
const char* WIFI_SSID = "iot";
const char* WIFI_PASS = "kron62hennie";

// Onboard 8x8 WS2812 matrix data pin on the ESP32-S3-Matrix board.
#define MATRIX_PIN   14
#define MATRIX_W     8
#define MATRIX_H     8
#define NUM_PIXELS   (MATRIX_W * MATRIX_H)

// Set true if rows alternate direction on your matrix (mascot looks torn
// apart / interleaved on odd rows). The stock board is plain row-major.
const bool SERPENTINE = false;

// Orientation fixes — with a circle these didn't matter, with a mascot they
// do. If the mascot is sideways/upside down, bump ROTATION (0–3 quarter
// turns clockwise). If it's mirrored left-right, flip MIRROR_X.
const uint8_t ROTATION = 2;
const bool MIRROR_X = false;

// Global brightness 0–255. 64 WS2812s at full white pull ~3.8 A — way past
// the 800 mA LDO — so keep this low. 25 is plenty for a desk indicator.
const uint8_t BRIGHTNESS = 25;

// mDNS hostname — lets you use "claude-indicator.local" instead of the raw IP
// in the tray app (works if your network/OS resolves mDNS; Windows needs
// Bonjour or Win10+ with mDNS support).
const char* MDNS_HOSTNAME = "claude-indicator";
// ----------------------------------------------------------------------------

// Color order: this board's matrix takes R,G,B on the wire — with the usual
// NEO_GRB the red/green channels land swapped (orange shows green, green
// shows salmon). The boot self-test flashes RED → GREEN → BLUE; if you see a
// different order on the hardware, adjust NEO_RGB here.
Adafruit_NeoPixel pixels(NUM_PIXELS, MATRIX_PIN, NEO_RGB + NEO_KHZ800);
WebServer server(80);

// State the loop() renders. Set by the /state handler.
enum Effect { EFFECT_SOLID, EFFECT_EYEBLINK, EFFECT_BLINK };
volatile Effect  g_effect = EFFECT_SOLID;
volatile uint8_t g_r = 0, g_g = 160, g_b = 0;  // boot color = green (done/idle);
                                               // gamma+BRIGHTNESS dims it a lot

// Per-pixel sprite mask 0–255, precomputed in setup(). 255 = mascot body
// (lit in the state color), 0 = background and eyes (off/black).
uint8_t g_coverage[NUM_PIXELS];

// The Claude mascot, one byte per row, MSB = leftmost column.
// Proportions taken from claude_full.png: 6-wide body, two tall eyes,
// full-width arm band, lower body, two leg pairs with a center gap.
const uint8_t MASCOT[MATRIX_H] = {
  0b01111110,   // .XXXXXX.  head
  0b01011010,   // .X.XX.X.  eyes (unlit)
  0b01011010,   // .X.XX.X.  eyes (unlit)
  0b11111111,   // XXXXXXXX  arms
  0b11111111,   // XXXXXXXX  arms
  0b01111110,   // .XXXXXX.  body
  0b01100110,   // .XX..XX.  legs
  0b01100110,   // .XX..XX.  legs
};

// Eye pixels — normally unlit (black); briefly filled with the body color
// when the mascot "blinks" during the working state.
const uint8_t MASCOT_EYES[MATRIX_H] = {
  0b00000000,
  0b00100100,   // ..X..X..  eyes
  0b00100100,   // ..X..X..  eyes
  0b00000000,
  0b00000000,
  0b00000000,
  0b00000000,
  0b00000000,
};
uint8_t g_eyes[NUM_PIXELS];

int pixelIndex(int x, int y) {
  for (uint8_t r = 0; r < (ROTATION & 3); r++) {   // quarter turns CW
    int t = x; x = MATRIX_W - 1 - y; y = t;
  }
  if (MIRROR_X) x = MATRIX_W - 1 - x;
  if (SERPENTINE && (y & 1)) x = MATRIX_W - 1 - x;
  return y * MATRIX_W + x;
}

void buildMascotMask() {
  for (int y = 0; y < MATRIX_H; y++) {
    for (int x = 0; x < MATRIX_W; x++) {
      int i = pixelIndex(x, y);
      g_coverage[i] = ((MASCOT[y] >> (7 - x)) & 1) ? 255 : 0;
      g_eyes[i]     = (MASCOT_EYES[y] >> (7 - x)) & 1;
    }
  }
}

// Render the mascot in `r,g,b` scaled by `level` (0–255, for effects).
// `eyesClosed` fills the eye pixels with the body color (a blink).
// Global BRIGHTNESS is applied by the NeoPixel library.
// (No default argument — the Arduino prototype generator chokes on them.)
void showSprite(uint8_t r, uint8_t g, uint8_t b, uint8_t level, bool eyesClosed) {
  // sRGB → LED gamma. Without this, mid-tone colors (like Claude orange)
  // wash out toward white because WS2812 PWM is linear but the palette
  // values are perceptual.
  r = Adafruit_NeoPixel::gamma8(r);
  g = Adafruit_NeoPixel::gamma8(g);
  b = Adafruit_NeoPixel::gamma8(b);
  for (int i = 0; i < NUM_PIXELS; i++) {
    uint8_t cov = g_coverage[i];
    if (eyesClosed && g_eyes[i]) cov = 255;
    uint16_t k = (uint16_t)cov * level / 255;
    pixels.setPixelColor(i, (uint8_t)((uint16_t)r * k / 255),
                            (uint8_t)((uint16_t)g * k / 255),
                            (uint8_t)((uint16_t)b * k / 255));
  }
  pixels.show();
}

// Parse one hex byte from str[i..i+1]; returns 0 on bad input.
uint8_t hexByte(const String& s, int i) {
  auto nib = [](char c) -> int {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
  };
  int hi = nib(s.charAt(i));
  int lo = nib(s.charAt(i + 1));
  if (hi < 0 || lo < 0) return 0;
  return (uint8_t)((hi << 4) | lo);
}

void handleState() {
  String rgb   = server.arg("rgb");     // e.g. "E53935"
  String value = server.arg("value");   // "working" | "waiting" | "done"

  // Default to the request color; fall back to current if hex is malformed.
  uint8_t r = g_r, g = g_g, b = g_b;
  if (rgb.length() >= 6) {
    r = hexByte(rgb, 0);
    g = hexByte(rgb, 2);
    b = hexByte(rgb, 4);
  }

  Effect effect = EFFECT_SOLID;
  if (value == "working")      effect = EFFECT_EYEBLINK; // solid, eyes blink — working
  else if (value == "waiting") effect = EFFECT_BLINK;    // blink — needs you
  else                         effect = EFFECT_SOLID;    // done / idle / unknown

  g_r = r; g_g = g; g_b = b;
  g_effect = effect;

  Serial.printf("[/state] value=%s rgb=%02X%02X%02X effect=%d\n",
                value.c_str(), r, g, b, (int)effect);

  server.send(200, "text/plain", "ok");
}

void handleRoot() {
  String html =
    "<html><body style='font-family:sans-serif'>"
    "<h2>Claude Code Indicator (8x8 Matrix)</h2>"
    "<p>WS2812 matrix indicator is online.</p>"
    "<p>Endpoint: <code>/state?value=&lt;working|waiting|done&gt;&amp;rgb=&lt;hex&gt;</code></p>"
    "</body></html>";
  server.send(200, "text/html", html);
}

void connectWiFi() {
  Serial.printf("Connecting to WiFi \"%s\"", WIFI_SSID);
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASS);

  // Blink the mascot in blue while connecting.
  uint32_t start = millis();
  bool on = false;
  while (WiFi.status() != WL_CONNECTED) {
    on = !on;
    showSprite(0, 0, 255, on ? 255 : 0, false);
    Serial.print(".");
    delay(250);
    if (millis() - start > 30000) {   // 30s timeout → retry from scratch
      Serial.println("\nWiFi timeout, retrying…");
      WiFi.disconnect();
      WiFi.begin(WIFI_SSID, WIFI_PASS);
      start = millis();
    }
  }
  Serial.println();
  Serial.print("WiFi connected. IP address: ");
  Serial.println(WiFi.localIP());
  Serial.println("Enter this IP in the tray app's \"Set ESP32 address…\".");
}

void setup() {
  Serial.begin(115200);
  delay(200);

  pixels.begin();
  pixels.setBrightness(BRIGHTNESS);
  buildMascotMask();

  // Channel-order self-test: the mascot flashes RED, GREEN, BLUE in that
  // order. A different sequence on the hardware means the NEO_* color order
  // in the pixels constructor is wrong for this matrix.
  showSprite(255, 0, 0, 255, false); delay(500);
  showSprite(0, 255, 0, 255, false); delay(500);
  showSprite(0, 0, 255, 255, false); delay(500);

  showSprite(g_r, g_g, g_b, 255, false);  // boot = green mascot

  connectWiFi();

  if (MDNS.begin(MDNS_HOSTNAME)) {
    MDNS.addService("http", "tcp", 80);
    Serial.printf("mDNS: http://%s.local/\n", MDNS_HOSTNAME);
  }

  server.on("/", handleRoot);
  server.on("/state", handleState);
  server.onNotFound([]() { server.send(404, "text/plain", "not found"); });
  server.begin();
  Serial.println("HTTP server started on port 80.");
}

void loop() {
  server.handleClient();

  // Reconnect transparently if WiFi drops.
  if (WiFi.status() != WL_CONNECTED) {
    connectWiFi();
  }

  // --- Render the current effect (non-blocking) ---------------------------
  uint8_t r = g_r, g = g_g, b = g_b;

  switch (g_effect) {
    case EFFECT_EYEBLINK: {
      // Solid mascot; every 2s the eyes blink shut for 200ms — "working".
      bool closed = (millis() % 2000) >= 1800;
      showSprite(r, g, b, 255, closed);
      break;
    }
    case EFFECT_BLINK: {
      // 1s on / 0.5s off — attention-grabbing for "waiting on you".
      bool on = (millis() % 1500) < 1000;
      showSprite(r, g, b, on ? 255 : 0, false);
      break;
    }
    case EFFECT_SOLID:
    default:
      showSprite(r, g, b, 255, false);
      break;
  }
}
