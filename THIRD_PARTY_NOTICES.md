# Third-party notices

The application uses the following direct, transitive, build-time, and self-contained runtime projects. Platform-specific native asset packages are grouped with their upstream project.

| Project | Version | License | Use |
| --- | --- | --- | --- |
| Avalonia UI and desktop packages | 12.1.0 | MIT | UI framework, themes, and platform backends |
| Avalonia BuildServices | 11.3.2 | MIT | Build tooling |
| ANGLE | 2.1.27548.20260419 | BSD 3-Clause | Windows graphics compatibility |
| Inter | bundled by Avalonia 12.1.0 | SIL Open Font License 1.1 | Application typeface |
| Lucide.Avalonia | 0.2.13 | MIT | Avalonia icon integration |
| Lucide Icons | bundled by Lucide.Avalonia | ISC | Icon artwork |
| ktsu.CredentialCache | 1.3.19 | MIT | Native credential storage |
| ktsu.RoundTripStringJsonConverter | 1.0.18 | MIT | Credential serialization |
| ktsu.Semantics.Strings | 2.5.4 | MIT | Typed credential values |
| Microsoft.Data.Sqlite | 10.0.10 | MIT | Managed SQLite access |
| SQLitePCLRaw | 3.0.4 | Apache 2.0 | Native SQLite integration |
| SQLite | 3.53.3 | Public Domain | Embedded item database engine |
| SkiaSharp and native assets | 3.119.4 | MIT | 2D rendering |
| HarfBuzzSharp and native assets | 8.3.1.3 | MIT | Text shaping |
| MicroCom.Runtime | 0.11.6 | MIT | Native interoperability |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Linux desktop integration |
| Polyfill | 10.11.2 | MIT | Build-time framework polyfills |
| Microsoft .NET runtime | 10 | MIT | Self-contained runtime |
| tsm-app-linux | compatibility reference | MIT | Clean-room protocol validation reference |

Project and license links are available from **Settings → Licenses** inside the application.

## License families

MIT-licensed components are distributed under the MIT License. The full MIT terms are reproduced below with the component-specific copyright statements supplied by each upstream package or repository remaining applicable.

ANGLE is distributed under the BSD 3-Clause License. See <https://github.com/AvaloniaUI/angle/blob/main/LICENSE>.

The Inter typeface is distributed under the SIL Open Font License 1.1. See <https://github.com/rsms/inter/blob/master/LICENSE.txt>.

Lucide icon artwork is distributed under the ISC License. See <https://github.com/lucide-icons/lucide/blob/main/LICENSE>.

SQLitePCLRaw is distributed under the Apache License 2.0. See <https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE>.

SQLite has been dedicated to the public domain. See <https://sqlite.org/copyright.html>.

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Wowhead item data

The optional Auction House fallback retrieves item tooltip metadata from Wowhead.
Item artwork is retrieved from Blizzard's World of Warcraft media CDN. These responses
are cached locally by World of Warcraft flavor. Wowhead and its item data are properties
of their respective owners; this project is not affiliated with or endorsed by Wowhead.
The Wowhead button uses the official 48px Wowhead mark downloaded from
`https://wow.zamimg.com/images/logos/favicon-live.png`.

The clean-room protocol validation and AppData compatibility work was informed by
`tsm-app-linux` (<https://github.com/exceptionptr/tsm-app-linux>), distributed under
the following MIT license:

Copyright © 2026 exceptionptr

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the “Software”), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## ktsu.CredentialCache

Native credential persistence uses `ktsu.CredentialCache`
(<https://github.com/ktsu-dev/CredentialCache>) under the MIT License.

Copyright (c) 2023-2026 ktsu-dev contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the “Software”), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
