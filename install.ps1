param([switch]$CheckOnly, [switch]$RadminOnly, [string]$GamePath)
$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'Granny Multiplayer — установка'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Эталон — сборка хоста: Steam build 21006637 (06.06.2026), Unity 6000.0.58f2
$RefGaSize   = 39105024
$RefMdSize   = 9163140
$RefGaSha    = 'E260D044CBAAD538DCCC6767E7E4344CC3ABEEBE7C56723ACF1672BA5DD7AF9A'
$RefUnity    = '6000.0.58'

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }

function Test-GameDir($d) {
    if (-not $d -or -not (Test-Path $d)) { return $false }
    return (Test-Path (Join-Path $d 'UnityPlayer.dll')) -and ((Get-ChildItem $d -Filter '*_Data' -Directory -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
}

function Find-Game {
    $cands = @()
    $cands += $here
    $cands += Split-Path -Parent $here
    $cands += (Get-Location).Path
    # Steam знает все свои библиотеки, на каком бы диске они ни были
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        $cands += (Join-Path $steam 'steamapps\common\Granny')
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $cands += (Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Granny')
            }
        }
    } catch { }
    foreach ($root in @('C:\','D:\','E:\','F:\')) {
        if (-not (Test-Path $root)) { continue }
        foreach ($sub in @('Games\Granny','Granny','Program Files\Granny','Program Files (x86)\Granny','Program Files (x86)\Steam\steamapps\common\Granny','SteamLibrary\steamapps\common\Granny','Steam\steamapps\common\Granny','Игры\Granny')) {
            $cands += (Join-Path $root $sub)
        }
    }
    foreach ($desk in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:USERPROFILE 'Downloads'))) {
        if (Test-Path $desk) { Get-ChildItem $desk -Directory -Filter '*Granny*' -ErrorAction SilentlyContinue | ForEach-Object { $cands += $_.FullName } }
    }
    foreach ($c in $cands) { if (Test-GameDir $c) { return (Resolve-Path $c).Path } }
    return $null
}

# Radmin VPN: качается с официального сайта, подпись проверяется
$RadminUrl = 'https://download.radmin-vpn.com/download/files/Radmin_VPN_2.1.4951.1.exe'

function Find-Radmin {
    foreach ($d in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($d -and (Test-Path (Join-Path $d 'Radmin VPN\RvRvpnGui.exe'))) { return (Join-Path $d 'Radmin VPN\RvRvpnGui.exe') }
    }
    return $null
}

function Say-RadminHelp {
    Say ''
    Say 'Как зайти в одну сеть в Radmin VPN (нужно один раз):' White
    Say '  Хост: кнопка "Создать сеть", придумай название и пароль.' White
    Say '  Друг: кнопка "Присоединиться к сети", вводит то же название и пароль.' White
    Say '  После этого окно Radmin можно закрыть: он работает сам и запускается с Windows.' White
    Say '  Потом хост создаёт игру: нужный IP игра покажет сама.' White
}

function Install-Radmin {
    try {
        $gui = Find-Radmin
        if ($gui) { Say 'Radmin VPN уже установлен.' Green }
        else {
            Say 'Скачиваю Radmin VPN с radmin-vpn.com...' Cyan
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $ProgressPreference = 'SilentlyContinue'
            $file = Join-Path $env:TEMP 'Radmin_VPN_setup.exe'
            Invoke-WebRequest $RadminUrl -OutFile $file -UseBasicParsing
            $sign = Get-AuthenticodeSignature $file
            if ($sign.Status -ne 'Valid' -or $sign.SignerCertificate.Subject -notmatch 'Famatech') {
                Remove-Item $file -Force -ErrorAction SilentlyContinue
                throw 'у скачанного файла нет подписи Famatech'
            }
            Say 'Подпись Famatech верна. Устанавливаю: Windows спросит разрешение, нажми "Да".' Cyan
            $p = Start-Process $file -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Verb RunAs -Wait -PassThru
            Remove-Item $file -Force -ErrorAction SilentlyContinue
            $gui = Find-Radmin
            if (-not $gui) { throw "установщик завершился с кодом $($p.ExitCode), но программа не появилась" }
            Say 'Radmin VPN установлен.' Green
        }
        Start-Process $gui
        Say-RadminHelp
    }
    catch {
        Say "Radmin VPN поставить не вышло: $($_.Exception.Message)" Yellow
        Say 'Скачай его вручную с radmin-vpn.com. Мод от этого не зависит.' Yellow
    }
}

