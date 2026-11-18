# Third-Party Notices

NFDFix includes the third-party components listed below. Each is distributed
under its own license, separate from NFDFix's BSD 3-Clause License.

## Pretendard

- Version: 1.3.9
- Copyright (c) 2021, Kil Hyung-jin, with Reserved Font Name Pretendard.
- Project: https://github.com/orioncactus/pretendard
- License: SIL Open Font License, Version 1.1
- Bundled file: `Assets/Fonts/PretendardVariable.ttf` in the app folder (unmodified)
- Full license text: `Assets/Fonts/Pretendard-OFL.txt` in the app folder;
  [`NFDFix/Assets/Fonts/Pretendard-OFL.txt`](NFDFix/Assets/Fonts/Pretendard-OFL.txt) in the source repository

NFDFix uses Pretendard as its UI font. The font is bundled unmodified and is not
sold on its own.

## .NET Runtime

- Version: 11.0.0
- Copyright (c) .NET Foundation and Contributors
- Project: https://github.com/dotnet/runtime
- License: MIT (see [MIT License](#mit-license) below)
- Third-party notices of the .NET Runtime:
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

NFDFix is published self-contained with Native AOT, so parts of the .NET
Runtime and its base class libraries are compiled into `NFDFix.exe`.

## Windows App SDK and WinUI 3

- Version: 2.7.0 (`Microsoft.WindowsAppSDK.WinUI`, `Microsoft.WindowsAppSDK.Foundation`,
  `Microsoft.WindowsAppSDK.Runtime`)
- Copyright (c) Microsoft Corporation.
- Projects: https://github.com/microsoft/WindowsAppSDK,
  https://github.com/microsoft/microsoft-ui-xaml
- License: MIT (see [MIT License](#mit-license) below)
- Bundled file: `Microsoft.WindowsAppRuntime.Bootstrap.dll`; the .NET projections
  are compiled into `NFDFix.exe`.

## C#/WinRT and the Windows SDK projection

- Version: 10.0.26100.87 (`Microsoft.Windows.SDK.NET.Ref`)
- Copyright (c) Microsoft Corporation.
- Project: https://github.com/microsoft/CsWinRT
- License: MIT (see [MIT License](#mit-license) below)

The WinRT interop runtime and the Windows SDK projection are compiled into
`NFDFix.exe`.

## CommunityToolkit.Mvvm

- Version: 8.4.2
- Copyright (c) .NET Foundation and Contributors
- Project: https://github.com/CommunityToolkit/dotnet
- License: MIT (see [MIT License](#mit-license) below)

Compiled into `NFDFix.exe`.

## MIT License

The following license text applies to the .NET Runtime, Windows App SDK,
WinUI 3, C#/WinRT and CommunityToolkit.Mvvm, each with the copyright notice
listed in its section above.

```
The MIT License (MIT)

Copyright (c) <copyright holders listed above>

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
```
