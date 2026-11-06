// Renders the round display on a PC, using the exact drawing code the
// firmware runs (../render.h), so layout and animations can be checked
// without hardware.
//
//   g++ -O2 -std=c++17 -o preview preview.cpp
//   ./preview <out-dir>
//
// Writes <out-dir>/frame_<state>_<nnn>.ppm — 40 frames at 50 ms for each
// state (idle, working, waiting). Turn them into GIFs/PNGs with any tool,
// e.g. ImageMagick:  magick -delay 5 frame_working_*.ppm working.gif
#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>
#include "../render.h"

struct FrameSink : ui::Sink {
  std::vector<uint16_t> fb = std::vector<uint16_t>(ui::SCREEN * ui::SCREEN, 0);
  void blit(int x, int y, const uint16_t* buf, int w, int h) override {
    for (int r = 0; r < h; r++)
      for (int c = 0; c < w; c++) {
        int X = x + c, Y = y + r;
        if (X >= 0 && Y >= 0 && X < ui::SCREEN && Y < ui::SCREEN) fb[Y * ui::SCREEN + X] = buf[r * w + c];
      }
  }
  void save(const std::string& path) const {
    FILE* f = fopen(path.c_str(), "wb");
    fprintf(f, "P6\n%d %d\n255\n", ui::SCREEN, ui::SCREEN);
    for (int y = 0; y < ui::SCREEN; y++)
      for (int x = 0; x < ui::SCREEN; x++) {
        // outside the round glass is invisible on the real panel
        float dx = x + 0.5f - 120, dy = y + 0.5f - 120;
        uint16_t p = (dx * dx + dy * dy > 120.0f * 120.0f) ? 0x2104 : fb[y * ui::SCREEN + x];
        unsigned char rgb[3] = { (unsigned char)((p >> 11) << 3), (unsigned char)(((p >> 5) & 63) << 2),
                                 (unsigned char)((p & 31) << 3) };
        fwrite(rgb, 1, 3, f);
      }
    fclose(f);
  }
};

int main(int argc, char** argv) {
  std::string dir = argc > 1 ? argv[1] : ".";
  static uint16_t strip[ui::SCREEN * 20];
  static uint16_t scratch[ui::STATUS_W * ui::LABEL_H];
  static uint16_t stage[ui::STAGE_W * ui::STAGE_H];

  struct Case { const char* name; ui::Activity claude, codex; };
  const Case cases[] = {
    {"idle",    ui::Activity::Idle,    ui::Activity::Idle},
    {"working", ui::Activity::Working, ui::Activity::Working},
    {"waiting", ui::Activity::Waiting, ui::Activity::Waiting},
  };
  for (const Case& k : cases) {
    FrameSink s;
    // sample usage: $221 of $800 spent, Codex 36% used
    ui::drawRings(s, strip, 20, ui::claudeLeft(221, 800), ui::codexLeft(36));
    ui::drawLabels(s, scratch, 221, 800, 36);
    ui::drawStatus(s, scratch, "192.168.0.123");
    for (int i = 0; i < 40; i++) {
      ui::Canvas cv{ui::STAGE_X, ui::STAGE_Y, ui::STAGE_W, ui::STAGE_H, stage};
      ui::drawStage(cv, k.claude, k.codex, 1000 + i * 50, ui::WAIT_DEFAULT);
      cv.push(s);
      char name[256];
      snprintf(name, sizeof name, "%s/frame_%s_%03d.ppm", dir.c_str(), k.name, i);
      s.save(name);
    }
  }
  return 0;
}
