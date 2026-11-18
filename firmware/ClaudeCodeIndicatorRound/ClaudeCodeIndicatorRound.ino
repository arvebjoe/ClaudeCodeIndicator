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
// Wi-Fi setup (no credentials in the source):
//   The network name and password are entered on the board's own web page
//   and kept in flash (NVS, via Preferences). With nothing saved, or when the
//   saved network can't be reached for PORTAL_AFTER_MS, the board opens its
//   own network "CLAUDE-XXXX" and the screen says so. Join it and a captive
//   portal opens the setup page (or browse to http://192.168.4.1/). Once the
//   board is on your Wi-Fi, the same page is at http://<board-ip>/, where the
//   network can be changed or forgotten. The setup network closes itself
//   PORTAL_LINGER_MS after the board connects.
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
//   6. Flash, then set up Wi-Fi as above. The IP shows at the bottom of the
//      screen for a minute (and on Serial @115200); enter it in the tray
//      app's "Set ESP32 address…".
// ---------------------------------------------------------------------------

#include <WiFi.h>
#include <WebServer.h>
#include <DNSServer.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include <Arduino_GFX_Library.h>
#include "render.h"

// ----------------------------------------------------------------- CONFIG ---
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

// Setup network. The name gets "-XXXX" from the board's MAC appended so two
// boards don't clash. Password: "" = open network (easiest to join from a
// phone), or 8–63 characters; it's shown on the screen either way.
const char* AP_PREFIX   = "CLAUDE";
const char* AP_PASSWORD = "";

// Open the setup network when the saved Wi-Fi has been unreachable this long.
const uint32_t PORTAL_AFTER_MS = 20000;
// Retry the saved Wi-Fi this often while it's down.
const uint32_t RETRY_MS = 30000;
// Keep the setup network up this long after joining Wi-Fi, so the setup page
// can show the new address before the phone drops off.
const uint32_t PORTAL_LINGER_MS = 30000;
// ----------------------------------------------------------------------------

Arduino_DataBus* bus = new Arduino_ESP32SPI(PIN_DC, PIN_CS, PIN_SCLK, PIN_MOSI, GFX_NOT_DEFINED);
Arduino_GFX* gfx = new Arduino_GC9A01(bus, GFX_NOT_DEFINED /* RST */, ROTATION, true /* IPS */);
WebServer server(80);
DNSServer dns;
Preferences prefs;

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

// Wi-Fi. Saved credentials live in NVS namespace "wifi" (keys ssid, pass);
// an empty g_ssid means none saved.
String   g_ssid, g_pass;
bool     g_wasConnected = false;
uint32_t g_connectedAt = 0;      // when STA last came up
uint32_t g_downSince = 0;        // when STA last went down (0 = boot)
uint32_t g_lastAttempt = 0;      // last WiFi.begin()
bool     g_portal = false;       // setup network (AP + captive DNS) running
uint32_t g_portalHitAt = 0;      // last web request while the portal is up
char     g_apName[24] = "";
char     g_apIp[16] = "";

// Web handlers must finish their reply before Wi-Fi is torn down, so they
// queue the change and loop() runs it a moment later.
enum class Pending : uint8_t { None, Connect, Forget };
Pending  g_pending = Pending::None;
uint32_t g_pendingAt = 0;

// ------------------------------------------------------------ /state (API) --

ui::Activity parseActivity(const String& v) {
  if (v == "working") return ui::Activity::Working;
  if (v == "waiting") return ui::Activity::Waiting;
  return ui::Activity::Idle;   // done / idle / unknown
}

