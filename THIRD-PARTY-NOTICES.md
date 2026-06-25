# Third-Party Notices

ChessKit's own source code is licensed under the MIT License (see [`LICENSE`](LICENSE)).
The application and its Windows download additionally include third-party
components that remain under **their own licenses**, listed below. Full license
texts are in the [`LICENSES/`](LICENSES) directory.

---

## Bundled assets and engine (copyleft — please read)

### Chess piece images — "cburnett"

- Files: `Assets/AnalysisBoardPieces/*.png` (embedded as resources in `ChessKit.exe`).
- Author: **Colin M.L. Burnett**.
- License: **GNU General Public License, version 2 or later (GPL-2.0-or-later)** — see [`LICENSES/GPL-2.0.txt`](LICENSES/GPL-2.0.txt).
- Obtained from the lichess project: <https://github.com/lichess-org/lila> (`public/piece/cburnett`).
- **Corresponding source** (the preferred form for making modifications) is the
  original SVGs, included in this repository at
  [`Assets/AnalysisBoardPieces/source/`](Assets/AnalysisBoardPieces/source).

### Stockfish 17.1 (chess engine)

- File: `engines/stockfish-windows-x86-64-avx2.exe` — included in the **Windows
  download only**; not in this source repository.
- Copyright: © the Stockfish developers (see the project's AUTHORS file).
- License: **GNU General Public License, version 3 or later (GPL-3.0-or-later)** — see [`LICENSES/GPL-3.0.txt`](LICENSES/GPL-3.0.txt).
- **Complete corresponding source** for this version is available at
  <https://github.com/official-stockfish/Stockfish> (release tag `sf_17.1`).
  **Written offer:** for at least three (3) years from the date you received
  this distribution, the identical complete corresponding source is available
  on request and at the URL above.

> ChessKit invokes Stockfish only as a **separate executable over the UCI
> protocol** — it is not statically or dynamically linked into the application.
> The piece images are **data assets**, not linked code. ChessKit's own MIT
> code and these GPL-licensed components are *aggregated* on the same medium;
> neither is a derivative work of the other.

---

## Bundled libraries (linked into `ChessKit.exe`)

### OpenCvSharp4, OpenCvSharp4.Extensions, OpenCvSharp4.runtime.win

- Copyright © 2008–2020 shimat. The `runtime.win` package also redistributes
  **OpenCV** native binaries (© the OpenCV team).
- License: **Apache License 2.0** — see [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt).
- <https://github.com/shimat/opencvsharp>

### SharpDX, SharpDX.Direct3D11, SharpDX.DXGI

- Copyright © 2010–2016 Alexandre Mutel.
- License: **MIT** (text below).

### Gera.Chess

- Copyright © 2025 Sviatoslav Harasymchuk.
- License: **MIT** (text below).

### System.Management

- Copyright © Microsoft Corporation.
- License: **MIT** (text below).

The download is **framework-dependent**: it does not redistribute the .NET
runtime, which the user installs separately. .NET is © Microsoft Corporation,
MIT licensed.

---

## Data

### Opening names — `Assets/lichess_openings/*.tsv`

- From lichess-org/chess-openings: <https://github.com/lichess-org/chess-openings>
- License: **CC0 1.0 Universal** (public domain dedication).

---

## MIT License (covers the MIT-licensed components listed above)

Copyright (c) 2010–2016 Alexandre Mutel (SharpDX)
Copyright (c) 2025 Sviatoslav Harasymchuk (Gera.Chess)
Copyright (c) Microsoft Corporation (System.Management, .NET)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
