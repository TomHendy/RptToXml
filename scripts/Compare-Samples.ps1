<#
.SYNOPSIS
	Regression check for RptToXml: converts the sample reports with a baseline build and a candidate build,
	checks the candidate, and reports how the candidate's XML differs from the baseline's.

.DESCRIPTION
	Both builds convert a copy of the samples with "-r --ignore-errors"; the candidate does it twice.
	A check fails when the candidate:
	  - fails on a sample, or writes XML that does not parse, where the baseline converts the sample
	  - ends with a non-zero exit code although no sample failed, or with 0 although one did
	  - reports errors inside reports ("Error ..." on standard error) where the baseline reports none
	  - writes different bytes in its two runs for a file that the baseline writes the same twice
	  - with --stdout does not write exactly the bytes of the file output without the BOM and the FileName attribute
	  - does not answer a missing or unreadable input file with a non-zero exit code and a message on
	    standard error (and, with --stdout, nothing on standard output)
	A problem that the baseline has as well is listed as a warning, not as a failure.
	Then the two outputs are compared. summary.txt lists every kind of difference with the number of times it
	occurs: REMOVED, CHANGED and REORDERED entries alter output that the baseline wrote and must be reviewed,
	"added" entries are new output. baseline-vs-candidate.diff is the text diff (needs git on PATH).
	Differences alone never fail the check.
	To see only the changes that are being worked on, use a build of the last commit as the baseline; a
	released build also shows what the commits since that release changed.
	Requires the Crystal Reports runtime (see README). Exit code: 0 = no check failed, 1 = a check failed.

.PARAMETER Baseline
	RptToXml.exe of the earlier build.

.PARAMETER Candidate
	RptToXml.exe of the build to check.

.PARAMETER Samples
	Folder with the .rpt files to convert. Nothing is written to it.

.PARAMETER OutDir
	Folder for the results. The results of an earlier run are replaced; other files in it are left alone.

.EXAMPLE
	.\scripts\Compare-Samples.ps1 -Baseline C:\tools\RptToXml-1.1.7\RptToXml.exe -Candidate .\RptToXml\bin\Debug\RptToXml.exe

.EXAMPLE
	powershell -ExecutionPolicy Bypass -File scripts\Compare-Samples.ps1 -Baseline ..\RptToXml-head\RptToXml\bin\Debug\RptToXml.exe -Candidate RptToXml\bin\Debug\RptToXml.exe
#>
param(
	[Parameter(Mandatory = $true)] [string] $Baseline,
	[Parameter(Mandatory = $true)] [string] $Candidate,
	[string] $Samples = (Join-Path $PSScriptRoot '..\RptToXml\Samples'),
	[string] $OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'RptToXml-compare')
)

$ErrorActionPreference = 'Stop'
$Baseline = (Resolve-Path -LiteralPath $Baseline).ProviderPath
$Candidate = (Resolve-Path -LiteralPath $Candidate).ProviderPath
$Samples = (Resolve-Path -LiteralPath $Samples).ProviderPath
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$sampleFiles = @(Get-ChildItem -LiteralPath $Samples | Where-Object { -not $_.PSIsContainer -and $_.Extension -eq '.rpt' } | Sort-Object Name)
if ($sampleFiles.Count -eq 0) { throw "No .rpt files in $Samples" }

