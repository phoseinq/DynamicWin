# Press Cancel on a browser download, from outside the browser.
#
# Runs under Windows PowerShell 5.1 rather than in-process, and that is the point. Chrome is UIA-first
# (MSAA returns zero children on the frame window AND on every Chrome_RenderWidgetHostHWND), so reaching
# the control means an IUIAutomation client. Hand-writing that COM vtable is ~400 lines where one wrong
# slot is an access violation, and this repo's first rule is that nothing may crash the pill. 5.1 ships
# UIAutomationClient in the GAC on every Windows install, so the whole client costs a process boundary
# instead — and a hang or crash out here cannot touch the notch.
#
# Ctrl+J is sent from HERE, not by the caller, because both browsers now answer it with a flyout that
# closes itself. Measured on Chrome: the caller sent Ctrl+J, spawned this process, and by the time the
# tree could be read the bubble was gone and the sweep saw the New Tab page. Opening the list and acting
# on it has to happen inside one uninterrupted run, with focus never leaving the browser.
#
# Exit codes are the contract: 0 pressed, 2 nothing to press, 3 no way to open the list.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$hwnd   = [IntPtr]__HWND__
$target = '__TARGET__'
$canTab = '__CANTAB__' -eq '1'   # is the browser confirmed in front? only then may we send keystrokes

# Neither control carries a stable AutomationId, so this matches on name; an unmatched locale falls out
# as exit 2 and the caller still leaves the downloads list open in front of the user.
$cancelLabels = @('Cancel', 'Cancel download', 'Abbrechen', 'Annuler', 'Cancelar', 'Annulla',
                  'Anuluj', 'Avbryt', 'Annuleren', 'Iptal')
# 'More options' is deliberately NOT here. Edge's downloads flyout has a control by that name, but it is
# the flyout's own overflow, not a row menu — and the single-candidate fallback below was pressing it,
# opening download settings and then reporting that no cancel item existed.
$moreLabels   = @('More actions', 'Weitere Aktionen', 'Plus d''actions', 'Mas acciones', 'Altre azioni')

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$isControl = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)
$Desc = [System.Windows.Automation.TreeScope]::Descendants

# Chrome puts its downloads on a PAGE inside the frame window; Edge opens a FLYOUT, which is its own
# top-level window. Searching only the frame's subtree therefore found Edge's menu button but never the
# Cancel item inside the flyout, and reported "menu opened but no cancel item". Sweep every top-level
# window the browser process owns instead of just the one we were handed.
$ownerPid = $root.Current.ProcessId
$byPid = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ownerPid)

function Sweep {
    $out = New-Object System.Collections.ArrayList
    foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                 [System.Windows.Automation.TreeScope]::Children, $byPid)) {
        try { foreach ($e in $top.FindAll($Desc, $isControl)) { [void]$out.Add($e) } } catch { }
    }
    if ($out.Count -eq 0) { foreach ($e in $root.FindAll($Desc, $isControl)) { [void]$out.Add($e) } }
    return $out
}

function Named($all, $labels) {
    $r = @()
    foreach ($e in $all) { if ($labels -contains $e.Current.Name) { $r += $e } }
    return $r
}

# Advertising a pattern is not the same as honouring it: Edge's downloads toolbar button reports
# ExpandCollapse and answers Expand with E_FAIL. With $ErrorActionPreference = 'Stop' that killed the whole
# script on its first strategy and reported rc=1, so a cancel that would have worked one strategy later
# never got there. Each attempt is therefore its own try, and a refusal just means "try the next thing".
function Press($e) {
    $p = @()
    try { $p = $e.GetSupportedPatterns() } catch { return $false }
    if ($p -contains [System.Windows.Automation.InvokePattern]::Pattern) {
        try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    }
    # a menu BUTTON expands rather than invokes; the menu ITEM inside it is the one that invokes
    if ($p -contains [System.Windows.Automation.ExpandCollapsePattern]::Pattern) {
        try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); return $true } catch { }
    }
    # Chrome's toolbar downloads button carries ONLY TogglePattern. Leaving it out here meant the one
    # control that opens Chrome's download bubble was reported as unpressable and silently skipped.
    if ($p -contains [System.Windows.Automation.TogglePattern]::Pattern) {
        try { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return $true } catch { }
    }
    return $false
}

