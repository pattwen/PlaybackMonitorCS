# Third-Party Notices

PlaybackMonitorCS uses or references third-party software and libraries.
These components are not relicensed by the project's GPL-3.0-only
notice. Each component remains subject to its own applicable license.

## FFmpeg

Project: <https://ffmpeg.org/>

FFmpeg is generally licensed under the GNU Lesser General Public License
(LGPL) version 2.1 or later. Optional components may be licensed under
the GNU General Public License (GPL); enabling those components changes
the applicable licensing terms for the FFmpeg build.

The project's README instructs users to obtain an FFmpeg Shared Build
themselves. The exact binary supplier, version, and configuration are
not specified here. Verify the actual build before redistribution.

Official licensing and compliance information:
<https://ffmpeg.org/legal.html>

For each redistributed FFmpeg build, record:

- FFmpeg version
- Binary supplier
- Build configuration
- Applicable license (LGPL/GPL and any other relevant notices)
- Corresponding source code and build information required by that
  license

Do not assume that all FFmpeg Shared Builds have the same license.

## FFmpeg.AutoGen

- NuGet package: `FFmpeg.AutoGen`
- Version: `9.0.1.1`
- License: MIT
- Copyright: Copyright © 2025 Ruslan Balanukhin (Rationale One)
- NuGet package: <https://www.nuget.org/packages/FFmpeg.AutoGen/9.0.1.1>
- Upstream project: <https://github.com/Ruslan-B/FFmpeg.AutoGen>

The MIT license applies to FFmpeg.AutoGen itself. It does not change the
license of the native FFmpeg libraries loaded at runtime.

### MIT License

Copyright © 2025 Ruslan Balanukhin (Rationale One)

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MediaInfo / MediaInfoLib

- Upstream project: <https://github.com/MediaArea/MediaInfoLib>
- Upstream license: BSD 2-Clause
- Upstream copyright notice: Copyright (c) 2002-2025, MediaArea.net SARL
- Binary used by this project: `mediainfo.dll`
- Exact binary version and supplier: Not specified in the supplied
  README. Verify the license and notices included with the actual binary
  package before redistribution.

### BSD 2-Clause License

Copyright (c) 2002-2025, MediaArea.net SARL

All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

1.  Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
2.  Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS
IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Other Dependencies

Other NuGet packages, native libraries, and bundled resources may have
separate licenses. Review the exact versions and preserve their required
notices before redistributing them.

The project's GPL-3.0-only license applies only to project materials
released under that license by the project copyright holder. It does not
replace or override the independent licenses of third-party components.