# Only what this script writes is removed from $OutDir.
[void][IO.Directory]::CreateDirectory($OutDir)
$runNames = 'baseline', 'baseline-run2', 'baseline-alone', 'candidate', 'candidate-run2', 'candidate-stdout', 'candidate-errors'
foreach ($name in @($runNames) + @($runNames | ForEach-Object { "$_.stdout.log"; "$_.stderr.log" }) + 'baseline-vs-candidate.diff', 'summary.txt') {
	$path = Join-Path $OutDir $name
	if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

# Runs exe with arguments in a working directory; returns exit code, stdout bytes and stderr text.
function Invoke-Tool([string] $exe, [string] $arguments, [string] $workingDirectory) {
	$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
	$psi.Arguments = $arguments
	$psi.WorkingDirectory = $workingDirectory
	$psi.UseShellExecute = $false
	$psi.RedirectStandardOutput = $true
	$psi.RedirectStandardError = $true
	$process = [System.Diagnostics.Process]::Start($psi)
	$stderrTask = $process.StandardError.ReadToEndAsync()
	$stdout = New-Object System.IO.MemoryStream
	$process.StandardOutput.BaseStream.CopyTo($stdout)
	$process.WaitForExit()
	[pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.ToArray(); Stderr = $stderrTask.Result }
}

# Runs exe with arguments in $OutDir\<name> on copies of the given sample files. A report is always converted as
# ".\<file>.rpt", because the path it is given ends up in the XML. Returns the directory, the exit code, the stdout
# bytes and the lines written to stdout and stderr; both streams are also saved as $OutDir\<name>.*.log.
function Convert-Samples([string] $exe, [string] $name, $files, [string] $arguments) {
	$dir = Join-Path $OutDir $name
	[void][IO.Directory]::CreateDirectory($dir)
	foreach ($file in $files) { [IO.File]::Copy($file.FullName, (Join-Path $dir $file.Name), $true) }
	$result = Invoke-Tool $exe $arguments $dir
	foreach ($file in $files) { Remove-Item -LiteralPath (Join-Path $dir $file.Name) -Force -ErrorAction SilentlyContinue }
	[IO.File]::WriteAllBytes((Join-Path $OutDir "$name.stdout.log"), $result.Stdout)
	[IO.File]::WriteAllText((Join-Path $OutDir "$name.stderr.log"), $result.Stderr)
	[pscustomobject]@{
		Dir = $dir
		ExitCode = $result.ExitCode
		Stdout = $result.Stdout
		StdoutLines = @([Console]::OutputEncoding.GetString($result.Stdout) -split "`r?`n")
		StderrLines = @($result.Stderr -split "`r?`n")
	}
}

function Convert-AllSamples([string] $exe, [string] $name) {
	$run = Convert-Samples $exe $name $sampleFiles '-r --ignore-errors'
	$count = @(Get-ChildItem -LiteralPath $run.Dir -Filter *.xml).Count
	Write-Host ("{0}: exit code {1}, {2} xml files for {3} samples" -f $name, $run.ExitCode, $count, $sampleFiles.Count)
	$run
}

# Loads an XML file; Document stays $null and Error is set when the file is not well-formed.
function Read-Xml([string] $path) {
	$result = [pscustomobject]@{ Document = $null; Error = $null }
	$document = New-Object System.Xml.XmlDocument
	$document.PreserveWhitespace = $true
	$stream = [IO.File]::OpenRead($path)
	try { $document.Load($stream); $result.Document = $document }
	catch { $result.Error = $_.Exception.GetBaseException().Message }
	finally { $stream.Dispose() }
	$result
}

# Whether two files have the same bytes; two missing files count as the same.
function Test-SameFile([string] $path1, [string] $path2) {
	$exists1 = Test-Path -LiteralPath $path1
	$exists2 = Test-Path -LiteralPath $path2
	if (-not $exists1 -or -not $exists2) { return $exists1 -eq $exists2 }
	(Get-FileHash -LiteralPath $path1).Hash -eq (Get-FileHash -LiteralPath $path2).Hash
}

# Whether the baseline fails on a sample as well. An earlier build run with --ignore-errors reports a failure only as
# a line on stdout that does not name the report, so the sample is converted alone and without that option: a
# failure then shows as an exception on stderr, a non-zero exit code or a missing .xml.
function Test-BaselineFails($sample) {
	$xmlName = [IO.Path]::ChangeExtension($sample.Name, '.xml')
	if (-not (Test-Path -LiteralPath (Join-Path $baselineRun.Dir $xmlName))) { return $true }
	$run = Convert-Samples $Baseline 'baseline-alone' @($sample) ('".\{0}"' -f $sample.Name)
	$run.ExitCode -ne 0 -or ($run.StderrLines -join '').Trim().Length -gt 0 -or -not (Test-Path -LiteralPath (Join-Path $run.Dir $xmlName))
}

# A value shortened to one line for a message.
function Get-Short([string] $text) {
	$text = ($text -replace '\s+', ' ').Trim()
	if ($text.Length -gt 70) { $text.Substring(0, 67) + '...' } else { $text }
}

# An example of a changed value for the summary.
function Get-Example([string] $old, [string] $new) {
	if (($old -replace '\s+', ' ').Trim() -ceq ($new -replace '\s+', ' ').Trim()) { return 'differs in white space or line breaks only' }
	'"{0}" -> "{1}"' -f (Get-Short $old), (Get-Short $new)
}

# Differences between the two outputs, counted per kind and element path.
$changes = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$currentFile = ''

function Add-Change([string] $kind, [string] $path, [string] $example) {
	$key = "$kind $path"
	if (-not $changes.ContainsKey($key)) {
		$files = New-Object 'System.Collections.Generic.HashSet[string]'
		$changes[$key] = [pscustomobject]@{ Kind = $kind; Path = $path; Total = 0; Files = $files; Example = (Get-Short $example) }
	}
	$changes[$key].Total++
	[void]$changes[$key].Files.Add($currentFile)
}

# Compares an element of the baseline output with its counterpart in the candidate output. Child elements are paired
# by tag name, Name attribute and position among the children that share both.
function Compare-Element($old, $new, [string] $path) {
	if ($old.get_OuterXml() -ceq $new.get_OuterXml()) { return }
	$oldCommon = ''
	foreach ($attribute in $old.get_Attributes()) {
		$name = $attribute.get_Name()
		$value = $attribute.get_Value()
		if (-not $new.HasAttribute($name)) { Add-Change 'REMOVED attribute' "$path/@$name" $value; continue }
		$oldCommon += "$name "
		$newValue = $new.GetAttribute($name)
		if ($newValue -cne $value) { Add-Change 'CHANGED attribute' "$path/@$name" (Get-Example $value $newValue) }
	}
	$newCommon = ''
	foreach ($attribute in $new.get_Attributes()) {
		$name = $attribute.get_Name()
		if ($old.HasAttribute($name)) { $newCommon += "$name " } else { Add-Change 'added attribute' "$path/@$name" $attribute.get_Value() }
	}
	if ($oldCommon -cne $newCommon) { Add-Change 'REORDERED attributes' $path '' }

	$newChildren = New-Object 'System.Collections.Generic.Dictionary[string,object]'
	$newKeys = New-Object System.Collections.Generic.List[string]
	$positions = New-Object 'System.Collections.Generic.Dictionary[string,int]'
	foreach ($child in $new.get_ChildNodes()) {
		if ($child.get_NodeType() -ne [System.Xml.XmlNodeType]::Element) { continue }
		$key = $child.get_LocalName() + ' ' + $child.GetAttribute('Name')
		$position = 0
		[void]$positions.TryGetValue($key, [ref] $position)
		$positions[$key] = $position + 1
		$newChildren["$key $position"] = $child
		$newKeys.Add("$key $position")
	}
	$positions.Clear()
	$paired = New-Object 'System.Collections.Generic.HashSet[string]'
	$oldPaired = ''
	$oldIsLeaf = $true
	foreach ($child in $old.get_ChildNodes()) {
		if ($child.get_NodeType() -ne [System.Xml.XmlNodeType]::Element) { continue }
		$oldIsLeaf = $false
		$key = $child.get_LocalName() + ' ' + $child.GetAttribute('Name')
		$position = 0
		[void]$positions.TryGetValue($key, [ref] $position)
		$positions[$key] = $position + 1
		$key = "$key $position"
		$childPath = $path + '/' + $child.get_LocalName()
		if ($newChildren.ContainsKey($key)) {
			[void]$paired.Add($key)
			$oldPaired += "$key|"
			Compare-Element $child $newChildren[$key] $childPath
		}
		else { Add-Change 'REMOVED element' $childPath $child.GetAttribute('Name') }
	}
	$newPaired = ''
	foreach ($key in $newKeys) {
		if ($paired.Contains($key)) { $newPaired += "$key|" }
		else { Add-Change 'added element' ($path + '/' + $newChildren[$key].get_LocalName()) $newChildren[$key].GetAttribute('Name') }
	}
	if ($oldPaired -cne $newPaired) { Add-Change 'REORDERED elements' $path '' }

	# what is left: the text of an element without child elements, and differences in form only
	$whitespace = [System.Xml.XmlNodeType]::Whitespace
	if ($oldIsLeaf -and $newKeys.Count -eq 0) {
		if ($old.get_InnerText() -cne $new.get_InnerText()) { Add-Change 'CHANGED text' $path (Get-Example $old.get_InnerText() $new.get_InnerText()) }
		elseif ($old.get_IsEmpty() -ne $new.get_IsEmpty()) { Add-Change 'CHANGED form' $path 'empty element written as <x /> by one build and as <x></x> by the other' }
	}
	elseif (-not $oldIsLeaf -and $newKeys.Count -gt 0 -and ($old.get_FirstChild().get_NodeType() -eq $whitespace) -ne ($new.get_FirstChild().get_NodeType() -eq $whitespace)) {
		Add-Change 'CHANGED form' $path 'child elements on separate indented lines in one build only'
	}
}

# What a file has before and after its root element: BOM, XML declaration and line breaks.
function Get-Wrapper([string] $path) {
	$text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($path))
	if ($text -match '(?s)^(?<before>.*?)<[A-Za-z].*>(?<after>[^>]*)$') { $Matches['before'] + '|' + $Matches['after'] } else { $text }
}

