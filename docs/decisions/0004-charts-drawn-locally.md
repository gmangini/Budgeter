# 0004: Draw charts on the machine with ScottPlot

Date: 2026-10-29

## Decision
`chart` and `daily chart` images are drawn by the app itself with ScottPlot (SkiaSharp underneath) and sent to
Telegram as photos.

## Why
- Hosted chart services (QuickChart and similar) would mean sending card spending to another company.
- ScottPlot ships Linux native libraries with the self-contained build, so the server needs only a font:
  `install.sh` adds `fonts-dejavu-core`, and `Charts` loads DejaVu Sans directly when it's there.

## Consequences
- The published bundle is bigger (SkiaSharp and HarfBuzz native libraries).
- Without the font file on Linux, chart text may not render; the deploy script installs it.