const char* activityName(ui::Activity a) {
  switch (a) {
    case ui::Activity::Working: return "working";
    case ui::Activity::Waiting: return "waiting";
    default:                    return "idle";
  }
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

// ------------------------------------------------------------ Wi-Fi control --

void loadCredentials() {
  prefs.begin("wifi", true);
  g_ssid = prefs.getString("ssid", "");
  g_pass = prefs.getString("pass", "");
  prefs.end();
}

void saveCredentials(const String& ssid, const String& pass) {
  prefs.begin("wifi", false);
  prefs.putString("ssid", ssid);
  prefs.putString("pass", pass);
  prefs.end();
  g_ssid = ssid;
  g_pass = pass;
}

void clearCredentials() {
  prefs.begin("wifi", false);
  prefs.clear();
  prefs.end();
  g_ssid = "";
  g_pass = "";
}

void connectSta() {
  g_lastAttempt = millis();
  if (g_ssid.isEmpty()) return;
  Serial.printf("Connecting to WiFi \"%s\"\n", g_ssid.c_str());
  WiFi.disconnect();
  WiFi.begin(g_ssid.c_str(), g_pass.c_str());
}

void startPortal() {
  if (g_portal) return;
  WiFi.mode(WIFI_AP_STA);
  bool hasPass = strlen(AP_PASSWORD) >= 8;
  WiFi.softAP(g_apName, hasPass ? AP_PASSWORD : nullptr);
  delay(100);   // let the AP interface come up before asking for its IP
  strncpy(g_apIp, WiFi.softAPIP().toString().c_str(), sizeof g_apIp - 1);
  // Answer every DNS name with our own IP: that's what makes phones and
  // laptops pop up the setup page by themselves.
  dns.setErrorReplyCode(DNSReplyCode::NoError);
  dns.start(53, "*", WiFi.softAPIP());
  g_portal = true;
  g_portalHitAt = 0;
  WiFi.scanNetworks(true);   // have a network list ready for the setup page
  Serial.printf("Setup network \"%s\" is up: http://%s/\n", g_apName, g_apIp);
}

void stopPortal() {
  if (!g_portal) return;
  dns.stop();
  WiFi.softAPdisconnect(true);
  WiFi.mode(WIFI_STA);
  g_portal = false;
  Serial.println("Setup network closed.");
}

// Keeps WiFi up without blocking the animation, opens/closes the setup
// network, and sets the status line.
void tickWiFi() {
  uint32_t now = millis();

  if (g_pending != Pending::None && now - g_pendingAt > 500) {
    Pending p = g_pending;
    g_pending = Pending::None;
    if (p == Pending::Forget) {
      Serial.println("WiFi settings forgotten.");
      WiFi.disconnect();
      startPortal();
    } else {
      connectSta();
    }
    g_downSince = now;   // give the new settings a full PORTAL_AFTER_MS
  }

  bool up = WiFi.status() == WL_CONNECTED;
  if (up && !g_wasConnected) {
    g_wasConnected = true;
    g_connectedAt = now;
    Serial.print("WiFi connected. IP address: ");
    Serial.println(WiFi.localIP());
    Serial.println("Enter this IP in the tray app's \"Set ESP32 address…\".");
    MDNS.end();
    if (MDNS.begin(MDNS_HOSTNAME)) {
      MDNS.addService("http", "tcp", 80);
      Serial.printf("mDNS: http://%s.local/\n", MDNS_HOSTNAME);
    }
  } else if (!up) {
    if (g_wasConnected) {
      Serial.println("WiFi lost, reconnecting…");
      g_wasConnected = false;
      g_downSince = now;
    }
    bool haveCreds = !g_ssid.isEmpty();
    if (!g_portal && (!haveCreds || now - g_downSince > PORTAL_AFTER_MS)) startPortal();

    // A connect attempt makes the radio hop channels, which knocks phones off
    // the setup network. Hold off while someone is using the setup page.
    bool someoneSettingUp = g_portal && WiFi.softAPgetStationNum() > 0 &&
                            g_portalHitAt && now - g_portalHitAt < 180000;
    if (haveCreds && !someoneSettingUp && now - g_lastAttempt > RETRY_MS) connectSta();
  }

  if (up && g_portal && now - g_connectedAt > PORTAL_LINGER_MS) stopPortal();

  if (up && now - g_connectedAt < SHOW_IP_MS) {
    strncpy(g_status, WiFi.localIP().toString().c_str(), sizeof g_status - 1);
  } else if (up || g_ssid.isEmpty()) {
    g_status[0] = 0;
  } else {
    strcpy(g_status, "WIFI...");
  }
}

// --------------------------------------------------------------- web pages --

String htmlEscape(const String& s) {
  String o;
  o.reserve(s.length() + 8);
  for (char c : s) {
    switch (c) {
      case '&':  o += "&amp;";  break;
      case '<':  o += "&lt;";   break;
      case '>':  o += "&gt;";   break;
      case '"':  o += "&quot;"; break;
      case '\'': o += "&#39;";  break;
      default:   o += c;
    }
  }
  return o;
}

String jsonEscape(const String& s) {
  String o;
  o.reserve(s.length() + 8);
  for (char c : s) {
    if (c == '"' || c == '\\') { o += '\\'; o += c; }
    else if ((uint8_t)c < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", c); o += b; }
    else o += c;
  }
  return o;
}

const char PAGE_HEAD[] PROGMEM = R"HTML(<!DOCTYPE html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Claude Code Indicator</title>
<style>
:root{--bg:#141414;--card:#1f1f1f;--fg:#eee;--dim:#999;--accent:#D37355;--line:#333}
*{box-sizing:border-box}
body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:16px/1.45 system-ui,sans-serif}
main{max-width:480px;margin:0 auto}
h1{font-size:20px;margin:4px 0 16px}
h2{font-size:15px;margin:0 0 10px;color:var(--dim);font-weight:600;text-transform:uppercase;letter-spacing:.04em}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin-bottom:14px}
dl{display:grid;grid-template-columns:auto 1fr;gap:4px 14px;margin:0}
dt{color:var(--dim)}dd{margin:0;overflow-wrap:anywhere}
label{display:block;margin:10px 0 4px;color:var(--dim);font-size:14px}
input{width:100%;padding:10px;border-radius:8px;border:1px solid var(--line);background:#111;color:var(--fg);font-size:16px}
button{margin-top:14px;width:100%;padding:11px;border:0;border-radius:8px;background:var(--accent);color:#fff;font-size:16px;font-weight:600;cursor:pointer}
button.ghost{background:transparent;border:1px solid var(--line);color:var(--fg)}
button.danger{background:transparent;border:1px solid #E53935;color:#ff8a80}
#nets{list-style:none;margin:8px 0 0;padding:0;max-height:220px;overflow:auto}
#nets li{display:flex;justify-content:space-between;padding:8px 10px;border-radius:6px;cursor:pointer}
#nets li:hover{background:#2a2a2a}
#nets span{color:var(--dim);font-size:13px}
.note{color:var(--dim);font-size:13px;margin:8px 0 0}
code{font-size:12px;overflow-wrap:anywhere}
.ok{color:#43A047}.bad{color:#ff8a80}
</style></head><body><main>
)HTML";

const char PAGE_SCRIPT[] PROGMEM = R"HTML(<script>
const list=document.getElementById('nets'),ssid=document.getElementById('ssid'),pw=document.getElementById('pass');
function bars(r){return r>-55?'▂▄▆█':r>-67?'▂▄▆':r>-78?'▂▄':'▂'}
function scan(refresh){
  fetch('/scan'+(refresh?'?refresh=1':'')).then(r=>r.json()).then(d=>{
    if(d.scanning){list.innerHTML='<li><span>Scanning…</span></li>';setTimeout(()=>scan(false),1500);return}
    list.innerHTML='';
    if(!d.networks.length){list.innerHTML='<li><span>No networks found</span></li>';return}
    d.networks.forEach(n=>{
      const li=document.createElement('li');
      const a=document.createElement('b');a.textContent=n.ssid;a.style.fontWeight='normal';
      const s=document.createElement('span');s.textContent=(n.secure?'🔒 ':'')+bars(n.rssi);
      li.append(a,s);li.onclick=()=>{ssid.value=n.ssid;pw.value='';pw.focus()};
      list.append(li);
    });
  }).catch(()=>{list.innerHTML='<li><span>Scan failed</span></li>'});
}
scan(false);
</script>)HTML";

void sendPage(const String& body) {
  String html;
  html.reserve(strlen_P(PAGE_HEAD) + body.length() + 32);
  html += FPSTR(PAGE_HEAD);
  html += body;
  html += "</main></body></html>";
  server.send(200, "text/html; charset=utf-8", html);
}

void handleRoot() {
  bool up = WiFi.status() == WL_CONNECTED;
  bool saved = !g_ssid.isEmpty();
  String b;
  b.reserve(3500);
  b += "<h1>Claude Code Indicator</h1>";

  b += "<section class='card'><h2>Wi-Fi</h2><dl>";
  if (up) {
    b += "<dt>Status</dt><dd class='ok'>Connected</dd>";
    b += "<dt>Network</dt><dd>" + htmlEscape(WiFi.SSID()) + "</dd>";
    b += "<dt>Address</dt><dd>" + WiFi.localIP().toString() + " &middot; " + MDNS_HOSTNAME + ".local</dd>";
    b += "<dt>Signal</dt><dd>" + String(WiFi.RSSI()) + " dBm</dd>";
  } else if (saved) {
    b += "<dt>Status</dt><dd class='bad'>Can't reach the saved network</dd>";
    b += "<dt>Saved</dt><dd>" + htmlEscape(g_ssid) + "</dd>";
  } else {
    b += "<dt>Status</dt><dd class='bad'>Not set up</dd>";
  }
  if (g_portal) b += "<dt>Setup network</dt><dd>" + htmlEscape(g_apName) + "</dd>";
  b += "</dl>";
  if (up)
    b += "<p class='note'>Enter <b>" + WiFi.localIP().toString() +
         "</b> in the tray app under <i>Set ESP32 address…</i></p>";
  b += "</section>";

  b += "<section class='card'><h2>" + String(saved ? "Change network" : "Connect to Wi-Fi") + "</h2>";
  b += "<form method='POST' action='/wifi' autocomplete='off'>";
  b += "<label for='ssid'>Network name</label>";
  b += "<input id='ssid' name='ssid' maxlength='32' required autocapitalize='none' value='" + htmlEscape(g_ssid) + "'>";
  b += "<ul id='nets'></ul>";
  b += "<button type='button' class='ghost' onclick='scan(true)'>Scan again</button>";
  b += "<label for='pass'>Password</label>";
  b += "<input id='pass' name='pass' type='password' maxlength='63' placeholder='";
  b += saved ? "Leave empty to keep the saved one" : "Empty for an open network";
  b += "'>";
  b += "<p class='note'>2.4 GHz networks only. Saving restarts the board's Wi-Fi.</p>";
  b += "<button type='submit'>Save and connect</button></form></section>";

  if (saved) {
    b += "<section class='card'><h2>Forget network</h2>";
    b += "<p class='note' style='margin-top:0'>Erases the saved network. The board then opens its setup network <b>" +
         htmlEscape(g_apName) + "</b> again.</p>";
    b += "<form method='POST' action='/forget' onsubmit=\"return confirm('Forget the saved Wi-Fi network?')\">";
    b += "<button class='danger' type='submit'>Forget Wi-Fi settings</button></form></section>";
  }

  b += "<section class='card'><h2>Indicator</h2><dl>";
  b += "<dt>Claude</dt><dd>" + String(activityName(g_claude));
  if (g_limit > 0) b += " &middot; $" + String(g_limit - g_spent, 2) + " of $" + String(g_limit, 2) + " left";
  b += "</dd><dt>Codex</dt><dd>" + String(activityName(g_codex));
  if (g_codexUsed >= 0) b += " &middot; " + String(100 - g_codexUsed, 0) + "% left";
  b += "</dd></dl><p class='note'>Tray app API: <code>/state?value=&lt;working|waiting|done&gt;&amp;rgb=&lt;hex&gt;"
       "[&amp;spent=&lt;usd&gt;&amp;limit=&lt;usd&gt;][&amp;codex=&lt;working|waiting|done&gt;]"
       "[&amp;cused=&lt;percent&gt;]</code></p></section>";

  b += FPSTR(PAGE_SCRIPT);
  sendPage(b);
}

void handleSave() {
  String ssid = server.arg("ssid");
  String pass = server.arg("pass");
  ssid.trim();
  if (ssid.isEmpty() || ssid.length() > 32) {
    sendPage("<h1>Not saved</h1><p class='bad'>The network name must be 1–32 characters.</p><p><a href='/'>Back</a></p>");
    return;
  }
  // Empty password + same network = keep the saved password.
  if (pass.isEmpty() && ssid == g_ssid) pass = g_pass;
  if (!pass.isEmpty() && (pass.length() < 8 || pass.length() > 63)) {
    sendPage("<h1>Not saved</h1><p class='bad'>A Wi-Fi password is 8–63 characters.</p><p><a href='/'>Back</a></p>");
    return;
  }
  saveCredentials(ssid, pass);
  g_pending = Pending::Connect;
  g_pendingAt = millis();

  String b = "<h1>Connecting…</h1><section class='card'>";
  b += "<p>Saved. The board is now joining <b>" + htmlEscape(ssid) + "</b>.</p>";
  b += "<p id='st' class='note'>Waiting for the board…</p>";
  b += "<p class='note'>Your device may drop off this network while the board switches. "
       "The new address also shows at the bottom of the round screen.</p>";
  b += "<p><a href='/'>Back to settings</a></p></section>";
  b += "<script>let n=0;const st=document.getElementById('st');"
       "function poll(){fetch('/status').then(r=>r.json()).then(d=>{"
       "if(d.connected){st.className='ok';st.innerHTML='Connected. Address: <b>'+d.ip+'</b> &mdash; enter it in the tray app.';return}"
       "if(++n>15){st.className='bad';st.textContent='Not connected yet. Check the password; the board keeps trying and reopens its setup network if it can\\'t join.';}"
       "setTimeout(poll,2000)}).catch(()=>setTimeout(poll,2000))}setTimeout(poll,3000);</script>";
  sendPage(b);
}

void handleForget() {
  clearCredentials();
  g_pending = Pending::Forget;
  g_pendingAt = millis();
  sendPage("<h1>Wi-Fi forgotten</h1><section class='card'><p>The board is leaving the network and opening "
           "its setup network <b>" + htmlEscape(g_apName) + "</b>. Join it to set up Wi-Fi again.</p></section>");
}

void handleStatus() {
  bool up = WiFi.status() == WL_CONNECTED;
  String j = "{\"connected\":";
  j += up ? "true" : "false";
  j += ",\"ssid\":\"" + jsonEscape(up ? WiFi.SSID() : g_ssid) + "\"";
  j += ",\"ip\":\"" + (up ? WiFi.localIP().toString() : String()) + "\"";
  j += ",\"portal\":";
  j += g_portal ? "true" : "false";
  j += "}";
  server.send(200, "application/json", j);
}

// Network list for the setup page. Scans run in the background; the page
// polls until the result is in. ?refresh=1 starts a new scan.
void handleScan() {
  int n = WiFi.scanComplete();
  if (n == WIFI_SCAN_RUNNING) {
    server.send(200, "application/json", "{\"scanning\":true}");
    return;
  }
  if (n < 0 || server.hasArg("refresh")) {
    WiFi.scanDelete();
    WiFi.scanNetworks(true);
    server.send(200, "application/json", "{\"scanning\":true}");
    return;
  }
  // Strongest first, one entry per name, hidden networks left out.
  String j = "{\"scanning\":false,\"networks\":[";
  bool used[64] = {false};
  int count = n < 64 ? n : 64, listed = 0;
  for (int k = 0; k < count; k++) {
    int best = -1;
    for (int i = 0; i < count; i++)
      if (!used[i] && (best < 0 || WiFi.RSSI(i) > WiFi.RSSI(best))) best = i;
    used[best] = true;
    String name = WiFi.SSID(best);
    if (name.isEmpty()) continue;
    bool dup = false;
    for (int i = 0; i < count && !dup; i++)
      if (used[i] && i != best && WiFi.SSID(i) == name && WiFi.RSSI(i) >= WiFi.RSSI(best)) dup = true;
    if (dup) continue;
    if (listed++) j += ',';
    j += "{\"ssid\":\"" + jsonEscape(name) + "\",\"rssi\":" + String(WiFi.RSSI(best)) +
         ",\"secure\":" + (WiFi.encryptionType(best) == WIFI_AUTH_OPEN ? "false" : "true") + "}";
  }
  j += "]}";
  server.send(200, "application/json", j);
}

// Everything else. On the setup network this is how the captive portal
// works: the OS's connectivity checks (generate_204, hotspot-detect.html,
// connecttest.txt, …) get redirected to the setup page, so the OS shows it.
void handleNotFound() {
  if (g_portal) {
    server.sendHeader("Location", String("http://") + g_apIp + "/", true);
    server.send(302, "text/plain", "");
    return;
  }
  server.send(404, "text/plain", "not found");
}

// Wraps a handler so requests on the setup network count as "someone is
// setting up" (see tickWiFi).
template <void (*H)()>
void tracked() {
  if (g_portal) g_portalHitAt = millis();
  H();
}

// ----------------------------------------------------------------- drawing --

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

void setup() {
  Serial.begin(115200);
  delay(200);

  if (!gfx->begin(80000000)) Serial.println("gfx->begin() failed");
  gfx->fillScreen(0x0000);
  pinMode(PIN_BL, OUTPUT);
  analogWrite(PIN_BL, BRIGHTNESS);

  // Setup network name: CLAUDE-XXXX from the last two MAC bytes. Upper case
  // so it matches what the screen's font shows.
  WiFi.persistent(false);   // credentials live in our own NVS namespace
  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);
  uint8_t mac[6];
  WiFi.macAddress(mac);
  snprintf(g_apName, sizeof g_apName, "%s-%02X%02X", AP_PREFIX, mac[4], mac[5]);

  loadCredentials();
  if (g_ssid.isEmpty()) {
    Serial.println("No WiFi saved.");
    startPortal();
  } else {
    connectSta();
  }

  server.on("/", tracked<handleRoot>);
  server.on("/wifi", HTTP_POST, tracked<handleSave>);
  server.on("/forget", HTTP_POST, tracked<handleForget>);
  server.on("/status", tracked<handleStatus>);
  server.on("/scan", tracked<handleScan>);
  server.on("/state", handleState);
  server.onNotFound(tracked<handleNotFound>);
  server.begin();
}

void loop() {
  if (g_portal) dns.processNextRequest();
  server.handleClient();
  tickWiFi();
  drawStatic();

  static uint32_t lastFrame = 0;
  uint32_t now = millis();
  if (now - lastFrame >= FRAME_MS) {
    lastFrame = now;
    ui::Canvas cv{ui::STAGE_X, ui::STAGE_Y, ui::STAGE_W, ui::STAGE_H, stageBuf};
    // The setup screen shows until the board is on Wi-Fi (the setup network
    // may linger a little after that; the avatars come back right away).
    if (g_portal && WiFi.status() != WL_CONNECTED)
      ui::drawSetup(cv, g_apName, strlen(AP_PASSWORD) >= 8 ? AP_PASSWORD : "", g_apIp, now);
    else
      ui::drawStage(cv, g_claude, g_codex, now, g_waitColor);
    cv.push(sink);
  }
}