# The toolbar's downloads button, which is what opens the list. Matched by shape rather than by an exact
# label: it is the one download-ish control that opens something, and the pattern requirement is what
# separates it from its neighbours ("Open downloads folder" only invokes; the flyout's own "Downloads"
# heading only invokes). Edge names it "Downloads, 5% complete" with ExpandCollapse; Chrome names it
# "1 download in progress" with Toggle.
function DownloadsButton($all) {
    foreach ($e in $all) {
        try {
            $n = $e.Current.Name
            if (-not $n -or $n -notmatch '(?i)download') { continue }
            if ($e.Current.ControlType.ProgrammaticName -notlike '*Button*') { continue }
            $p = $e.GetSupportedPatterns()
            if (($p -contains [System.Windows.Automation.TogglePattern]::Pattern) -or
                ($p -contains [System.Windows.Automation.ExpandCollapsePattern]::Pattern)) { return $e }
        } catch { }
    }
    return $null
}

if (-not ('Halo.Keys' -as [type])) {
    Add-Type -Namespace Halo -Name Keys -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte vk, byte scan, uint flags, System.IntPtr extra);
'@
}
function Tap([byte]$vk) {
    [Halo.Keys]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
    [Halo.Keys]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
}

# ── open the list ──────────────────────────────────────────────────────────────────────────────────────
#
# Pressing the toolbar's own downloads button beats sending Ctrl+J, and not by a little: a keystroke goes
# to whatever holds the foreground, so it needs the browser in front, and Windows grants foreground rights
# only to the process receiving input — which the pill does not always still have by the time this runs.
# Measured: with rights denied, the old path sent nothing, found nothing and left the user staring at an
# unchanged download. UIA presses the button whether or not the browser is in front.
#
# Chrome needs this for a second reason. Its Ctrl+J bubble never appears in the accessibility tree at all
# (measured: after Ctrl+J the only download-ish control anywhere was the toolbar button itself), while
# toggling that button puts a "Recent download history" window in the tree with the row inside it.
# The retry is not optional: a browser builds its accessibility tree only once a UIA client attaches, and
# attaching is what this script just did. The first sweep can come back with a handful of controls and no
# toolbar at all — measured here as "no downloads button" against an Edge window that plainly had one.
$opened = $false
for ($try = 0; $try -lt 5; $try++) {
    $btn = DownloadsButton (Sweep)
    if ($btn) { $opened = Press $btn; break }
    Start-Sleep -Milliseconds 400
}
if ($opened) { Start-Sleep -Milliseconds 900 }
elseif ($canTab) {
    # Ctrl+J is the one shortcut every Chromium browser and Firefox share — the fallback when the button
    # could not be named. Only when the caller confirmed the browser is in front, or the keystroke lands
    # in whatever window does hold it.
    [Halo.Keys]::keybd_event(0x11, 0, 0, [IntPtr]::Zero)   # Ctrl down
    Tap 0x4A                                               # J
    [Halo.Keys]::keybd_event(0x11, 0, 2, [IntPtr]::Zero)   # Ctrl up
    Start-Sleep -Milliseconds 700
}
# and if neither worked, carry on anyway: the list may already be open, and the app-menu strategy below
# needs no keystroke and no foreground rights. Giving up here is what made a click on Cancel do nothing.

