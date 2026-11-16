@echo off
setlocal
chcp 65001 >nul
set "GRANNY_ARCHIVE=%~dp0GrannyMP-payload-1.1.8.zip"
set "GRANNY_SHA=1D03C749EB197F1E7B4472A36946A7BEC136B5F7E93A16C70166FDB20CE2BF9C"
set "GRANNY_URL=https://github.com/Fuckswat/granny-online/releases/download/v1.1.8/GrannyMP-payload-1.1.8.zip"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { $zip=$env:GRANNY_ARCHIVE; if (!(Test-Path -LiteralPath $zip)) { $zip=Join-Path ([IO.Path]::GetTempPath()) ('GrannyMP-'+[guid]::NewGuid().ToString()+'.zip'); [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Write-Host 'Downloading GrannyMP 1.1.8...'; try { Invoke-WebRequest -Uri $env:GRANNY_URL -OutFile $zip -UseBasicParsing } catch { Write-Host 'Release unavailable. Paste the HTTPS link to GrannyMP-payload-1.1.8.zip.'; $url=Read-Host 'Link'; if ($url -notmatch '^https://') { throw 'An HTTPS link is required.' }; Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing } }; if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $env:GRANNY_SHA) { throw 'Archive checksum does not match. Installation stopped.' }; $dir=Join-Path ([IO.Path]::GetTempPath()) ('GrannyMP-'+[guid]::NewGuid().ToString()); New-Item -ItemType Directory -Path $dir | Out-Null; try { Expand-Archive -LiteralPath $zip -DestinationPath $dir; & (Join-Path $dir 'install.ps1'); if (!$?) { throw 'Installer failed.' } } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue; if ($zip -ne $env:GRANNY_ARCHIVE) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue } } } catch { Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red; exit 1 }"
if errorlevel 1 echo Installation did not complete.
echo.
pause
