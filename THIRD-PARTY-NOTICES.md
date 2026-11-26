# Third-party notices

GRB Animation Tool is licensed GPL-3.0-or-later (see [LICENSE](LICENSE)), with an additional permission under section 7 for the Oodle
library of the user's game install (see [LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md)). This file lists the third-party parts it uses
and their licences.

## 1. The program

The core library (`GrbAnimTool.Core`), the viewer (`GrbAnimTool`, a WPF program) and the command-line harness (`grbanim-cli`) have no
package references. Apart from .NET itself they contain no third-party code. The viewer draws its 3D view with WPF's own Viewport3D.
The LZO1X decoder that reads the game's graph documents is the project's own, written from a description of the format, not from any
LZO library's code.

| Part | Licence | Note |
|---|---|---|
| .NET 10 runtime and base class library | MIT | Shipped unchanged in the release zip |
| .NET 10 Windows Desktop runtime (WPF) | MIT | Shipped unchanged in the release zip. Used by the viewer, with WPF's built-in Fluent theme for its light and dark look |
| Microsoft's native parts inside those runtimes (`D3DCompiler_47_cor3.dll`, `vcruntime140_cor3.dll`, `PresentationNative_cor3.dll`, `PenImc_cor3.dll`, `wpfgfx_cor3.dll`, `Microsoft.DiaSymReader.Native.amd64.dll`, `msquic.dll`) | As part of the runtime packs, published by Microsoft under the MIT licence | Shipped unchanged as part of the .NET and Windows Desktop runtime packs, whose licence files are in the zip's `third-party-licenses` folder; `msquic` is also named in the .NET runtime's third-party notices there |

The release zip holds the .NET 10 runtime and the Windows Desktop runtime as Microsoft publishes them, so the program runs without a
.NET install. None of their files is changed. The zip's `third-party-licenses` folder holds a `README.txt` saying what it is, and three
files taken from the runtime packs:

- `dotnet-runtime-LICENSE.txt`: the MIT licence of the .NET runtime pack;
- `THIRD-PARTY-NOTICES.TXT`: the .NET runtime pack's notices for the third-party parts it holds;
- `dotnet-windowsdesktop-LICENSE.txt`: the MIT licence of the Windows Desktop runtime pack (that pack ships no notices file).

The licence of both runtimes is:

    The MIT License (MIT)

    Copyright (c) .NET Foundation and Contributors

    All rights reserved.

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

## 2. Test-only packages

These are used to build and run the tests in `tests/`. None of them is part of the program.

| Package | Version | Licence |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | Apache-2.0 |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.assert | 2.9.3 | Apache-2.0 |
| xunit.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.CodeCoverage | 17.14.1 | MIT |
| Microsoft.TestPlatform.ObjectModel | 17.14.1 | MIT |
| Microsoft.TestPlatform.TestHost | 17.14.1 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |

## 3. Not included: Oodle

The game's data is compressed with Oodle, the compression library of RAD Game Tools (Epic Games). **Oodle is not included with GRB Animation
Tool and is never distributed with it.** The tool loads the copy that comes with your own installation of the game
(`oo2core_7_win64.dll` in the game folder) at run time, and only after checking that its SHA-256 is one the tool knows. The tool only
decompresses with it. Loading it is covered by the additional permission in [LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md). Oodle is
a trademark of its owner.

## 4. Game data

GRB Animation Tool contains none of the game's files or assets. It reads your own installation of the game, read-only. Built into it are a
table of clip IDs with the SHA-256 of each clip record, so the tool can tell which clips of an install are the game's own, and the
tool's own clip index (clip IDs, parts of clip names and the tool's own descriptions).

## 5. Trademarks

Tom Clancy's Ghost Recon Breakpoint is a trademark of Ubisoft Entertainment. GRB Animation Tool is an unofficial fan-made tool: it is not
made, endorsed or supported by Ubisoft.