if ($Baseline -eq $Candidate) { $warnings.Add('Baseline and Candidate are the same file') }
Write-Host "Converting $($sampleFiles.Count) samples into $OutDir (three runs, this can take several minutes)"
$baselineRun = Convert-AllSamples $Baseline 'baseline'
$candidateRun = Convert-AllSamples $Candidate 'candidate'
$candidateRun2 = Convert-AllSamples $Candidate 'candidate-run2'

if (@(Get-ChildItem -LiteralPath $baselineRun.Dir -Filter *.xml).Count -eq 0) {
	throw "The baseline converted no sample, so there is nothing to compare with (see baseline.stdout.log and baseline.stderr.log in $OutDir). Is the Crystal Reports runtime installed?"
}

# What the candidate wrote to stderr: ".\<file>.rpt: <message>" is a report that failed, "Error ..." is a part of a
# report that was skipped; indented lines (stack traces) continue the line before them.
$failedReports = @{}
$candidateErrors = New-Object System.Collections.Generic.List[string]
$otherStderr = New-Object System.Collections.Generic.List[string]
foreach ($line in $candidateRun.StderrLines) {
	if ($line -match '^(?<path>\.[\\/].+?\.rpt): (?<message>.*)$') { $failedReports[($Matches['path'] -split '[\\/]')[-1]] = $Matches['message'] }
	elseif ($line -match '^Error ') { $candidateErrors.Add($line) }
	elseif ($line -match '^\S') { $otherStderr.Add($line) }
}
foreach ($name in $failedReports.Keys) {
	if (@($sampleFiles | Where-Object { $_.Name -eq $name }).Count -eq 0) { $failures.Add("the candidate's stderr names a failed report that is not one of the samples: $name") }
}

