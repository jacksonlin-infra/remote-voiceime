# Windows runtime notices

The Windows 0.1.1 trial bundles Microsoft .NET 10.0.13. The notice files are copied from the official NuGet runtime packages, with line endings normalized:

- `Microsoft.NETCore.App.Runtime.win-x64` 10.0.13: `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT`.
- `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.13: `LICENSE`.

The Windows project copies these files alongside the self-contained executable. When upgrading the bundled runtime, update its notices from the corresponding packages as well. These are third-party runtime licenses, not a choice of license for this project's own source code.
