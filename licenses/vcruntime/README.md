# Microsoft Visual C++ runtime

The WAI application package carries unmodified x64 MSVCP140.dll,
VCRUNTIME140.dll and VCRUNTIME140_1.dll version 14.51.36247.0 beside its
executables. They are extracted without installing the redistributable.
Their Microsoft installer signature, archive checksum and individual DLL
checksums are checked during preparation; exact inputs are in
native-dependencies.json and the shipped native-provenance.json.

These proprietary Microsoft files are not covered by WAI's MIT license.
The original runtime license document and its text rendering are included.
Redistribution rights are governed separately by the applicable Visual Studio
license and its distributable-code list. The Visual Studio Community 2022
terms used by this project's developer are included for reference; they are
not an instruction to install Visual Studio to run WAI.

Official sources:

- https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution#visual-c-runtime-files
- https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method
- https://visualstudio.microsoft.com/license-terms/vs2022-ga-community/
- https://visualstudio.microsoft.com/wp-content/uploads/2021/11/Visual-Studio-2022-Community-License-EN.docx
- https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/
- https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx

The .docx documents preserve the originals. The .txt files contain their
paragraph text for convenient reading, without Word's automatic numbering.
The developer must retain applicable Visual Studio redistribution rights
when redistributing these components. End users do not need a developer
license or a separate machine-wide runtime installation.