# every sample converted, and every file is well-formed XML
$converted = New-Object System.Collections.Generic.List[object]
$compared = New-Object System.Collections.Generic.List[object]
foreach ($sample in $sampleFiles) {
	$xmlName = [IO.Path]::ChangeExtension($sample.Name, '.xml')
	$candidateXml = Join-Path $candidateRun.Dir $xmlName
	$baselineXml = Join-Path $baselineRun.Dir $xmlName
	$problem = $null
	if ($failedReports.ContainsKey($sample.Name)) { $problem = "fails to convert ($($failedReports[$sample.Name]))" }
	elseif (-not (Test-Path -LiteralPath $candidateXml)) { $problem = 'gives no .xml and no error message' }
	if ($problem) {
		if (Test-BaselineFails $sample) { $warnings.Add("$($sample.Name) $problem; the baseline fails on it as well") }
		else { $failures.Add("$($sample.Name) $problem; the baseline converts it") }
		continue
	}
	$new = Read-Xml $candidateXml
	$old = if (Test-Path -LiteralPath $baselineXml) { Read-Xml $baselineXml } else { $null }
	if ($null -eq $new.Document) {
		if ($null -ne $old -and $null -eq $old.Document) { $warnings.Add("not well-formed with either build: $xmlName ($($new.Error))") }
		else { $failures.Add("not well-formed: $candidateXml ($($new.Error))") }
		continue
	}
	$converted.Add($sample)
	if ($null -eq $old) { $warnings.Add("$($sample.Name) is converted by the candidate only") }
	elseif ($null -eq $old.Document) { $warnings.Add("baseline output is not well-formed and was not compared: $xmlName ($($old.Error))") }
	else { $compared.Add([pscustomobject]@{ Name = $xmlName; Old = $old.Document; New = $new.Document }) }
}
if ($converted.Count -eq 0) { $failures.Add('the candidate converted no sample at all (is the Crystal Reports runtime installed?)') }

