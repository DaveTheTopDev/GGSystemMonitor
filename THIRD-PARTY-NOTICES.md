# Third-party components

GGSystemMonitor is distributed under GPL-3.0; bundled dependencies retain their own licenses.
Exact direct and transitive versions and integrity hashes are recorded in packages.lock.json.

- LibreHardwareMonitorLib 0.9.6: MPL-2.0.
  Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6
  The application consumes the published upstream package without modifying its library.
- Newtonsoft.Json: MIT. https://github.com/JamesNK/Newtonsoft.Json
- Microsoft System.* and Microsoft.Bcl.* packages: upstream .NET licenses.
  https://github.com/dotnet/runtime
- HidSharp: https://github.com/IntergatedCircuits/HidSharp
- DiskInfoToolkit, RAMSPDToolkit-NDD and their transitive dependencies:
  consult the package metadata and license files in the restored NuGet packages.

PawnIO 2.2.0 is bundled unmodified and installed automatically when missing or outdated. It is a shared prerequisite and is not removed on uninstall. Driver source (GPL-2.0-or-later with the upstream exception), COPYING, and the pinned PawnPP interpreter source are included in Prerequisites source archives. Microsoft .NET Framework 4.8 is redistributed unmodified under its accompanying Microsoft terms. No antivirus exclusions are changed.

Moq and Castle.Core are development/test dependencies only and are not included in release ZIPs.

## OLED native glyph subset (from GNU Unifont 15.1.04)

GGSystemMonitor embeds a renamed subset of the bitmap data from the public
`unifont_all-15.1.04.hex.gz` release for native rows in mixed-font OLED frames.
The selected glyph bitmaps are unmodified. The subset is distributed under
the SIL Open Font License 1.1 below, separately from the application code.

Source: https://unifoundry.com/pub/unifont/unifont-15.1.04/font-builds/

Copyright (C) 1998-2022 Roman Czyborra, Paul Hardy, Qianqian Fang,
Andrew Miller, Johnnie Weaver, David Corbett, Nils Moskopp,
Rebecca Bettencourt, et al.

The SIL Open Font License version 1.1 is copied below, and is also
available with a FAQ at http://scripts.sil.org/OFL.


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
