---
paths:
  - "Directory.Build.props"
  - "build.ps1"
  - "release.ps1"
  - ".github/workflows/release.yml"
---

# Versioning

- **The version lives in `Directory.Build.props` only** (issue #46). Both projects inherit it and
  `build.ps1` defaults to it, so there is no second number to keep in step. The shell extension sat
  at 0.1.0 for five releases because it had one. `release.yml` fails a tag that disagrees with the
  props file, since a lagging version makes a build from source see the newer tag and show a
  permanent "update available" pill (issue #33). Bump it with `./release.ps1` (see "Cutting a release"): its tag
  step refuses until `main`'s props file carries the version, so the two can't drift apart. Note the declared version
  never reaches a *released* binary: `build.ps1` passes `-p:Version` from the tag, which overrides
  it. It only affects local builds.