# exit code and messages of the conversion run
if ($failedReports.Count -eq 0 -and $candidateRun.ExitCode -ne 0) { $failures.Add("candidate exit code $($candidateRun.ExitCode) although no report is named as failed on stderr (see candidate.stderr.log)") }
if ($failedReports.Count -gt 0 -and $candidateRun.ExitCode -eq 0) { $failures.Add('candidate exit code 0 although reports failed') }
# "Error reading ..." on stderr: data that only the candidate dumps could not be read and was left out.
# Other "Error ..." lines: part of a report failed; an earlier build writes these to stdout.
$readErrors = @($candidateErrors | Where-Object { $_ -match '^Error reading ' })
$baselineReadErrors = @($baselineRun.StderrLines | Where-Object { $_ -match '^Error reading ' })
if ($readErrors.Count -gt $baselineReadErrors.Count) { $failures.Add("the candidate could not read $($readErrors.Count) parts of reports and left them out (lines starting with 'Error reading' in candidate.stderr.log), first: $(Get-Short $readErrors[0])") }
$otherErrors = @($candidateErrors | Where-Object { $_ -notmatch '^Error reading ' })
$baselineErrors = @($baselineRun.StdoutLines + $baselineRun.StderrLines | Where-Object { $_ -match '^Error (?!reading )' })
if ($otherErrors.Count -gt $baselineErrors.Count) {
	$text = "the candidate reports $($otherErrors.Count) errors inside reports, the baseline $($baselineErrors.Count) (lines starting with 'Error' in candidate.stderr.log and baseline.stdout.log), first: $(Get-Short $otherErrors[0])"
	if ($baselineErrors.Count -eq 0) { $failures.Add($text) } else { $warnings.Add($text) }
}
if ($otherStderr.Count -gt 0) { $warnings.Add("$($otherStderr.Count) other lines on the candidate's stderr (see candidate.stderr.log), first: $(Get-Short $otherStderr[0])") }
# settings that the candidate reports as unreadable on stdout and leaves out
$skipped = @($candidateRun.StdoutLines | Where-Object { $_ -match '^Error reading ' })
$baselineSkipped = @($baselineRun.StdoutLines | Where-Object { $_ -match '^Error reading ' })
if ($skipped.Count -gt $baselineSkipped.Count) { $warnings.Add("the candidate left out $($skipped.Count) settings it could not read (lines starting with 'Error reading' in candidate.stdout.log), first: $(Get-Short $skipped[0])") }

# deterministic: two runs give identical bytes
$names = @(@(Get-ChildItem -LiteralPath $candidateRun.Dir -Filter *.xml) + @(Get-ChildItem -LiteralPath $candidateRun2.Dir -Filter *.xml) | ForEach-Object { $_.Name } | Sort-Object -Unique)
$unstable = @($names | Where-Object { -not (Test-SameFile (Join-Path $candidateRun.Dir $_) (Join-Path $candidateRun2.Dir $_)) })
if ($unstable.Count -gt 0) {
	Write-Host "The candidate's two runs differ; converting with the baseline again to see whether the baseline is stable"
	$baselineRun2 = Convert-AllSamples $Baseline 'baseline-run2'
	foreach ($name in $unstable) {
		if (Test-SameFile (Join-Path $baselineRun.Dir $name) (Join-Path $baselineRun2.Dir $name)) { $failures.Add("differs between two runs of the candidate: $name") }
		else { $warnings.Add("differs between two runs, with the baseline as well: $name") }
	}
}

