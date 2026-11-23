# Builds VolumeBridgeApo.dll (x64). Run from a VS developer shell (CI: ilammy/msvc-dev-cmd).
param([string]$Out = "$PSScriptRoot\bin")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
# Static CRT (/MT): audiodg must not depend on a VC++ redistributable. No manifest (APO guidance).
cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX- /DUNICODE /D_UNICODE /LD `
  "$PSScriptRoot\VolumeBridgeApo.cpp" `
  /Fo"$Out\\" /Fe"$Out\VolumeBridgeApo.dll" `
  /link /DEF:"$PSScriptRoot\VolumeBridgeApo.def" /MANIFEST:NO `
  ole32.lib oleaut32.lib advapi32.lib shell32.lib uuid.lib `
  AudioBaseProcessingObjectV140.lib audiomediatypecrt.lib AudioEng.lib
if ($LASTEXITCODE -ne 0) { throw "APO build failed ($LASTEXITCODE)" }
