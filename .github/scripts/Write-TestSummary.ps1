# Writes a short test summary (passed / failed / skipped, plus the names of failed tests) to the GitHub Actions job
# summary, from the .trx files that `dotnet test --logger trx` wrote. Used by ci.yml and release.yml.
#
#   ./.github/scripts/Write-TestSummary.ps1 -ResultsDirectory TestResults
#
# Runs even when tests failed (the workflows call it with `if: always()`), so it never fails the job itself;
# the `dotnet test` step already did that.

param(
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [string] $SummaryPath = $env:GITHUB_STEP_SUMMARY
)

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('## Tests')
$lines.Add('')

$trxFiles = @(Get-ChildItem -Path $ResultsDirectory -Filter *.trx -Recurse -ErrorAction SilentlyContinue)
if ($trxFiles.Count -eq 0) {
    $lines.Add('No test results were found. The build or test run probably failed before any test ran; see the log.')
}
else {
    # One UnitTestResult per test case (each [Theory] row counts separately). Outcomes: Passed, Failed, NotExecuted
    # (skipped), and rarer ones such as Timeout or Aborted, which count as failed here.
    $results = foreach ($file in $trxFiles) { ([xml](Get-Content -LiteralPath $file.FullName -Raw)).TestRun.Results.UnitTestResult }
    $results = @($results | Where-Object { $_ })
    $passed = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
    $skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
    $failedTests = @($results | Where-Object { $_.outcome -notin 'Passed', 'NotExecuted' })

    $lines.Add('| Passed | Failed | Skipped | Total |')
    $lines.Add('|---:|---:|---:|---:|')
    $lines.Add("| $passed | $($failedTests.Count) | $skipped | $($results.Count) |")

    if ($failedTests.Count -gt 0) {
        $lines.Add('')
        $lines.Add('### Failed')
        $lines.Add('')
        foreach ($test in $failedTests | Select-Object -First 50) {
            $lines.Add("- ``$($test.testName)`` ($($test.outcome))")
        }
        if ($failedTests.Count -gt 50) {
            $lines.Add("- ... and $($failedTests.Count - 50) more (see the test-results artifact)")
        }
    }
}

$text = ($lines -join "`n") + "`n"
if ($SummaryPath) {
    [System.IO.File]::AppendAllText($SummaryPath, $text)
}
Write-Output $text
