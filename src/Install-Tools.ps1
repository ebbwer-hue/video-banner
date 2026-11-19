$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$appDir = Split-Path $PSScriptRoot -Parent
$toolDir = Join-Path $appDir 'tools'
$stageDir = Join-Path $appDir ('download-' + [guid]::NewGuid().ToString('N'))
function Get-ExpectedHash([string]$sumFile, [string]$assetName) {
    # GitHub serves checksums as application/octet-stream. In Windows PowerShell
    # response.Content is byte[], so always read the saved file explicitly as UTF-8.
    $text = Get-Content -LiteralPath $sumFile -Raw -Encoding UTF8
    $pattern = '(?m)^([a-fA-F0-9]{64})[ \t]+\*?' + [regex]::Escape($assetName) + '[ \t]*\r?$'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { throw "Checksum entry not found: $assetName" }
    return $match.Groups[1].Value
}
function Confirm-Hash([string]$file, [string]$expected) {
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "SHA256 mismatch for $(Split-Path $file -Leaf). Expected: $expected; actual: $actual. File was not installed."
    }
}
New-Item -ItemType Directory -Force -Path $toolDir, $stageDir | Out-Null
try {
    if (-not (Test-Path (Join-Path $toolDir 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $toolDir 'ffprobe.exe'))) {
        Write-Host 'Downloading FFmpeg...'
        $archive = Join-Path $stageDir 'ffmpeg.zip'
        Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive -UseBasicParsing
        $sumFile = Join-Path $stageDir 'ffmpeg.sha256'
        Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256' -OutFile $sumFile -UseBasicParsing
        $sumText = Get-Content -LiteralPath $sumFile -Raw -Encoding UTF8
        $checksum = [regex]::Match($sumText, '^\s*([a-fA-F0-9]{64})(?:\s|$)').Groups[1].Value
        if (-not $checksum) { throw 'FFmpeg checksum is missing or invalid' }
        Confirm-Hash $archive $checksum
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $stageDir 'ffmpeg')
        foreach ($name in 'ffmpeg.exe','ffprobe.exe') {
            $binary = Get-ChildItem (Join-Path $stageDir 'ffmpeg') -Recurse -File -Filter $name | Select-Object -First 1
            Copy-Item -LiteralPath $binary.FullName -Destination (Join-Path $toolDir $name)
        }
    }
    Write-Host 'Downloading yt-dlp...'
    $yt = Join-Path $stageDir 'yt-dlp.exe'
    # Resolve latest once so the binary and its checksum come from one release.
    $release = Invoke-RestMethod 'https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest' -Headers @{ 'User-Agent' = 'VideoBanner-Installer' }
    $tag = [string]$release.tag_name
    if ($tag -notmatch '^[A-Za-z0-9._-]+$') { throw 'Invalid yt-dlp release tag' }
    $baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/$tag"
    $ytSumFile = Join-Path $stageDir 'yt-dlp-SHA256SUMS.txt'
    Invoke-WebRequest "$baseUrl/SHA2-256SUMS" -OutFile $ytSumFile -UseBasicParsing
    $ytExpected = Get-ExpectedHash $ytSumFile 'yt-dlp.exe'
    $installedYt = Join-Path $toolDir 'yt-dlp.exe'
    if (-not (Test-Path -LiteralPath $installedYt) -or (Get-FileHash -LiteralPath $installedYt -Algorithm SHA256).Hash -ne $ytExpected) {
        Invoke-WebRequest "$baseUrl/yt-dlp.exe" -OutFile $yt -UseBasicParsing
        Confirm-Hash $yt $ytExpected
        Copy-Item -LiteralPath $yt -Destination $installedYt -Force
    } else {
        Write-Host "yt-dlp $tag is already verified."
    }
    if (-not (Test-Path (Join-Path $toolDir 'node.exe'))) {
        Write-Host 'Downloading Node.js...'
        $version = 'v24.11.1'
        $node = Join-Path $stageDir 'node.exe'
        Invoke-WebRequest "https://nodejs.org/dist/$version/win-x64/node.exe" -OutFile $node -UseBasicParsing
        $nodeSumFile = Join-Path $stageDir 'node-SHA256SUMS.txt'
        Invoke-WebRequest "https://nodejs.org/dist/$version/SHASUMS256.txt" -OutFile $nodeSumFile -UseBasicParsing
        $expected = Get-ExpectedHash $nodeSumFile 'win-x64/node.exe'
        Confirm-Hash $node $expected
        Copy-Item -LiteralPath $node -Destination (Join-Path $toolDir 'node.exe')
    }
    Write-Host 'Ready.'
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stageDir)
    $resolvedApp = [IO.Path]::GetFullPath($appDir).TrimEnd('\') + '\'
    if ($resolvedStage.StartsWith($resolvedApp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
