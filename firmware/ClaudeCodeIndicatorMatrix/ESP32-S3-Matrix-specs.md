# ESP32-S3-Matrix — Technical Specifications

Product: ESP32-S3 Matrix 8x8 RGB LED board with QMI8658C attitude sensor (SpotPear, SKU 0601045, Part No. ESP32-S3-Matrix)

## Processor & Memory

| Spec | Value |
|---|---|
| Chip | ESP32-S3FH4R2 |
| CPU | Xtensa 32-bit LX7 dual-core, up to 240 MHz |
| SRAM | 512 KB |
| ROM | 384 KB |
| PSRAM | 2 MB |
| Flash | 4 MB |

## Wireless

| Spec | Value |
|---|---|
| Wi-Fi | 2.4 GHz, 802.11 b/g/n, 40 MHz bandwidth support |
| Bluetooth | Bluetooth 5 (LE) + Bluetooth Mesh |
| Low power | Multiple low-power operating states; adjustable balance between communication distance, data rate, and power consumption |

## Onboard Hardware

| Component | Details |
|---|---|
| LED matrix | 8×8 RGB LED matrix |
| Matrix expansion | Dout pin for chaining additional RGB matrices |
| Attitude sensor | QMI8658C (QST) — 3-axis accelerometer + 3-axis gyroscope |
| USB | USB Type-C connector |
| Voltage regulator | ME6217C33M5G low-dropout LDO, 800 mA max |
| Buttons | BOOT (hold during reset to enter download mode), RESET |

## I/O

- 17 × multi-function GPIO pins with configurable pin functions
- Rich peripheral interfaces

## Software Support

- ESP-IDF
- Arduino
- MicroPython

## Package Contents

- ESP32-S3-Matrix board × 1
