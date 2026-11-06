param([string]$RunDirectory = '.local/query-evaluation-20261106', [string]$OutputPath = 'Documents/QueryEvaluationResults-2026-11-06.json')
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunDirectory)
$report = Get-Content (Join-Path $run 'report.json') -Raw | ConvertFrom-Json
$supplement = Get-Content (Join-Path $run 'short-instruction-report.json') -Raw | ConvertFrom-Json
$queries = @($report.Queries) + @($supplement.Queries)
function Metrics($rows) {
    [ordered]@{
        Count=$rows.Count; EvidenceAt1=@($rows | Where-Object Rank -eq 1).Count
        EvidenceAt5=@($rows | Where-Object Rank -ne $null).Count
        FileAt1=@($rows | Where-Object FileRank -eq 1).Count
        MrrAt5=[math]::Round(($rows | ForEach-Object {if ($_.Rank) {1.0/$_.Rank} else {0}} | Measure-Object -Average).Average,4)
        Errors=@($rows | Where-Object Error).Count
        MeanReturnedCharacters=[math]::Round(($rows | Measure-Object Characters -Average).Average,1)
        MeanSeconds=[math]::Round(($rows | Measure-Object Seconds -Average).Average,4)
    }
}
$metrics = @($queries | Group-Object Style | ForEach-Object {
    [ordered]@{Style=$_.Name; Metrics=(Metrics $_.Group)}
})
$subsets = @($queries | Group-Object Style | ForEach-Object {
    $style=$_.Name
    $_.Group | Group-Object {if ($_.File.EndsWith('.cs')) {'code'} elseif ($_.File -eq 'idempotent.txt') {'english_document'} else {'japanese_document'}} | ForEach-Object {
        [ordered]@{Style=$style; Subset=$_.Name; Metrics=(Metrics $_.Group)}
    }
})
# Exploratory two-query fusion, fixed k=60. Exact returned passage deduplication only.
# Rank by reciprocal ranks, never by evidence labels or cross-query similarity scores.
$fusions = @()
foreach ($second in @('keywords','description')) {
    $fused = @()
    foreach ($question in $report.Queries | Where-Object Style -eq 'question') {
        $alternative = $report.Queries | Where-Object {$_.Id -eq $question.Id -and $_.Style -eq $second}
        $candidates = @{}
        $order=0
        foreach ($query in @($question,$alternative)) {
            $seen = [Collections.Generic.HashSet[string]]::new()
            foreach ($hit in $query.Results) {
                $identity = $hit.File + "`n" + $hit.ParentChunkId + "`n" + $hit.Content
                $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity)))
                if (-not $seen.Add($key)) { continue }
                if (-not $candidates.ContainsKey($key)) {
                    $candidates[$key] = [pscustomobject]@{Score=0.0;Order=$order++;Hit=$hit}
                }
                $candidates[$key].Score += 1.0/(60+$hit.Rank)
            }
        }
        $ranked = @($candidates.Values | Sort-Object @{Expression='Score';Descending=$true},Order | Select-Object -First 5)
        $rank=$null; $fileRank=$null; $chars=0
        for ($i=0; $i -lt $ranked.Count; $i++) {
            $hit=$ranked[$i].Hit; $chars += $hit.Content.Length
            if ($hit.File -eq $question.File) {
                if ($null -eq $fileRank) { $fileRank=$i+1 }
                $all = $null -ne $hit.Content
                foreach ($e in $question.ExpectedEvidence) { if ($null -eq $hit.Content -or -not $hit.Content.Contains($e,[StringComparison]::OrdinalIgnoreCase)) {$all=$false} }
                if ($all -and $null -eq $rank) { $rank=$i+1 }
            }
        }
        $fused += [pscustomobject]@{Id=$question.Id;Rank=$rank;FileRank=$fileRank;Characters=$chars;Seconds=$question.Seconds+$alternative.Seconds;Error=$null}
    }
    $fusions += [ordered]@{Name="question+$second";K=60;Metrics=(Metrics $fused);Queries=$fused}
}
$rows=@($queries | ForEach-Object {
    [ordered]@{ Id=$_.Id;Style=$_.Style;Split=$_.Split;Text=$_.Text;ExpectedFile=$_.File;Evidence=$_.ExpectedEvidence
        Rank=$_.Rank;FileRank=$_.FileRank;Characters=$_.Characters;Seconds=$_.Seconds;Error=$_.Error
        Utf8Bytes=[Text.Encoding]::UTF8.GetByteCount($_.Text)
        ReturnedFiles=@($_.Results | ForEach-Object File)
    }
})
[ordered]@{
    Timestamp=$report.Timestamp;EmbeddingModel=$report.EmbeddingModel;ChatModel=$report.ChatModel
    Configuration=$report.Configuration;Ingestion=$report.cycle;IngestionChatCalls=$report.IngestionChatCalls
    IngestionEmbeddingCalls=$report.IngestionEmbeddingCalls;TotalChatSeconds=$report.ChatSeconds
    Metrics=$metrics;Subsets=$subsets;ExploratoryFusion=$fusions;Queries=$rows
} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$metrics | ForEach-Object { [pscustomobject]@{Style=$_.Style;Top1=$_.Metrics.EvidenceAt1;Top5=$_.Metrics.EvidenceAt5;MRR=$_.Metrics.MrrAt5;Errors=$_.Metrics.Errors} } | Format-Table
Write-Output "Content-free results saved: $OutputPath"