if ($RadminOnly) { Install-Radmin; exit 0 }

Say '=============================================' Red
Say '      GRANNY MULTIPLAYER — мод от хоста' Red
Say '=============================================' Red
Say ''

$game = if ($GamePath) { if (-not (Test-GameDir $GamePath)) { throw "Указана неверная папка игры" }; (Resolve-Path $GamePath).Path } else { Find-Game }
if (-not $game) {
    Say 'Папка с игрой не найдена автоматически. Выбери папку, где лежит Granny.exe' Yellow
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Выбери папку с игрой Granny (где лежит Granny.exe)'
    if ($dlg.ShowDialog() -ne 'OK') { Say 'Отменено.' Red; exit 1 }
    $game = $dlg.SelectedPath
    if (-not (Test-GameDir $game)) { Say "В папке $game нет игры на Unity (UnityPlayer.dll / *_Data)." Red; exit 1 }
}
Say "Игра: $game" Cyan
$exe = Get-ChildItem $game -Filter '*.exe' | Where-Object { $_.Name -notmatch 'UnityCrashHandler|unins' } | Select-Object -First 1

# версия
Say ''
Say '--- ВЕРСИЯ ТВОЕЙ ИГРЫ ---' White
$unity = (Get-Item (Join-Path $game 'UnityPlayer.dll')).VersionInfo.FileVersion
Say ("Unity:            {0}" -f $unity)
$ga = Join-Path $game 'GameAssembly.dll'
$dataDir = Get-ChildItem $game -Filter '*_Data' -Directory | Select-Object -First 1
$md = Join-Path $dataDir.FullName 'il2cpp_data\Metadata\global-metadata.dat'
$ok = $true
if (-not (Test-Path $ga)) {
    Say 'GameAssembly.dll:  НЕТ — это старая (Mono) сборка Granny.' Red
    $ok = $false
} else {
    $gaSize = (Get-Item $ga).Length
    $gaSha = (Get-FileHash $ga -Algorithm SHA256).Hash
    $mdSize = if (Test-Path $md) { (Get-Item $md).Length } else { 0 }
    Say ("GameAssembly.dll: {0} байт" -f $gaSize)
    Say ("metadata:         {0} байт" -f $mdSize)
    Say ("SHA256:           {0}" -f $gaSha)
    if ($gaSha -ne $RefGaSha -or $mdSize -ne $RefMdSize) { $ok = $false }
}
if ($exe) { Say ("exe:              {0}  (дата файлов игры {1:dd.MM.yyyy})" -f $exe.Name, (Get-Item $ga -ErrorAction SilentlyContinue).LastWriteTime) }
Say ''
Say '--- ВЕРСИЯ ХОСТА ---' White
Say "Steam build 21006637 от 06.06.2026, Unity $RefUnity, GameAssembly $RefGaSize байт"
Say ''

$info = @"
Granny версия ($env:USERNAME, $(Get-Date -Format 'dd.MM.yyyy HH:mm'))
Unity: $unity
GameAssembly: $gaSize байт, SHA256 $gaSha
metadata: $mdSize байт
Совпадает с хостом: $ok
"@
# Информация о системе не сохраняется в файл.

