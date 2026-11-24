# Downloads the Windows build of gg2-server and unpacks it into .\gg2-server.
# Re-running it updates that directory in place and leaves files it did not
# ship (a gg2.json, logs) alone.
#
#   irm https://raw.githubusercontent.com/kylelmoy/gg2-server/master/install.ps1 | iex
#
# Environment:
#   GG2_DIR      where to install (default: .\gg2-server)
#   GG2_VERSION  a release tag such as v1.1.0 (default: the latest release)
#   GG2_REPO     owner/name of the GitHub repository (default: kylelmoy/gg2-server)
#
# Runs on Windows PowerShell 5.1 and PowerShell 7. It is wrapped in a script
# block and throws rather than calling exit, because `irm | iex` runs it inside
# the caller's own session: exit would close their window. install.sh is the
# Linux and macOS equivalent.

& {
    $ErrorActionPreference = 'Stop'
    # Invoke-WebRequest's progress bar makes a download several times slower on 5.1.
    $ProgressPreference = 'SilentlyContinue'
    # 5.1 can default to TLS 1.0, which GitHub refuses.
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo = if ($env:GG2_REPO) { $env:GG2_REPO } else { 'kylelmoy/gg2-server' }
    $dest = if ($env:GG2_DIR) { $env:GG2_DIR } else { Join-Path (Get-Location).Path 'gg2-server' }
    $base = if ($env:GG2_DOWNLOAD_BASE) { $env:GG2_DOWNLOAD_BASE } else { "https://github.com/$repo/releases/download" }

    $tag = $env:GG2_VERSION
    if (-not $tag) {
        $tag = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest").tag_name
        if (-not $tag) { throw "install.ps1: could not find the latest release of $repo" }
    }

    # win-x64 on every Windows machine: Windows on Arm runs it under emulation.
    $archive = "gg2-server-$tag-win-x64.zip"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("gg2-server-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        Write-Host "Downloading $archive"
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$tag/$archive" -OutFile (Join-Path $tmp $archive)
        Expand-Archive -Path (Join-Path $tmp $archive) -DestinationPath $tmp
        $unpacked = Join-Path $tmp 'gg2-server'
        if (-not (Test-Path (Join-Path $unpacked 'gg2-server.exe'))) {
            throw "install.ps1: $archive does not contain gg2-server\gg2-server.exe"
        }

        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -Path (Join-Path $unpacked '*') -Destination $dest -Recurse -Force
        Get-ChildItem -Path $dest -Recurse -File | Unblock-File
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host "Installed gg2-server $tag (win-x64) in $dest"
    Write-Host ''
    Write-Host 'Start a server with eight bots:'
    Write-Host "  & '$dest\gg2-server.exe' --bots 8"
    Write-Host ''
    Write-Host 'Then connect a Gang Garrison 2 client to 127.0.0.1:8190.'
}
