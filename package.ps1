# Assembles a clean, shippable WotRUpscaler folder and zip in .\dist: only the files the mod needs, no logs, captures or user settings.
# The release includes NVIDIA's DLSS runtime (nvngx_dlss.dll) and WotRUpscalerNative.dll (which contains NVIDIA NGX code), as permitted for the SDK's
# redistributable components inside an application. NVIDIA's license text and a notice therefore travel with the package, and this script refuses to
# build a package without them. Neither NVIDIA file is ever committed to the source repository.
# It also fails when a build fails, when the native plugin or the shader bundle is older than its sources, and when the output contains a string that
# must not be published (the build machine's user name or paths, plus anything listed in the git-ignored release-forbidden.txt, one string per line).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$info = Get-Content (Join-Path $root "managed\Info.json") -Raw | ConvertFrom-Json
$version = $info.Version
$name = "WotRUpscaler"
$dist = Join-Path $root "dist"
$out = Join-Path $dist $name
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# 1. Managed mod, Release (no debug symbols, so no build paths inside).
Push-Location $root
dotnet build (Join-Path $root "managed\WotRUpscaler.csproj") -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "dotnet build failed." }
Pop-Location
$dll = Join-Path $root "managed\bin\Release\net48\WotRUpscaler.dll"
if (-not (Test-Path $dll)) { throw "WotRUpscaler.dll was not built." }
# The release must contain the in-game menu page (it is built in only when ModMenu is installed next to the game).
if (-not (Select-String -Path $dll -Pattern "wotrupscaler.preset.other" -Encoding Unicode -Quiet)) { throw "The build has no ModMenu page. Install ModMenu in the game's Mods folder before packaging." }

# 2. Native plugin and shader bundle must exist and be newer than their sources.
$native = Join-Path $root "native\bin\WotRUpscalerNative.dll"
if (-not (Test-Path $native)) { throw "Native plugin not built. Run native\build.bat first." }
if ((Get-Item $native).LastWriteTime -lt (Get-Item (Join-Path $root "native\WotRUpscaler.cpp")).LastWriteTime) { throw "WotRUpscalerNative.dll is older than WotRUpscaler.cpp. Run native\build.bat." }
$bundle = Join-Path $root "unity\bundle\wotrupscaler"
if (-not (Test-Path $bundle)) { throw "Shader bundle not built. Run the Unity BuildBundle step (see README)." }
$newestShader = Get-ChildItem (Join-Path $root "unity\Assets") -Recurse -Filter *.shader | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ((Get-Item $bundle).LastWriteTime -lt $newestShader.LastWriteTime) { throw "The shader bundle is older than $($newestShader.Name). Rebuild it." }

Copy-Item $dll $out
Copy-Item (Join-Path $root "managed\Info.json") $out
Copy-Item $native $out
Copy-Item $bundle $out
foreach ($f in "README.md", "CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md") { Copy-Item (Join-Path $root $f) $out }

# 3. NVIDIA files: the runtime and the license text that must travel with them.
$sdk = Join-Path $root "..\ThirdParty\DLSS"
$nvidiaLicense = Join-Path $sdk "LICENSE.txt"
$runtime = Join-Path $sdk "lib\Windows_x86_64\rel\nvngx_dlss.dll"
if (-not (Test-Path $nvidiaLicense)) { throw "NVIDIA SDK license not found at $nvidiaLicense. Clone github.com/NVIDIA/DLSS to ..\ThirdParty\DLSS first." }
if (-not (Test-Path $runtime)) { throw "DLSS runtime not found at $runtime." }
New-Item -ItemType Directory -Force (Join-Path $out "licenses") | Out-Null
Copy-Item $nvidiaLicense (Join-Path $out "licenses\NVIDIA-RTX-SDKs-LICENSE.txt")
Copy-Item $runtime $out

# 4. Nothing that must not be published may be inside any file (text or binary, ASCII or UTF-16).
$forbidden = @($env:USERNAME, $env:USERPROFILE, "gmail.com")
$extra = Join-Path $root "release-forbidden.txt"
if (Test-Path $extra) { $forbidden += Get-Content $extra | Where-Object { $_.Trim().Length -gt 0 } }
$forbidden = $forbidden | Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique
$bad = @()
foreach ($file in Get-ChildItem $out -Recurse -File) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $ascii = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    $utf16b = if ($bytes.Length -gt 1) { [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) } else { "" }
    foreach ($s in $forbidden) {
        if ($ascii.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16b.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $bad += "$($file.Name) contains a forbidden string (entry $([array]::IndexOf($forbidden, $s) + 1) of the list)"
        }
    }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host $_ -ForegroundColor Red }; throw "Release scan failed: see above." }

# 5. Zip.
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
Get-ChildItem $out -Recurse -File | Select-Object @{n="Path";e={$_.FullName.Substring($out.Length + 1)}}, Length | Format-Table -AutoSize
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB). It contains NVIDIA files (nvngx_dlss.dll, WotRUpscalerNative.dll); NVIDIA's license text is in licenses\."
