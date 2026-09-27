# ============================================================
#  CMover end-to-end test.
#
#  ASCII only ON PURPOSE: PowerShell 5.1 reads a .ps1 that has no
#  UTF-8 BOM as GBK and silently mangles non-ASCII literals.
#
#  The falsification cases matter as much as the happy path:
#  a verifier that never says "no" is not a verifier.
# ============================================================
$ErrorActionPreference = 'Stop'

$csc   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# Everything below is DERIVED, never hardcoded to one machine. Whoever clones
# this repo has to be able to run the suite as-is: a hardcoded profile name
# turns the KB fixtures into tests of a path that does not exist.
$proj   = Split-Path -Parent $PSScriptRoot
$exe    = "$proj\build\cmover.exe"
$prof   = $env:USERPROFILE
$unameL = (Split-Path $prof -Leaf).ToLower()
$base   = Join-Path $prof "cmover-test"
$src    = "$base\FakeGame"
$tgt    = "$proj\test-target"
$new    = "$tgt\FakeGame"
$recs   = "$env:LOCALAPPDATA\CMover\records"
$floorRoot = Join-Path $prof "cmover-test-floor"
$sdSrc = "$base\sd-probe"

$nPass = 0; $nFail = 0
function Check($name, $ok, $detail) {
  if ($ok) { $script:nPass++; Write-Host ("  PASS   " + $name) }
  else     { $script:nFail++; Write-Host ("  FAIL   " + $name + "    -> " + $detail) }
}
function RunCli($argv) {
  # Start-Process -ArgumentList does NOT quote for you: a path with a space gets
  # split into two argv entries, and the exe silently answers about "C:\Program".
  # That looks like a tool bug but is a harness bug -- quote here.
  $q = @()
  foreach ($x in $argv) { if ($x -match '\s') { $q += ('"' + $x + '"') } else { $q += $x } }
  $p = Start-Process -FilePath $exe -ArgumentList $q -Wait -PassThru -NoNewWindow
  return $p.ExitCode
}
# RunCli can only ever hand back an exit code, so on its own it proves NOTHING about
# whether the CLI prints a single character. Every exit-code check in this file would
# stay green if --kb/--scan/--records printed silence. That is a whole dimension of
# "is this thing usable" with zero coverage, so capture stdout for real.
#
# The exe is a Windows-subsystem binary (one exe serves both GUI and CLI), which is
# exactly the shape where output silently goes nowhere: it calls AttachConsole(-1)
# and hopes a parent console exists. Measured: piping ("& $exe ... | Out-String")
# and this Start-Process form both work; "cmd /c exe > file" does NOT and yields 0
# bytes, because cmd cannot start from this session's WSL UNC cwd. So the capture
# method itself is a variable, and this function pins the working one.
function RunCliCapture($argv) {
  $q = @()
  foreach ($x in $argv) { if ($x -match '\s') { $q += ('"' + $x + '"') } else { $q += $x } }
  $o = "$base\cli-out.txt"; $er = "$base\cli-err.txt"
  $p = Start-Process -FilePath $exe -ArgumentList $q -Wait -PassThru -NoNewWindow `
       -RedirectStandardOutput $o -RedirectStandardError $er
  $script:LastCliOut = [System.IO.File]::ReadAllText($o)
  return $p.ExitCode
}
# Strip junctions first, deleting the LINK only -- never recurse through one, or we
# delete the real data on the other drive. Two reasons this matters here:
#   1. A run killed mid-migrate leaves exactly this shape, and then every LATER run
#      dies inside its own cleanup ("Access to the path ... is denied") instead of
#      reporting anything about the tool. That is what happened once already.
#   2. -Recurse on a tree containing a junction either fails or walks into D:.
function RmJunctions($p) {
  $stack = New-Object System.Collections.Stack
  $stack.Push($p)
  while ($stack.Count -gt 0) {
    $d = $stack.Pop()
    $subs = @()
    try { $subs = [System.IO.Directory]::GetDirectories($d) } catch { }
    foreach ($s in $subs) {
      $isLink = $false
      try { $isLink = (([int](Get-Item -LiteralPath $s -Force).Attributes) -band 1024) -ne 0 } catch { }
      if ($isLink) { try { [System.IO.Directory]::Delete($s, $false) } catch { } }
      else { $stack.Push($s) }
    }
  }
}
# NOT named Del: PowerShell aliases outrank functions, and "Del" is a
# built-in alias for Remove-Item -- a function of that name is silently ignored.
function RmTree($p) {
  if (-not (Test-Path -LiteralPath $p)) { return }
  RmJunctions $p
  try {
    Get-ChildItem -LiteralPath $p -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
      if ($_.IsReadOnly) { $_.IsReadOnly = $false }
    }
  } catch { }
  try { [System.IO.Directory]::Delete($p, $true) }
  catch { Write-Host ("         (could not fully remove " + $p + ": " + $_.Exception.Message + ")") }
}
function FileBytes($root) {
  $f = [System.IO.Directory]::GetFiles($root, "*", [System.IO.SearchOption]::AllDirectories)
  $b = 0; foreach ($x in $f) { $b += (New-Object System.IO.FileInfo $x).Length }
  return @($f.Count, $b)
}

Write-Host ""
Write-Host "=========== 1. BUILD ==========="
New-Item -ItemType Directory -Force -Path "$proj\build" | Out-Null
& $csc /nologo /langversion:5 /target:winexe /platform:x64 /optimize+ /out:$exe `
  /win32manifest:"$proj\app.manifest" `
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  "$proj\Core.cs" "$proj\Log.cs" "$proj\Migrate.cs" "$proj\Report.cs" "$proj\Program.cs" "$proj\Ui.cs"
Check "compiles" ($LASTEXITCODE -eq 0) "csc exit $LASTEXITCODE"
Check "exe produced" (Test-Path $exe) "no exe"
if (Test-Path $exe) { Write-Host ("         size = " + (Get-Item $exe).Length + " B") }

# The manifest is a resource; if /win32manifest silently did nothing the exe still
# builds and every other check still passes. So read the bytes back and look.
if (Test-Path $exe) {
  $raw = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($exe))
  Check "manifest embedded: asInvoker"     ($raw.Contains("asInvoker"))     "no manifest in exe -- /win32manifest did nothing"
  Check "manifest embedded: longPathAware" ($raw.Contains("longPathAware")) "manifest present but missing longPathAware"
  Check "manifest embedded: PerMonitorV2"  ($raw.Contains("PerMonitorV2"))  "manifest present but no DPI awareness"
}

Write-Host ""
Write-Host "=========== 2. CLEAN + SANDBOX ==========="
# leftover junctions first: remove the LINK only, never recurse into the target.
# FakeGame is in this list because it is the one migration leaves behind if the run
# is interrupted before --undo; missing it means the next run cannot clean up at all.
foreach ($l in @("$proj\test\link1", "$base\link2", "$base\FakeGame", "$sdSrc")) {
  if (Test-Path -LiteralPath $l) {
    try {
      if ((([int](Get-Item -LiteralPath $l -Force).Attributes) -band 1024) -ne 0) {
        [System.IO.Directory]::Delete($l, $false)
      }
    } catch { }
  }
}
RmTree $base
RmTree $tgt
RmTree $recs
RmTree $floorRoot
[System.IO.Directory]::CreateDirectory("$src\bin") | Out-Null
[System.IO.Directory]::CreateDirectory("$src\data\sub") | Out-Null
[System.IO.Directory]::CreateDirectory("$src\emptydir") | Out-Null
$rnd = New-Object System.Random 42
$mb = New-Object byte[] (1MB)
for ($i = 1; $i -le 20; $i++) { $rnd.NextBytes($mb); [System.IO.File]::WriteAllBytes("$src\bin\file$i.bin", $mb) }
for ($i = 1; $i -le 50; $i++) { [System.IO.File]::WriteAllText("$src\data\sub\note$i.txt", ("hello world " * 200) + " line $i") }
[System.IO.File]::WriteAllText("$src\readonly.txt", "read only file")
[System.IO.File]::SetAttributes("$src\readonly.txt", [System.IO.FileAttributes]::ReadOnly)
# Same-drive probe: a plain, unmoved folder whose ONLY possible refusal reason is
# "source and destination are on the same drive". Everything else about it is legal
# (not a link, not a locked name, not a personal dir, KB says movable), so a
# non-zero exit here can only mean that one check fired.
[System.IO.Directory]::CreateDirectory($sdSrc) | Out-Null
[System.IO.File]::WriteAllText("$sdSrc\keep.txt", "untouched")
$rc = FileBytes $src
Check "sandbox built (71 files / 21091925 B)" (($rc[0] -eq 71) -and ($rc[1] -eq 21091925)) ("got " + $rc[0] + " / " + $rc[1])

Write-Host ""
Write-Host "=========== 3. SCAN ==========="
$e = RunCli @("--scan", "$proj\test\report.html", "--min-gb", "2", "--txt", "$proj\test\report.txt")
Check "scan exits 0" ($e -eq 0) "exit $e"
$html = "$proj\test\report.html"; $txt = "$proj\test\report.txt"
Check "html written" ((Test-Path $html) -and ((Get-Item $html).Length -gt 3000)) "missing/small"
Check "txt written"  ((Test-Path $txt)  -and ((Get-Item $txt).Length  -gt 400))  "missing/small"
if (Test-Path $html) {
  $h = [System.IO.File]::ReadAllText($html)
  Check "html has doctype" ($h.Contains("<!DOCTYPE html>")) "no doctype"
  Check "html is SELF-CONTAINED (no http/cdn link)" (-not ($h -match 'src\s*=\s*"https?:') -and -not ($h -match 'href\s*=\s*"https?:') -and -not ($h -match '<link')) "found external reference"
  Check "html has bars" ($h.Contains("class=""fill""")) "no bar marks"
}
if (Test-Path $txt) {
  $t = [System.IO.File]::ReadAllText($txt)
  Check "txt reports GB figures" ($t -match "GB") "no GB"
}

Write-Host ""
Write-Host "=========== 3b. FLOOR: a small movable folder must still be listed ==========="
# The size floor exists to keep the report readable. It must never hide the only
# thing the user can act on.
#
# The bug this pins down: this machine's ONLY movable items are npm (1.41 GB) and
# vscode-remote-wsl (1.66 GB). Both sit under the 2 GB floor, so --min-gb 2 listed
# zero checkable rows and the tool was unusable -- while looking perfectly healthy.
# Movable/delete-only rows now get their own much lower bar (Scanner.ActionFloorBytes).
#
# 320 MB sits between the two bars on purpose: hidden by the old rule, listed by the
# new one. Keep it between them, or this test stops testing anything.
$floorDir = "$floorRoot\SmallMoveable"
RmTree $floorRoot
[System.IO.Directory]::CreateDirectory($floorDir) | Out-Null
$fs = [System.IO.File]::Create("$floorDir\big.bin")
$fs.SetLength(335544320); $fs.Close()     # 320 MB, sparse -- costs no real disk
Check "floor probe reads as 320 MB" ((Get-Item "$floorDir\big.bin").Length -eq 335544320) "wrong length"

$e = RunCli @("--scan", "$proj\test\report2.html", "--min-gb", "2", "--txt", "$proj\test\report2.txt")
Check "floor scan exits 0" ($e -eq 0) "exit $e"
$t2 = [System.IO.File]::ReadAllText("$proj\test\report2.txt")
$moveWord = [char]0x80FD + [char]0x642C          # the "movable" verdict label
Check "320MB movable folder LISTED despite --min-gb 2" ($t2.Contains("SmallMoveable")) `
      "hidden by the floor -- the action list can go empty again"
Check "and it is judged movable" ($t2 -match ("SmallMoveable\s+320\.0 MB\s+" + $moveWord)) `
      "listed but not as movable"

# The other half: the floor must still hide small NON-movable things, or the "two
# bars" change would just be "no floor at all" wearing a hat. Same size, same depth,
# same parent -- the only difference is the verdict, so this pair isolates the rule.
$venvDir = "$floorRoot\.venv"
[System.IO.Directory]::CreateDirectory($venvDir) | Out-Null
$fs = [System.IO.File]::Create("$venvDir\big.bin")
$fs.SetLength(335544320); $fs.Close()
$e = RunCli @("--scan", "$proj\test\report3.html", "--min-gb", "2", "--txt", "$proj\test\report3.txt")
Check "control scan exits 0" ($e -eq 0) "exit $e"
$t3 = [System.IO.File]::ReadAllText("$proj\test\report3.txt")
$neverWord = [char]0x4E0D + [char]0x80FD + [char]0x78B0      # the "do not touch" label
Check "same-size .venv is HIDDEN by the floor" (-not ($t3 -match ("\.venv\s+320\.0 MB"))) `
      "the floor is not filtering anything any more"
# Both scans are done, so this scratch tree can go -- otherwise it lingers and the
# shipped tool would report a 640 MB junk folder sitting in the user's profile.
RmTree $floorRoot

Write-Host ""
Write-Host "=========== 4. MIGRATE ==========="
$freeBefore = (Get-PSDrive C).Free
$e = RunCli @("--migrate", $src, $tgt)
$freeAfter = (Get-PSDrive C).Free
Check "migrate exits 0" ($e -eq 0) "exit $e"
Check "C: free space actually grew (~20 MB)" (($freeAfter - $freeBefore) -gt 15MB) ("delta = " + [math]::Round(($freeAfter - $freeBefore)/1MB, 2) + " MB")

$rn = FileBytes $new
Check "new location has all 71 files" ($rn[0] -eq 71) ("got " + $rn[0])
Check "new location bytes match"      ($rn[1] -eq 21091925) ("got " + $rn[1])

$isLink = $false; $linkTgt = ""
if (Test-Path $src) {
  $it = Get-Item -LiteralPath $src -Force
  $isLink = (([int]$it.Attributes) -band 1024) -ne 0
  $linkTgt = [string]$it.Target
}
Check "old path is now a junction" $isLink "not a reparse point"
Check "junction points at new location" ($linkTgt.TrimEnd('\') -eq $new.TrimEnd('\')) ("target = " + $linkTgt)

$ob = FileBytes $src
Check "every byte still readable THROUGH the old path" (($ob[0] -eq 71) -and ($ob[1] -eq 21091925)) ("got " + $ob[0] + " / " + $ob[1])

$h1 = (Get-FileHash -LiteralPath "$src\readonly.txt" -Algorithm SHA256).Hash
$h2 = (Get-FileHash -LiteralPath "$new\readonly.txt" -Algorithm SHA256).Hash
Check "same content read through OLD path" ($h1 -eq $h2) "hash mismatch"

$probe = "$src\__thru.tmp"
[System.IO.File]::WriteAllText($probe, "x")
$thru = Test-Path -LiteralPath "$new\__thru.tmp"
[System.IO.File]::Delete($probe)
Check "write-through: file written via old path lands in new" $thru "not visible at new location"

$bk = [System.IO.Directory]::GetDirectories($base, "*" + [char]0x8FC1 + [char]0x79FB + "*")
Check "backup folder removed (space actually freed)" ($bk.Count -eq 0) ("left " + $bk.Count)

Write-Host ""
Write-Host "=========== 5. RECORDS / CHECK ==========="
$e = RunCli @("--check")
Check "--check reports healthy" ($e -eq 0) "exit $e"
$e = RunCli @("--records")
Check "--records exits 0" ($e -eq 0) "exit $e"

Write-Host ""
Write-Host "=========== 6. VERIFIER FALSIFICATION ==========="
# A separate identical pair, so we are not comparing a junction to its own target.
# deliberately on C:, so the "same drive" guard does NOT fire and the
# "non-empty destination" guard is what actually gets exercised below
$va = "$base\vt-a"; $vb = "$base\vt-b"
RmTree $va; RmTree $vb
[System.IO.Directory]::CreateDirectory("$va\sub") | Out-Null
[System.IO.Directory]::CreateDirectory("$vb\sub") | Out-Null
1..5 | ForEach-Object { [System.IO.File]::WriteAllText("$va\sub\f$_.txt", "content $_") }
Copy-Item "$va\*" $vb -Recurse -Force
$e = RunCli @("--verify", $va, $vb)
Check "identical pair -> verify PASSES" ($e -eq 0) "exit $e"

[System.IO.File]::WriteAllText("$vb\sub\f3.txt", "content 3 TAMPERED")
$e = RunCli @("--verify", $va, $vb)
Check "one byte changed -> verify FAILS" ($e -ne 0) "exit $e (verifier is blind!)"
[System.IO.File]::WriteAllText("$vb\sub\f3.txt", "content 3")

[System.IO.File]::WriteAllText("$vb\extra.txt", "extra")
$e = RunCli @("--verify", $va, $vb)
Check "extra file -> verify FAILS" ($e -ne 0) "exit $e"
[System.IO.File]::Delete("$vb\extra.txt")

[System.IO.File]::Delete("$va\sub\f5.txt")
$e = RunCli @("--verify", $va, $vb)
Check "missing file -> verify FAILS" ($e -ne 0) "exit $e"
[System.IO.File]::WriteAllText("$va\sub\f5.txt", "content 5")
$e = RunCli @("--verify", $va, $vb)
Check "restored -> verify PASSES again" ($e -eq 0) "exit $e"

Write-Host ""
Write-Host "=========== 7. KNOWLEDGE BASE ==========="
# The verdicts are the safety-critical part of this tool, so they get pinned
# here one path at a time -- not "run a scan and eyeball it".
# Both directions are checked: if the KB answered "never" to everything, the
# suite would still be green and the tool would be useless.
$kbNo = @(
  @("${prof}\xwechat_files\wxid_x\msg",        "wechat msg subdir (the bug: its NAME is 'msg')"),
  @("${prof}\AppData\Roaming\Tencent",         "tencent roaming (the bug: trailing backslash)"),
  @("${prof}\AppData\Local\wsl\{g}\ext4.vhdx", "wsl virtual disk"),
  @("${prof}\AppData\Local\Temp",              "temp (delete only)"),
  @("C:\Windows\System32",                            "system32"),
  @("C:\Program Files\Anything",                      "program files"),
  @("${prof}\proj\.venv\Lib\site-packages",    "python venv"),
  @("${prof}\AppData\Roaming\kingsoft",        "wps"),
  @("${prof}\AppData\Local\Microsoft\Edge",    "browser data"),
  @("C:\ProgramData",                                 "programdata"),
  @("C:\KRECYCLE",                                    "locked name (guard vs report must agree)"),
  @("C:\AMDAcp_v6.0.0.95_id_0",                       "locked name 2"),
  # lowercase path against the real (mixed-case) profile root -- the desktop/
  # documents rules silently failed on this until the case was normalised
  @("c:\users\$unameL\desktop",                         "desktop via lowercase path"),
  @("c:\users\$unameL\appdata\local\programs",          "user-level program dir"),
  # C:\Users -- the worst one. It was NOT in the move guard: --migrate C:\Users went
  # all the way through the checks and would have Directory.Move'd the whole thing.
  @("C:\Users",                                       "C:\Users itself"),
  @("C:\Users\Public",                                "public profile"),
  # AppData was judged by the generic "big folder -> movable" fallback. Lowering the
  # size floor surfaced 6 of these at once and pushed "reclaimable" from 5.56 to 14.22 GB.
  @("${prof}\AppData",                         "AppData root"),
  @("${prof}\AppData\Local",                   "AppData\Local"),
  @("${prof}\AppData\Roaming\Code",            "VS Code user data"),
  @("${prof}\AppData\Local\Packages",          "UWP app data (ACLs are per-app)"),
  # Kernel-level anti-cheat. The old rule said Riot games were MOVABLE, which put
  # Valorant -- the most aggressive kernel anti-cheat in circulation -- in the
  # SAFEST bucket. A move leaves a junction where the game used to be, and these
  # drivers inspect that path. Cannot be verified without risking a real account,
  # so it is refused on cost asymmetry, not on proof.
  @("C:\Games\VALORANT",                              "anti-cheat: Vanguard"),
  @("C:\Games\PUBG",                                  "anti-cheat: BattlEye"),
  @("C:\Games\EasyAntiCheat",                         "the anti-cheat driver itself"),
  @("C:\Riot Games\VALORANT",                         "riot launcher layout"),
  # Found by reviewing someone else's blacklist -- these three were missing here.
  @("C:\Games\Roblox",                                "anti-cheat: Hyperion/Byfron"),
  @("C:\Games\Fortnite",                              "anti-cheat: EAC + BattlEye"),
  @("C:\Games\Battlegrounds",                         "PUBG's own folder name")
)
foreach ($c in $kbNo) {
  $e = RunCli @("--kb", $c[0])
  Check ("KB refuses: " + $c[1]) ($e -ne 0) ("exit " + $e + " -- it called this movable!")
}
$kbYes = @(
  @("${prof}\Downloads\SomeBigThing", "downloads"),
  @("C:\Games\MyGame",                       "plain folder"),
  @("C:\MyProjects",                         "plain top-level folder"),
  # The AppData rule must NOT be a blanket ban -- npm's global dir is genuinely
  # movable (npm resolves it from %APPDATA% at runtime, so the junction holds) and
  # it is one of the few real wins on this machine. Same parent as the Code case
  # above, so this pair isolates the rule instead of testing "AppData is refused".
  @("${prof}\AppData\Roaming\npm",    "npm global dir (movable AppData exception)"),
  # ...and the new anti-cheat blacklist must match WHOLE path segments, never
  # substrings. A folder that merely CONTAINS the word is an ordinary folder and
  # has to stay movable -- otherwise one word in the list starts banning
  # unrelated directories, and the user has no idea why.
  @("${prof}\projects\vanguard-notes", "contains 'vanguard' but is not a game")
)
foreach ($c in $kbYes) {
  $e = RunCli @("--kb", $c[0])
  Check ("KB allows: " + $c[1]) ($e -eq 0) ("exit " + $e + " -- it refuses a perfectly movable folder")
}
$e = RunCli @("--selftest")
Check "--selftest: KB and move-guard agree" ($e -eq 0) "exit $e"

# ...and it has to SAY so. --kb echoes the path it was asked about, which keeps this
# assertion ASCII (this file must stay ASCII: PowerShell 5.1 reads a BOM-less .ps1 as
# GBK and mangles anything else). Read-only command -- it moves nothing.
$probeOut = "C:\Windows\System32"
$e = RunCliCapture @("--kb", $probeOut)
Check "CLI actually prints to stdout" `
      ($script:LastCliOut.Length -gt 8 -and $script:LastCliOut.StartsWith($probeOut)) `
      ("stdout was [" + $script:LastCliOut + "] len=" + $script:LastCliOut.Length)
$e = RunCliCapture @("--selftest")
Check "--selftest prints its verdict count" ($script:LastCliOut -match '\d') `
      ("stdout was [" + $script:LastCliOut + "] len=" + $script:LastCliOut.Length)

# --- logging --------------------------------------------------------------
# The log exists so that a failure on SOMEONE ELSE'S machine leaves evidence.
# So three things have to hold: the location is predictable, it really is
# written, and the BOM appears exactly once.
# The third one targets a specific trap: File.AppendAllText with
# UTF8Encoding(true) writes a BOM on EVERY append, so EF BB BF ends up sprinkled
# through the middle of the file and it looks like garbage. Ours writes the BOM
# by hand only when the file length is 0 -- hence "exactly one", not "has one".
$e = RunCliCapture @("--log")
$logPath = $script:LastCliOut.Trim()
Check "--log prints a path" ($logPath.Length -gt 8) ("stdout=[" + $script:LastCliOut + "]")
Check "--log path is LOCALAPPDATA\CMover\cmover.log" ($logPath -like '*\CMover\cmover.log') $logPath
Check "--log file really exists" (Test-Path -LiteralPath $logPath) $logPath

if (Test-Path -LiteralPath $logPath) {
  $lb = [System.IO.File]::ReadAllBytes($logPath)
  $nb = 0
  for ($i = 0; $i -lt $lb.Length - 2; $i++) {
    if ($lb[$i] -eq 0xEF -and $lb[$i+1] -eq 0xBB -and $lb[$i+2] -eq 0xBF) { $nb++ }
  }
  Check "log starts with a UTF-8 BOM" `
        ($lb.Length -ge 3 -and $lb[0] -eq 0xEF -and $lb[1] -eq 0xBB -and $lb[2] -eq 0xBF) `
        ("first bytes: " + (($lb[0..2] | ForEach-Object { $_.ToString('X2') }) -join ' '))
  Check "log has EXACTLY ONE BOM (an append must not re-write it)" ($nb -eq 1) ("found " + $nb)

  $lt = [System.IO.File]::ReadAllText($logPath)
  Check "log recorded this run's argv" ($lt.Contains("--log")) "the --log run never reached the log"
  Check "log names the executable it ran" ($lt.Contains("cmover.exe")) "banner is missing the exe path"

  # Every run must append -- not truncate, not skip. This is the one that would
  # catch a logger that only writes when it feels like it.
  $len0 = (Get-Item -LiteralPath $logPath).Length
  $e = RunCli @("--selftest")
  $len1 = (Get-Item -LiteralPath $logPath).Length
  Check "each CLI run appends to the log" ($len1 -gt $len0) ("$len0 -> $len1 bytes")
}

Write-Host ""
Write-Host "=========== 8. REFUSALS (must not touch anything) ==========="
# Uses $sdSrc, NOT $src. $src has been migrated by now, so it is a junction and the
# run gets refused for "source is already a link" -- the same-drive check would never
# actually execute while the test still went green. That is the failure mode this
# whole suite exists to avoid: a check that cannot fail is not a check.
$e = RunCli @("--migrate", $sdSrc, "${prof}\cmover-test-target")
Check "same-drive move REFUSED" ($e -ne 0) "exit $e -- should refuse"
Check "  probe untouched (so it really was the same-drive check)" `
      ((Test-Path -LiteralPath "$sdSrc\keep.txt") -and
       (([System.IO.Directory]::GetFiles($sdSrc)).Count -eq 1)) "source got copied or moved anyway"

$e = RunCli @("--migrate", "C:\Windows\System32\drivers", $tgt)
Check "system dir REFUSED" ($e -ne 0) "exit $e"

$e = RunCli @("--migrate", "$env:LOCALAPPDATA\wsl", $tgt)
Check "WSL vhdx REFUSED (must use wsl --export)" ($e -ne 0) "exit $e"

# pre-fill the destination this migrate would pick (= $tgt\vt-a), so it is NOT empty
[System.IO.Directory]::CreateDirectory("$tgt\vt-a") | Out-Null
[System.IO.File]::WriteAllText("$tgt\vt-a\blocker.txt", "x")
$e = RunCli @("--migrate", $va, $tgt)
Check "non-empty destination REFUSED" ($e -ne 0) "exit $e"

Check "refusals left source intact" ((FileBytes $va)[0] -eq 5) "source was touched!"
Check "C:\Windows untouched" ((Test-Path "C:\Windows\System32\drivers") -and -not ((([int](Get-Item "C:\Windows\System32\drivers" -Force).Attributes) -band 1024) -ne 0)) "drivers became a link!"

Write-Host ""
Write-Host "=========== 9. UNDO ==========="
$e = RunCli @("--undo", $src)
Check "undo exits 0" ($e -eq 0) "exit $e"
$ro = FileBytes $src
Check "files back on C: (71)" ($ro[0] -eq 71) ("got " + $ro[0])
Check "bytes back on C:" ($ro[1] -eq 21091925) ("got " + $ro[1])
$stillLink = $false
if (Test-Path $src) { $stillLink = (([int](Get-Item -LiteralPath $src -Force).Attributes) -band 1024) -ne 0 }
Check "junction gone after undo" (-not $stillLink) "still a link"
Check "target copy removed" (-not (Test-Path $new)) "still there"
$ro2 = FileBytes $src
Check "restored bytes still correct" ($ro2[1] -eq 21091925) ("got " + $ro2[1])

Write-Host ""
# Clean up after ourselves. Section 1 already wipes these on the way IN (which is what
# makes a run killed mid-migrate recoverable), but without this, every single run
# leaves a fixture tree -- including a junction -- sitting on the user's C: drive.
# RmTree strips links before deleting, so this cannot follow a junction into D:.
RmTree $base
RmTree $tgt

Write-Host "==================================================="
Write-Host ("  PASS " + $nPass + "   FAIL " + $nFail)
Write-Host "==================================================="
if ($nFail -gt 0) { exit 1 } else { exit 0 }
