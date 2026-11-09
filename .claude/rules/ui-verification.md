---
paths:
  - "**/*.xaml"
  - "**/*.xaml.cs"
---

# Driving the app, when Jacob asks

Only when Jacob asks for a visual check (see Verify a change in `CLAUDE.md`).

Launch the exe with a folder containing relevant files, confirm it stays up (no crash / no error
dialog), then capture it with `powershell -ExecutionPolicy Bypass -File tools/Capture-Window.ps1`,
which writes `%TEMP%\eh_shot.png` (`-Out` picks another path). **Find the window rect
dynamically**: the window gets moved between monitors and can sit at negative coordinates, so never
hardcode positions or assume the primary screen. The script captures the whole virtual desktop and
crops to the window's `GetWindowRect`.

Notes:
- The file `ListView` is virtualized and **does not expose rows to UI Automation**, so select a file
  with a real mouse click into the list + arrow keys (`SendKeys`) rather than `SelectionItemPattern`.
- The details strip and video timeline only appear when a file is selected, so select one first.
- `ffmpeg` is available for generating test media, e.g.
  `ffmpeg -f lavfi -i testsrc=size=1280x720:rate=30 -t 3 -pix_fmt yuv420p test.mp4`.
- To unit-test a `Services/*` interop file in isolation, a throwaway console csproj that
  `<Compile Include>`s the single file (absolute path) is faster than launching the whole app.