# ── strategy 1: walk keyboard focus ────────────────────────────────────────────────────────────────────
#
# This is first because it is the only one that works against a list that closes itself, and because the
# controls it needs may not be in the tree at all until focus reaches them. Measured on Edge: a descendant
# search over the downloads flyout returns the row as a Group with an Image, two Texts and a ProgressBar —
# and no buttons whatsoever. Tab into that row and 'Pause' and 'Cancel' appear as the next two focus stops,
# with the row's Group name spelling out everything it contains ("File icon report.pdf 24.0 KB/s - 7.5 MB
# of 60.0 MB, 37 mins left 12 Pause Cancel"). That is why cancel worked in Chrome and did nothing at all
# in Edge for so long: the control was never there to be found.
$rowName = ''
$first = ''
for ($step = 0; $canTab -and $step -lt 60; $step++) {
    $f = $null
    try { $f = [System.Windows.Automation.AutomationElement]::FocusedElement } catch { }
    if ($f) {
        $name = ''; $type = ''
        try { $name = $f.Current.Name; $type = $f.Current.ControlType.ProgrammaticName } catch { }
        $stop = "$type|$name"
        if ($step -eq 0) { $first = $stop }
        elseif ($stop -eq $first) { break }   # wrapped all the way round: nothing to cancel here

        # remember the row we are inside, so a Cancel belongs to a known download and not to whichever
        # row happened to come first
        if ($name -and ($type -like '*Group*' -or $type -like '*ListItem*')) { $rowName = $name }

        if ($cancelLabels -contains $name) {
            if (-not $target -or $rowName -like "*$target*" -or -not $rowName) {
                if (Press $f) { Write-Output "pressed cancel via focus walk (row '$rowName')"; exit 0 }
            }
        }
    }
    Tap 0x09      # Tab
    Start-Sleep -Milliseconds 150
}

# ── strategy 2: open the downloads PAGE through the app menu ───────────────────────────────────────────
#
# Chrome's Ctrl+J and its toolbar button both produce a bubble whose rows expose exactly one control — the
# row itself — so there is nothing named Cancel to press anywhere in it (measured: 'Close' plus one Button
# per row, nothing else). The app menu's Downloads item opens the real chrome://downloads page instead,
# where each row carries 'Copy download link' and a 'More actions' menu holding Pause and Cancel. So the
# way into Chrome is its menu bar, not its download UI.
#
# The menu item is matched on 'Ctrl+J' rather than on the word "Downloads": the accelerator is the same
# string in every locale, and the label is not.
function OpenDownloadsPage {
    $menuLabels = @('Chrome', 'Google Chrome', 'Customize and control Google Chrome',
                    'Brave', 'Vivaldi', 'Opera', 'Firefox')
    foreach ($e in (Sweep)) {
        $ec = $null
        try {
            $n = $e.Current.Name
            if (-not $n) { continue }
            if (($menuLabels -notcontains $n) -and ($n -notlike '*Alt+F*') -and ($n -notlike '*Alt+E*')) { continue }
            if ($e.Current.ControlType.ProgrammaticName -notlike '*Button*') { continue }
            $p = $e.GetSupportedPatterns()
            if ($p -notcontains [System.Windows.Automation.ExpandCollapsePattern]::Pattern) { continue }
            $ec = $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $ec.Expand()
        } catch { continue }

        Start-Sleep -Milliseconds 700
        foreach ($m in (Sweep)) {
            try {
                if ($m.Current.ControlType.ProgrammaticName -notlike '*MenuItem*') { continue }
                if ($m.Current.Name -notlike '*Ctrl+J*') { continue }
            } catch { continue }
            if (Press $m) { Start-Sleep -Milliseconds 1400; return $true }
        }
        # leave nothing hanging open in the user's face if the item was not there
        try { $ec.Collapse() } catch { }
    }
    return $false
}

[void](OpenDownloadsPage)

# ── strategy 3: the row's own menu ─────────────────────────────────────────────────────────────────────
# On the downloads page an in-progress row shows only "Copy download link" and "More actions", with Pause
# and Cancel as MenuItems inside that menu.
#
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
# no filename to match on is still safe when there is exactly one row menu on screen
if (-not $more) {
    $cands = Named $all $moreLabels
    if ($cands.Count -eq 1) { $more = $cands[0] }
}
if (-not $more) {
    Write-Output "no row menu or focusable cancel for '$target'; controls seen:"
    $all | ForEach-Object { if ($_.Current.Name -and $_.Current.Name.Length -lt 40) { Write-Output ("  " + $_.Current.Name) } }
    exit 2
}

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
