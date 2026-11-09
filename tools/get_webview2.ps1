$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$lib  = Join-Path $root 'lib'
New-Item -ItemType Directory -Force $lib | Out-Null
$tmp  = Join-Path $env:TEMP 'emopunk_wv2'
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip  = Join-Path $tmp 'wv2.zip'
$urls = @('https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.2903.40',
          'https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2')
$ok = $false
foreach ($u in $urls) {
  try { Write-Host "      $u"; Invoke-WebRequest -Uri $u -OutFile $zip -UseBasicParsing; $ok = $true; break }
  catch { Write-Host "      failed: $($_.Exception.Message)" }
}
if (-not $ok) { exit 1 }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pkg = Join-Path $tmp 'pkg'
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $pkg)
function Pick($name, $filter) {
  $f = Get-ChildItem $pkg -Recurse -Filter $name | Where-Object { $_.FullName -match $filter } | Sort-Object FullName | Select-Object -First 1
  if (-not $f) { throw "$name not found in package" }
  Copy-Item $f.FullName $lib -Force
  Write-Host "      + $name"
}
try {
  Pick 'Microsoft.Web.WebView2.Core.dll'     '\\lib\\net4'
  Pick 'Microsoft.Web.WebView2.WinForms.dll' '\\lib\\net4'
  Pick 'WebView2Loader.dll'                  'win-x64'
} catch { Write-Host "      $($_.Exception.Message)"; exit 1 }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
exit 0
