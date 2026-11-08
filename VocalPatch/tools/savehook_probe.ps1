# Save-hook probe, no rendezvous flag.
#
# Windows PowerShell 5.1 is a .NET Framework process, so the injected runtime shares its
# runtime and AppDomain (the injected log confirms it). The question this answers: which
# BCL write methods does the injected postfix actually intercept? Each write uses a
# differently sized BCL entry point, because a small method can be inlined into its caller
# by the JIT, and an inlined copy never reaches a Harmony detour.
#
# The caller injects during the first 15 seconds, so no flag file is involved - the earlier
# flag-watching version stalled for reasons that had nothing to do with the hook.

$trace = Join-Path $env:TEMP 'vpatch-probe-trace.txt'
$dir = Join-Path $env:TEMP 'vpatch-savehook'

function Note([string] $text) {
    try { Add-Content -Path $trace -Value ((Get-Date -Format 'HH:mm:ss.fff') + '  ' + $text) } catch { }
}

Set-Content -Path $trace -Value "probe started pid=$PID  $([System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription)"
Note ("appdomain: " + [AppDomain]::CurrentDomain.FriendlyName)

Note 'waiting 15s for the injection'
Start-Sleep -Seconds 15
Note 'writing'

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$body = "[#SETTING]`r`nTempo=120.00`r`n[#0000]`r`nLength=480`r`nLyric=a`r`nNoteNum=60`r`n[#TRACKEND]`r`n"

function Try-Write([string] $label, [scriptblock] $action) {
    Note ("before $label")
    try {
        & $action
        Note ("ok     $label")
    } catch {
        Note ("FAIL   $label : " + $_.Exception.GetType().Name + ' - ' + $_.Exception.Message)
    }
    Note ("after  $label")
}

Try-Write 'File.WriteAllText'  { [System.IO.File]::WriteAllText((Join-Path $dir 'a-writealltext.ust'), $body) }
Try-Write 'File.WriteAllBytes' { [System.IO.File]::WriteAllBytes((Join-Path $dir 'b-writeallbytes.ust'), [Text.Encoding]::UTF8.GetBytes($body)) }
Try-Write 'XmlDocument.Save'   { $x = New-Object System.Xml.XmlDocument; $x.LoadXml('<song/>'); $x.Save((Join-Path $dir 'c-xmldocument.ust')) }
Try-Write 'XDocument.Save'     { [System.Xml.Linq.XDocument]::Parse('<song/>').Save((Join-Path $dir 'd-xdocument.ust')) }
Try-Write 'StreamWriter'       { $w = New-Object System.IO.StreamWriter((Join-Path $dir 'e-streamwriter.ust')); $w.Write($body); $w.Dispose() }

Note 'done writing'
Start-Sleep -Seconds 10
Note 'probe exiting'