# --stdout: UTF-8 without BOM, utf-8 declaration, same content as the file output except for the FileName attribute
# (checked for the first sample whose output has non-ASCII text, because that is where the encoding matters)
if ($converted.Count -gt 0) {
	$sample = $converted[0]
	foreach ($other in $converted) {
		if ([IO.File]::ReadAllText((Join-Path $candidateRun.Dir ([IO.Path]::ChangeExtension($other.Name, '.xml')))) -match '[^\x00-\x7F]') { $sample = $other; break }
	}
	$run = Convert-Samples $Candidate 'candidate-stdout' @($sample) ('--stdout ".\{0}"' -f $sample.Name)
	$stdoutBytes = $run.Stdout
	$fileBytes = [IO.File]::ReadAllBytes((Join-Path $candidateRun.Dir ([IO.Path]::ChangeExtension($sample.Name, '.xml'))))
	if ($run.ExitCode -ne 0) { $failures.Add("--stdout exit code $($run.ExitCode): $(Get-Short ($run.StderrLines -join ' '))") }
	if ($stdoutBytes.Length -ge 3 -and $stdoutBytes[0] -eq 0xEF -and $stdoutBytes[1] -eq 0xBB -and $stdoutBytes[2] -eq 0xBF) { $failures.Add('--stdout output starts with a BOM') }
	if (-not [Text.Encoding]::UTF8.GetString($stdoutBytes).StartsWith('<?xml version="1.0" encoding="utf-8"?>')) { $failures.Add('--stdout output does not start with the utf-8 XML declaration') }
	# the file output without its BOM and without the FileName attribute of the root element, which --stdout leaves out
	$bomLength = if ($fileBytes.Length -ge 3 -and $fileBytes[0] -eq 0xEF -and $fileBytes[1] -eq 0xBB -and $fileBytes[2] -eq 0xBF) { 3 } else { 0 }
	$expectedText = ([regex] ' FileName="[^"]*"').Replace([Text.Encoding]::UTF8.GetString($fileBytes, $bomLength, $fileBytes.Length - $bomLength), '', 1)
	$expectedBytes = (New-Object System.Text.UTF8Encoding $false).GetBytes($expectedText)
	if ([Convert]::ToBase64String($expectedBytes) -cne [Convert]::ToBase64String($stdoutBytes)) {
		$failures.Add("--stdout output differs from the file output without the FileName attribute for $($sample.Name) ($($stdoutBytes.Length) bytes on stdout, $($expectedBytes.Length) expected; see candidate-stdout.stdout.log)")
	}
}

# errors: non-zero exit code and a message on stderr; with --stdout nothing on stdout, where it would end up in a diff
$errorsDir = Join-Path $OutDir 'candidate-errors'
[void][IO.Directory]::CreateDirectory($errorsDir)
[IO.File]::WriteAllText((Join-Path $errorsDir 'not-a-report.rpt'), 'This is not a report.')
foreach ($case in @(
		@('a missing input file', '"does-not-exist.rpt"'),
		@('a missing input file with --stdout', '--stdout "does-not-exist.rpt"'),
		@('an unreadable report with --stdout', '--stdout ".\not-a-report.rpt"'),
		@('an unreadable report with --stdout --ignore-errors', '--stdout --ignore-errors ".\not-a-report.rpt"'),
		@('an unreadable report with -r --ignore-errors', '-r --ignore-errors'))) {
	$result = Invoke-Tool $Candidate $case[1] $errorsDir
	if ($result.ExitCode -eq 0) { $failures.Add("$($case[0]) gives exit code 0") }
	if ($result.Stderr.Trim().Length -eq 0) { $failures.Add("$($case[0]) gives no message on stderr") }
	if ($case[1] -match '--stdout' -and $result.Stdout.Length -ne 0) { $failures.Add("$($case[0]) writes $($result.Stdout.Length) bytes to stdout") }
	# the check for failed reports above relies on this format
	if ($case[1] -eq '-r --ignore-errors' -and $result.Stderr -notmatch '(?m)^\.[\\/]not-a-report\.rpt: ') { $failures.Add("$($case[0]) is not reported as '.\not-a-report.rpt: <message>' on stderr but as: $(Get-Short $result.Stderr)") }
}

# baseline vs candidate
Write-Host 'Comparing the outputs'
$names = @(@(Get-ChildItem -LiteralPath $baselineRun.Dir -Filter *.xml) + @(Get-ChildItem -LiteralPath $candidateRun.Dir -Filter *.xml) | ForEach-Object { $_.Name } | Sort-Object -Unique)
$different = @($names | Where-Object { -not (Test-SameFile (Join-Path $baselineRun.Dir $_) (Join-Path $candidateRun.Dir $_)) })
foreach ($pair in $compared) {
	if ($different -notcontains $pair.Name) { continue }
	$currentFile = $pair.Name
	$totalBefore = 0
	foreach ($change in $changes.Values) { $totalBefore += $change.Total }
	Compare-Element $pair.Old.get_DocumentElement() $pair.New.get_DocumentElement() $pair.Old.get_DocumentElement().get_LocalName()
	if ((Get-Wrapper (Join-Path $baselineRun.Dir $pair.Name)) -cne (Get-Wrapper (Join-Path $candidateRun.Dir $pair.Name))) { Add-Change 'CHANGED form' '(BOM, XML declaration or line breaks around the root element)' '' }
	$totalAfter = 0
	foreach ($change in $changes.Values) { $totalAfter += $change.Total }
	if ($totalAfter -eq $totalBefore) { Add-Change 'CHANGED form' '(same elements, attributes and text; see the text diff)' '' }
}
$review = @($changes.Values | Where-Object { $_.Kind -cmatch '^[A-Z]' } | Sort-Object Kind, Path)
$added = @($changes.Values | Where-Object { $_.Kind -cnotmatch '^[A-Z]' } | Sort-Object Kind, Path)

