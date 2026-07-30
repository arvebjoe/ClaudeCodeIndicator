// ClaudeCodeIndicator — ESP32-S3 WS2812 RGB indicator firmware
// ---------------------------------------------------------------------------
// Pairs with the Windows tray app (ClaudeCodeIndicator). The tray app fires:
//
//     GET http://<esp-ip>/state?value=<working|waiting|done>&rgb=<hexcolor>
//
// e.g.  GET http://192.168.0.123/state?value=working&rgb=E53935
//
//   value : semantic state — chooses the LED effect (breathe / blink / solid)
//   rgb   : 6-digit hex, no '#' — the exact tray palette color for this state
//
// Board: ESP32-S3 (XH-S3E, N16R8) with one onboard addressable WS2812 LED.
// No external library required — uses the ESP32 Arduino core's neopixelWrite().
//
// Arduino IDE setup:
//   1. Tools > Board > esp32 > "ESP32S3 Dev Module"
//   2. Tools > USB CDC On Boot: "Enabled"   (so the Serial monitor works over USB)
//   3. Tools > Flash Size: 16MB / PSRAM: OPI PSRAM   (matches N16R8 — optional)
//   4. Fill in WIFI_SSID / WIFI_PASS below, flash, open Serial Monitor @115200,
//      note the printed IP, then enter it in the tray app's "Set ESP32 address…".
// ---------------------------------------------------------------------------

#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>

// ----------------------------------------------------------------- CONFIG ---
const char* WIFI_SSID = "iot";
const char* WIFI_PASS = "kron62hennie";

// Onboard WS2812 data pin. GPIO48 is correct for most ESP32-S3 boards
// (incl. the XH-S3E / S3 DevKitC-1 layout). If the LED never lights, try 38.
// If your core defines RGB_BUILTIN for the selected board, we use that instead.
#ifdef RGB_BUILTIN
  #define LED_PIN RGB_BUILTIN
#else
  #define LED_PIN 48
#endif

// Global brightness 0–255. WS2812s are blinding at full power; 40 is plenty
// for a desk indicator. Raise if you want it brighter.
const uint8_t BRIGHTNESS = 40;

// mDNS hostname — lets you use "claude-indicator.local" instead of the raw IP
// in the tray app (works if your network/OS resolves mDNS; Windows needs
// Bonjour or Win10+ with mDNS support).
const char* MDNS_HOSTNAME = "claude-indicator";
// ----------------------------------------------------------------------------

WebServer server(80);

// State the loop() renders. Set by the /state handler.
enum Effect { EFFECT_SOLID, EFFECT_BREATHE, EFFECT_BLINK };
volatile Effect  g_effect = EFFECT_SOLID;
volatile uint8_t g_r = 0, g_g = 60, g_b = 0;   // boot color = dim green (done/idle)

// Render `r,g,b` to the onboard pixel, scaled by global brightness.
void showColor(uint8_t r, uint8_t g, uint8_t b) {
  // neopixelWrite expects raw 0–255 per channel; apply brightness here.
  uint8_t sr = (uint16_t)r * BRIGHTNESS / 255;
  uint8_t sg = (uint16_t)g * BRIGHTNESS / 255;
  uint8_t sb = (uint16_t)b * BRIGHTNESS / 255;
  neopixelWrite(LED_PIN, sr, sg, sb);
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
  if (value == "working")      effect = EFFECT_BREATHE;  // pulse — work in progress
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
    "<h2>Claude Code Indicator</h2>"
    "<p>WS2812 RGB indicator is online.</p>"
    "<p>Endpoint: <code>/state?value=&lt;working|waiting|done&gt;&amp;rgb=&lt;hex&gt;</code></p>"
    "</body></html>";
  server.send(200, "text/html", html);
}

void connectWiFi() {
  Serial.printf("Connecting to WiFi \"%s\"", WIFI_SSID);
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASS);

  // Blink dim blue while connecting.
  uint32_t start = millis();
  bool on = false;
  while (WiFi.status() != WL_CONNECTED) {
    on = !on;
    showColor(0, 0, on ? 255 : 0);
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
  showColor(g_r, g_g, g_b);  // boot = dim green

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
    case EFFECT_BREATHE: {
      // ~2.5s breathing cycle using a triangle wave on top of a 15% floor.
      uint32_t t = millis() % 2500;
      uint16_t phase = (t < 1250) ? t : (2500 - t);     // 0..1250..0
      uint16_t level = 38 + (uint32_t)phase * (255 - 38) / 1250;  // 38..255
      showColor((uint16_t)r * level / 255,
                (uint16_t)g * level / 255,
                (uint16_t)b * level / 255);
      break;
    }
    case EFFECT_BLINK: {
      // 1s on / 0.5s off — attention-grabbing for "waiting on you".
      bool on = (millis() % 1500) < 1000;
      if (on) showColor(r, g, b);
      else    showColor(0, 0, 0);
      break;
    }
    case EFFECT_SOLID:
    default:
      showColor(r, g, b);
      break;
  }
}
