<#
.SYNOPSIS
  Lists the tool calls a Cordyceps / Hyperceps server received, read from its own log.

.DESCRIPTION
  For the step 8 behaviour checks: did the agent use gh_batch, and how many calls did it make?
  Run with -Reset just before a check (clears the log), then without it afterwards.

  The log keeps the last 500 lines (about 120 calls). For longer runs, use the component's
  "Commands received" count for the total.

  Reading the log is itself one call to the server (it adds 1 to "Commands received") and is
  left out of the list.

.EXAMPLE
  .\tests\tool-calls.ps1 -Reset        # before a check
  .\tests\tool-calls.ps1               # after it
  .\tests\tool-calls.ps1 -Port 26929   # the original Cordyceps
#>
param(
    [int]$Port = 26930,
    [switch]$Reset
)

$headers = @{ Accept = "application/json" }
$body = @{
    jsonrpc = "2.0"; id = 1; method = "tools/call"
    params = @{ name = "gh_inspect"; arguments = @{ action = "log"; limit = 500; clear = $(if ($Reset) { "true" } else { "false" }) } }
} | ConvertTo-Json -Depth 10 -Compress

try {
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/mcp" -Method Post -Headers $headers -ContentType "application/json" -Body $body -TimeoutSec 30
} catch {
    Write-Output "Nothing is answering on port $Port. Is Rhino open with the component on a canvas?"
    exit 1
}
$log = $r.result.content[0].text | ConvertFrom-Json

if ($Reset) {
    Write-Output "Log cleared on port $Port. Run the check, then run this script again without -Reset."
    exit 0
}

$calls = @()
foreach ($e in $log.entries) {
    if ($e.message -notmatch '^Received: .*"tools/call"') { continue }
    # The log keeps the first 200 characters of each request; the tool name is normally near the start.
    $name = if ($e.message -match '"name"\s*:\s*"([^"]+)"') { $Matches[1] } else { "(name cut off in the log)" }
    $action = if ($e.message -match '"action"\s*:\s*"([^"]+)"') { $Matches[1] } else { "" }
    $calls += [pscustomobject]@{ Time = $e.time; Tool = $name; Action = $action }
}
# The last call is this script reading the log.
if ($calls.Count -gt 0 -and $calls[-1].Tool -eq "gh_inspect" -and $calls[-1].Action -eq "log") {
    $calls = if ($calls.Count -gt 1) { @($calls[0..($calls.Count - 2)]) } else { @() }
}

if ($calls.Count -eq 0) {
    Write-Output "No tool calls in the log since it was last cleared."
    exit 0
}

Write-Output "Tool calls on port $Port, oldest first:"
$i = 0
foreach ($c in $calls) {
    $i++
    Write-Output ("{0,3}. {1}  {2,-12} {3}" -f $i, $c.Time, $c.Tool, $c.Action)
}

Write-Output ""
Write-Output "Totals:"
$calls | Group-Object { if ($_.Action) { "$($_.Tool) $($_.Action)" } else { $_.Tool } } |
    Sort-Object Count -Descending |
    ForEach-Object { Write-Output ("  {0,3} x {1}" -f $_.Count, $_.Name) }

$batches = @($log.entries | Where-Object { $_.message -like "gh_batch *" })
if ($batches.Count -gt 0) {
    Write-Output ""
    Write-Output "gh_batch results:"
    foreach ($b in $batches) { Write-Output "  $($b.time)  $($b.message)" }
}
