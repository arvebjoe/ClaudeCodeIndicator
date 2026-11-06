# Uploading the firmware to the round display

How to flash `ClaudeCodeIndicatorRound` onto the **ESP32-2424S012**, the ESP32-C3 board with a
1.28" round screen, and connect it to the tray app. This takes about 15 minutes the first
time.

## What you need

- The board and a **USB-C data cable**. Charge-only cables are common and make the board
  invisible to the PC.
- A Windows PC with the [Arduino IDE 2.x](https://www.arduino.cc/en/software).
- Your Wi-Fi name and password. The ESP32-C3 only supports **2.4 GHz** Wi-Fi.

The demo zip linked from the AliExpress listing (`3.2inch_ESP32-2432S032.zip`) is for a
different board. You don't need it.

## 1. One-time Arduino IDE setup

1. **Add ESP32 board support.**
   1. Open **File → Preferences**.
   2. Paste this URL into *Additional boards manager URLs*:
      ```
      https://espressif.github.io/arduino-esp32/package_esp32_index.json
      ```
   3. Open **Tools → Board → Boards Manager**, search **esp32**, and install
      **esp32 by Espressif Systems**.
2. **Install the display library.** Open **Tools → Manage Libraries…**, search
   **GFX Library for Arduino**, and install the one by **moononournation**.

## 2. Get the sketch ready

1. Open `firmware/ClaudeCodeIndicatorRound/ClaudeCodeIndicatorRound.ino` in the Arduino IDE.
   `render.h` should open as a second tab. Both files must stay in this folder, and the
   folder name must match the `.ino` name.
2. Copy `secrets.example.h` to `secrets.h` in the same folder and fill it in:
   ```c
   #define WIFI_SSID_VALUE "MyNetwork"
   #define WIFI_PASS_VALUE "my-password"
   ```
   `secrets.h` is gitignored, so your password is never committed.

## 3. Board settings

Plug in the board, then set the **Tools** menu as follows:

| Setting | Value |
|---|---|
| Board | **ESP32C3 Dev Module** |
| Port | the new COM port that appeared when you plugged the board in |
| USB CDC On Boot | **Enabled** (see the note below) |
| Flash Size | **4MB (32Mb)** |
| Partition Scheme | Default 4MB with spiffs |
| Upload Speed | 921600 (drop to 115200 if uploads fail) |

**No COM port?**
- Try another cable. Most "no port" problems are a charge-only cable.
- Open Device Manager. If you see an unknown device or "USB-SERIAL CH340", install the
  **CH340 driver** from WCH. Some versions of this board have a CH340 USB chip; others use
  the C3's built-in USB, which needs no driver.

## 4. Upload

1. Click **Upload** (the → button). The sketch compiles, which takes a minute the first time,
   and then flashes the board.
2. If the upload stops at `Connecting......` or says *No serial data received*, put the
   board into download mode by hand:
   1. Hold the **BOOT** button.
   2. While holding it, tap **RST**, or unplug and replug the USB cable.
   3. Release BOOT and click **Upload** again.
   4. After a manual-boot upload, press **RST** (or replug) to start the new firmware.

## 5. First start

1. The screen goes black, then the two rings and the two avatars appear. The bottom line
   reads `WIFI...`.
2. When Wi-Fi connects, the board's **IP address** shows at the bottom for one minute. The
   IP is also printed in **Tools → Serial Monitor**, at **115200** baud.
3. In the tray app, right-click the icon, choose **Set ESP32 address…**, and enter that IP.
   You can also enter `claude-indicator.local` if your PC resolves mDNS names.
4. Check that it works:
   - Send Claude Code a prompt. Clawd should start walking.
   - When Claude finishes, Clawd goes back to idle. The orange ring fills in after the tray
     app's next usage read, which happens 10 s after the tray app starts and then every
     10 min.

To make the IP easy to find later, reserve it in your router's DHCP settings so it stays the
same.

## 6. Adjusting to taste

The settings are at the top of `ClaudeCodeIndicatorRound.ino`, in the `CONFIG` section.
Upload again after changing them.

| Constant | What it does |
|---|---|
| `ROTATION` | 0–3 quarter turns. Use it to put the USB-C port where you want it. |
| `BRIGHTNESS` | Backlight level, 0–255. 120 is the default; 255 is glaring. |
| `SHOW_IP_MS` | How long the IP stays on screen after connecting. |
| `PIN_*` | Display wiring. Only change these if the screen stays dark (see below). |

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| Upload works but the screen stays black | The backlight or pins differ on your board. Check `PIN_BL` (3) first, then SCLK 6, MOSI 7, DC 2 and CS 10 against the board's schematic. |
| Colours look inverted (black background shows white) | Change the last argument of `new Arduino_GC9A01(...)` from `true` to `false`. |
| Image is rotated | Change `ROTATION`. |
| Serial Monitor shows nothing | Flip **USB CDC On Boot**. Boards with a CH340 chip need it **Disabled**; boards using the C3's own USB need it **Enabled**. Then upload again. |
| Stuck on `WIFI...` | Check the name and password in `secrets.h`, and that the network is 2.4 GHz. |
| Display works, but the avatars never change | Check the IP in the tray app. Opening `http://<ip>/` in a browser should show a status page. |
| Rings stay dim | No usage reading yet, or usage tracking is off in the tray menu. The Codex ring needs Codex installed and signed in with ChatGPT. |
| The Codex avatar is always idle | Codex hooks aren't set up. See [README.md](README.md#codex-state-optional-codex-hooks). |

## Updating later

Pull the latest code, open the `.ino`, and click **Upload**. Your `secrets.h` stays in place,
and none of the other steps need repeating.
