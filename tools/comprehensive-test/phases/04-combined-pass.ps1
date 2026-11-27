# Shared evidence for two distinct combined-fix routes.
function Assert-CombinedFixtureFix {
    param([string]$PathLike, [string[]]$IssueTypes, [int]$MaxHeight = 0)
    Start-MediaDashScan -Wait -TimeoutSec 600
    $pair = @(Get-MediaDashIssues | Where-Object { $_.Path -like "*$PathLike*" -and $_.Type -in $IssueTypes })
    Assert-Equal $IssueTypes.Count $pair.Count 'every required companion issue must be detected'
    Assert-Equal $IssueTypes.Count @($pair.Type | Select-Object -Unique).Count 'distinct companion types required'
    Assert-Equal 1 @($pair.Path | Select-Object -Unique).Count 'companions must share one full source path'
    $source = $pair[0].Path
    $originalHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $beforeBins = @((Get-MediaDashRecycleBinItems).BinPath)
    foreach ($issue in $pair) { Approve-MediaDashIssue $issue.Id }
    Start-MediaDashFix -Wait -TimeoutSec 900
    $firstRow = $null
    foreach ($issueType in $IssueTypes) {
        $row = Get-CurrentFixHistory -FileName ([IO.Path]::GetFileName($source)) -Type $issueType
        if ($null -eq $firstRow) { $firstRow = $row }
    }
    $entries = @(Get-MediaDashRecycleBinItems | Where-Object { $_.OriginalPath -eq $source -and $_.BinPath -notin $beforeBins })
    Assert-Equal 1 $entries.Count 'combined action must recycle the original exactly once'
    Assert-Equal $originalHash (Get-FileHash -LiteralPath $entries[0].BinPath -Algorithm SHA256).Hash 'recycled original hash'
    $ffmpeg = Get-JellyfinFfmpegPath
    $ffprobe = Join-Path (Split-Path -Parent $ffmpeg) ('ffprobe' + [IO.Path]::GetExtension($ffmpeg))
    $raw = & $ffprobe -v error -show_streams -of json $source
    if ($LASTEXITCODE -ne 0) { throw 'Cannot probe combined output.' }
    $streams = ($raw | ConvertFrom-Json).streams
    Assert-Equal 1 @($streams | Where-Object codec_type -EQ 'audio').Count 'one English audio stream retained'
    Assert-Equal 0 @($streams | Where-Object { $_.codec_type -in @('audio','subtitle') -and $_.tags.language -ne 'eng' }).Count 'disallowed tracks removed'
    if ($MaxHeight -gt 0) {
        $video = @($streams | Where-Object codec_type -EQ 'video')
        Assert-Equal 1 $video.Count 'one video stream retained'
        Assert-LessOrEqual $MaxHeight $video[0].height 'quality companion obeys the configured ceiling'
    }
    & $ffmpeg -nostdin -v error -xerror -i $source -map '0:v?' -map '0:a?' -f null - 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Combined output failed full audio/video decode.' }
    Restore-FromRecycleBin -HistoryId $firstRow.Id
    Assert-Equal $originalHash (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash 'restored original hash'
}

function Invoke-Phase-CombinedPass {
    Start-Phase 'Combined track and transcode-companion routing'
    Invoke-Test 'Combined audio/subtitle fix produces current history and one original' {
        Set-MediaDashConfig @{AllowedAudioLanguages=@('eng'); AllowedSubtitleLanguages=@('eng'); DryRun=$false}
        Assert-CombinedFixtureFix -PathLike 'Sub Heavy' -IssueTypes @('AudioLanguage','SubtitleLanguage')
    }
    Invoke-Test 'Transcode + Track companion routing produces one recoverable original' {
        Assert-CombinedFixtureFix -PathLike 'Companion Quality' -IssueTypes @('Quality','AudioLanguage') -MaxHeight (Get-MediaDashConfig).MaxResolutionHeight
    }
    Invoke-Test 'Post-combined-pass: restore DryRun ON' { Set-MediaDashConfig @{DryRun=$true} }
}