if ($ok) {
    Say 'ВЕРСИЯ СОВПАДАЕТ С ХОСТОМ — всё ок.' Green
} elseif ($unity -like "$RefUnity*") {
    Say 'ВЕРСИЯ НЕ СОВПАДАЕТ ТОЧНО (тот же Unity, но другая сборка игры).' Yellow
    Say 'Мод может заработать, но возможны ошибки. Лучше поставить ту же версию, что у хоста.' Yellow
} else {
    Say 'ВЕРСИЯ НЕ СОВПАДАЕТ — мод собран под Granny на Unity 6000.0.58 (обновление от 06.06.2026).' Red
    Say 'На этой версии мод не заработает. Нужна такая же версия игры, как у хоста.' Red
}

Say ''

if ($CheckOnly) { exit 0 }

if (-not $ok) {
    $a = Read-Host 'Всё равно установить мод? (y/n)'
    if ($a -notmatch '^[yYдД]') { Say 'Установка отменена.' Yellow; exit 1 }
}

# установка
$running = Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($game, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Say 'Игра запущена — закрой её и нажми Enter.' Yellow
    Read-Host | Out-Null
    $running = Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($game, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw 'Игра всё ещё открыта. Установка отменена.' }
}

$payload = Join-Path $here 'payload'
if (-not (Test-Path $payload)) { Say 'Нет папки payload рядом с установщиком — распакуй архив целиком.' Red; exit 1 }

$plugin = Join-Path $game 'BepInEx\plugins\GrannyMP'
$oldMod = Join-Path $plugin 'GrannyMP.dll'
if (Test-Path $oldMod) { Copy-Item $oldMod (Join-Path $plugin 'GrannyMP.dll.old') -Force }
if (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Core.dll')) {
    Say 'BepInEx уже установлен. Обновляю только файлы GrannyMP.' Cyan
    New-Item -ItemType Directory -Force -Path $plugin | Out-Null
    Get-ChildItem (Join-Path $payload 'BepInEx\plugins\GrannyMP') -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $plugin $_.Name) -Force }
} else {
    if (Test-Path (Join-Path $game 'winhttp.dll')) { throw 'Обнаружен winhttp.dll от другой установки. Автоматическая замена отменена.' }
    Say 'Устанавливаю BepInEx и актуальный мод...' Cyan
    Copy-Item -Path (Join-Path $payload '*') -Destination $game -Recurse -Force
    Copy-Item -Path (Join-Path $payload '.doorstop_version') -Destination $game -Force -ErrorAction SilentlyContinue
}
$expected = (Get-FileHash (Join-Path $payload 'BepInEx\plugins\GrannyMP\GrannyMP.dll') -Algorithm SHA256).Hash
if ((Get-FileHash (Join-Path $plugin 'GrannyMP.dll') -Algorithm SHA256).Hash -ne $expected) { throw 'Проверка скопированного мода не прошла.' }
Say 'Установлена полная версия 1.1.8, отдельно старые обновления не нужны.' Green

$cfg = Join-Path $game 'BepInEx\config\local.adam.granny.multiplayer.cfg'
$name = Read-Host 'Твой ник в игре (Enter — сохранить текущий или имя Windows)'
if ($name) {
    New-Item -ItemType Directory -Force -Path (Split-Path $cfg) | Out-Null
    $lines = @()
    if (Test-Path $cfg) { $lines = @(Get-Content $cfg -Encoding UTF8) }
    if ($lines -match '^\s*Имя\s*=') { $lines = $lines -replace '^\s*Имя\s*=.*', "Имя = $name" }
    else { $lines = @('[Сеть]', "Имя = $name") + $lines }
    $lines | Out-File -FilePath $cfg -Encoding utf8
}

Say ''
Say 'Radmin VPN нужен, если вы играете не в одной домашней сети.' White
$r = Read-Host 'Поставить Radmin VPN? (y = да, Enter = нет)'
if ($r -match '^[yYдД]') { Install-Radmin }

Say ''
Say 'ГОТОВО!' Green
Say 'Первый запуск игры будет долгим (1-3 минуты, BepInEx готовит файлы) — это нормально.' White
Say 'В игре: меню "Мультиплеер", вводишь IP хоста и жмёшь "Присоединиться".' White
Say 'Для игры оба участника должны использовать одну сборку мода и совместимую версию Granny.' White
