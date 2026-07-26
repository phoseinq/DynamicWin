# Press Cancel on a browser download, from outside the browser.
#
# Runs under Windows PowerShell 5.1 rather than in-process, and that is the point. Chrome is UIA-first
# (MSAA returns zero children on the frame window AND on every Chrome_RenderWidgetHostHWND), so reaching
# the control means an IUIAutomation client. Hand-writing that COM vtable is ~400 lines where one wrong
# slot is an access violation, and this repo's first rule is that nothing may crash the pill. 5.1 ships
# UIAutomationClient in the GAC on every Windows install, so the whole client costs a process boundary
# instead — and a hang or crash out here cannot touch the notch.
#
# Exit codes are the contract: 0 cancelled and verified, 2 nothing to press, 3 pressed but the download
# was still running afterwards.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$hwnd   = [IntPtr]__HWND__
$target = '__TARGET__'

# Neither control carries a stable AutomationId, so this matches on name; an unmatched locale falls out
# as exit 2 and the caller still leaves the downloads list open in front of the user.
$cancelLabels = @('Cancel', 'Cancel download', 'Abbrechen', 'Annuler', 'Cancelar', 'Annulla',
                  'Anuluj', 'Avbryt', 'Annuleren', 'Iptal')
$moreLabels   = @('More actions', 'More options', 'Weitere Aktionen', 'Plus d''actions',
                  'Mas acciones', 'Altre azioni')

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$isControl = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)
$Desc = [System.Windows.Automation.TreeScope]::Descendants

function Sweep { return $root.FindAll($Desc, $isControl) }

function Named($all, $labels) {
    $r = @()
    foreach ($e in $all) { if ($labels -contains $e.Current.Name) { $r += $e } }
    return $r
}

function Press($e) {
    $p = $e.GetSupportedPatterns()
    if ($p -contains [System.Windows.Automation.InvokePattern]::Pattern) {
        $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true
    }
    # a menu BUTTON expands rather than invokes; the menu ITEM inside it is the one that invokes
    if ($p -contains [System.Windows.Automation.ExpandCollapsePattern]::Pattern) {
        $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); return $true
    }
    return $false
}

# Chrome only builds the renderer's accessibility tree once a UIA client attaches, and attaching is what
# this script just did — so the first sweep sees browser chrome and no page content at all. Measured: one
# query returned 47 buttons with no downloads rows, a later one had them.
$all = @()
for ($try = 0; $try -lt 6; $try++) {
    $all = Sweep
    if ((Named $all @('Clear all')).Count -gt 0) { break }
    Start-Sleep -Milliseconds 400
}

# Find the row FIRST, then its menu button, rather than finding menu buttons and walking up to guess which
# row they belong to. The ancestor walk matched the wrong row and cancelled a download that was already
# finished, which looked like success and changed nothing.
$more = $null
if ($target) {
    foreach ($e in $all) {
        if ($e.Current.Name -notlike "*$target*") { continue }
        foreach ($m in $moreLabels) {
            $c = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $m)
            $hit = $e.FindFirst($Desc, $c)
            if ($hit) { $more = $hit; break }
        }
        if ($more) { break }
    }
}
# no filename to match on (Edge never renames its Unconfirmed partial) is still safe when there is one row
if (-not $more) {
    $cands = Named $all $moreLabels
    if ($cands.Count -eq 1) { $more = $cands[0] }
}
if (-not $more) {
    Write-Output "no row menu for '$target'; controls seen:"
    $all | ForEach-Object { if ($_.Current.Name -and $_.Current.Name.Length -lt 40) { Write-Output ("  " + $_.Current.Name) } }
    exit 2
}

# An in-progress row shows only "Copy download link" and "More actions"; Pause and Cancel live in that
# row's menu, as MenuItems that do carry InvokePattern.
Press $more | Out-Null
Start-Sleep -Milliseconds 800

$cancel = $null
foreach ($e in (Named (Sweep) $cancelLabels)) {
    if ($e.Current.ControlType.ProgrammaticName -like '*MenuItem*') { $cancel = $e; break }
    if (-not $cancel) { $cancel = $e }
}
if (-not $cancel) { Write-Output "menu opened but no cancel item"; exit 2 }
Press $cancel | Out-Null

# Whether it actually worked is not decided here. Checking the row was tried and was wrong — a cancelled
# row still carries a menu (Copy download link, Delete from history), so a successful cancel reported
# failure. The caller watches the partial file instead, which is the same answer in every language.
exit 0