$report = New-Object System.Collections.Generic.List[string]
$report.Add("$($names.Count) output files, $($different.Count) differ between baseline and candidate, $($compared.Count) were compared element by element.")
$report.Add("Changes to output that the baseline wrote ($($review.Count) kinds):")
foreach ($change in $review) { $report.Add(("  {0,-20} {1}  [{2} x in {3} files]  {4}" -f $change.Kind, $change.Path, $change.Total, $change.Files.Count, $change.Example).TrimEnd()) }
$report.Add("New output ($($added.Count) kinds):")
foreach ($change in $added) { $report.Add(("  {0,-20} {1}  [{2} x in {3} files]  {4}" -f $change.Kind, $change.Path, $change.Total, $change.Files.Count, $change.Example).TrimEnd()) }
Write-Host ''
$report | Select-Object -First 60 | ForEach-Object { Write-Host $_ }
if ($report.Count -gt 60) { Write-Host "  ... ($($report.Count - 60) more lines in summary.txt)" }

# The text diff is written by git itself: piped through PowerShell, the UTF-8 text would be decoded in the console
# code page. Exit code 1 only means that there are differences.
$diffPath = Join-Path $OutDir 'baseline-vs-candidate.diff'
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $git) { $diffPath = 'not written, git is not on PATH (compare the baseline and candidate folders with any diff tool)' }
else {
	$result = Invoke-Tool $git.Path '-c core.autocrlf=false -c core.quotepath=false diff --no-index --no-color --no-ext-diff --output=baseline-vs-candidate.diff baseline candidate' $OutDir
	if ($result.ExitCode -gt 1 -or $result.Stderr -match '(?m)^(error|fatal):') { $warnings.Add("git diff: exit code $($result.ExitCode) $(Get-Short $result.Stderr)") }
}

$lost = @($review | Where-Object { $_.Kind -cmatch '^(REMOVED|REORDERED)' })
if ($failures.Count -gt 0) { $verdict = "FAILED: $($failures.Count) checks failed." }
elseif ($lost.Count -gt 0) { $verdict = "No check failed, but the candidate REMOVES or REORDERS output that the baseline wrote ($($lost.Count) kinds above): confirm that this is intended." }
elseif ($review.Count -gt 0) { $verdict = "No check failed. The candidate changes output that the baseline wrote ($($review.Count) CHANGED kinds above): confirm that each is intended." }
elseif ($different.Count -gt 0) { $verdict = 'All checks passed. The candidate only adds output.' }
else { $verdict = 'All checks passed. Baseline and candidate output are identical.' }

$report.Insert(0, "Baseline:  $Baseline (built $((Get-Item -LiteralPath $Baseline).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))")
$report.Insert(1, "Candidate: $Candidate (built $((Get-Item -LiteralPath $Candidate).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))")
$report.Add('Files that differ:')
foreach ($name in $different) { $report.Add("  $name") }
foreach ($warning in $warnings) { $report.Add("WARNING: $warning") }
foreach ($failure in $failures) { $report.Add("FAILED CHECK: $failure") }
$report.Add($verdict)
$summaryPath = Join-Path $OutDir 'summary.txt'
[IO.File]::WriteAllLines($summaryPath, $report.ToArray(), [Text.Encoding]::UTF8)
Write-Host ''
Write-Host "Summary:   $summaryPath"
Write-Host "Text diff: $diffPath"
Write-Host "Logs:      $(Join-Path $OutDir '*.log')"

Write-Host ''
foreach ($warning in $warnings) { Write-Host "WARNING: $warning" -ForegroundColor Yellow }
if ($failures.Count -gt 0) {
	Write-Host "FAILED checks ($($failures.Count)):" -ForegroundColor Red
	foreach ($failure in $failures) { Write-Host "  $failure" -ForegroundColor Red }
	exit 1
}
if ($review.Count -gt 0) { Write-Host $verdict -ForegroundColor Yellow } else { Write-Host $verdict -ForegroundColor Green }
exit 0
