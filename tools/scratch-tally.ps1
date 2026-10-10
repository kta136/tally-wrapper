# A stand-in for TallyPrime's XML endpoint. run-scratch.ps1 starts it; it is
# for the scratch environment only and keeps nothing.
#
# It answers the two things the API asks Tally for:
#   - the four master collections (companies, ledgers, stock items, voucher types)
#   - a voucher import (Create or Alter)
#
# To see the bill states that need Tally to misbehave, put one of these words in
# the bill's party name or note (both go into the voucher narration):
#   FAIL     Tally refuses the voucher with a line error  -> bill becomes "failed"
#   UNKNOWN  Tally answers with a server error            -> bill needs reconciliation

[CmdletBinding()]
param(
    [int]$Port = 9000,
    [string]$LogPath,
    # Lists to serve instead of the built-in ones: the file run-scratch.ps1
    # writes after restoring a dump, so a refresh returns the copy's own masters.
    [string]$MastersPath
)

$ErrorActionPreference = 'Stop'

function Yes([bool]$value) { if ($value) { 'Yes' } else { 'No' } }

$companies = @(
    @{ name = 'Scratch Jewellers (Test Books)'; active = $true },
    @{ name = 'Scratch Jewellers 2025-26'; active = $true }
)
# The first seven are the ledger names a new database starts with.
$ledgers = @(
    @{ name = 'Sales'; parent = 'Sales Accounts'; primaryGroup = 'Sales Accounts'; isRevenue = $true },
    @{ name = 'Cash'; parent = 'Cash-in-Hand'; primaryGroup = 'Cash-in-Hand' },
    @{ name = 'Card / UPI'; parent = 'Bank Accounts'; primaryGroup = 'Bank Accounts' },
    @{ name = 'CGST'; parent = 'Duties & Taxes'; primaryGroup = 'Duties & Taxes' },
    @{ name = 'SGST'; parent = 'Duties & Taxes'; primaryGroup = 'Duties & Taxes' },
    @{ name = 'Round Off'; parent = 'Indirect Expenses'; primaryGroup = 'Indirect Expenses'; isRevenue = $true },
    @{ name = 'Discount'; parent = 'Indirect Expenses'; primaryGroup = 'Indirect Expenses'; isRevenue = $true },
    @{ name = 'Sales - Gold'; parent = 'Sales Accounts'; primaryGroup = 'Sales Accounts'; isRevenue = $true },
    @{ name = 'HDFC Bank'; parent = 'Bank Accounts'; primaryGroup = 'Bank Accounts' }
)
$stockItems = @(
    @{ name = 'Gold Ornaments 22KT'; alias = 'G22'; unit = 'GMS'; hsn = '7113' },
    @{ name = 'Gold Ornaments 18KT'; alias = 'G18'; unit = 'GMS'; hsn = '7113' },
    @{ name = 'Gold Ornaments 14KT'; alias = 'G14'; unit = 'GMS'; hsn = '7113' },
    @{ name = 'Gold Coin 24KT'; alias = 'G24'; unit = 'GMS'; hsn = '7118' },
    @{ name = 'Silver Articles'; alias = 'SLV'; unit = 'GMS'; hsn = '7114' }
)
$voucherTypes = @(
    @{ name = 'Sales'; parent = 'Sales' },
    @{ name = 'Sales - Showroom'; parent = 'Sales' },
    @{ name = 'Receipt'; parent = 'Receipt' }
)

$mastersSource = 'built-in test lists'
if ($MastersPath -and (Test-Path -LiteralPath $MastersPath)) {
    $file = Get-Content -LiteralPath $MastersPath -Raw | ConvertFrom-Json
    # A list the copy does not have stays on the built-in one.
    if (@($file.companies).Count -gt 0) { $companies = @($file.companies) }
    if (@($file.ledgers).Count -gt 0) { $ledgers = @($file.ledgers) }
    if (@($file.stockItems).Count -gt 0) { $stockItems = @($file.stockItems) }
    if (@($file.voucherTypes).Count -gt 0) { $voucherTypes = @($file.voucherTypes) }
    # Push checks that the company in Settings is open in Tally.
    if ($file.activeCompany -and -not (@($companies | ForEach-Object { $_.name }) -contains $file.activeCompany)) {
        $companies += @{ name = $file.activeCompany; active = $true }
    }
    $mastersSource = "lists from $MastersPath"
}

function Escape-Xml([string]$text) { [System.Security.SecurityElement]::Escape($text) }

# An element, or nothing when there is no value: Tally leaves empty fields out.
function Tag([string]$name, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { '' } else { "<$name>$(Escape-Xml $value)</$name>" }
}

function Get-Collection([string]$body) {
    $rows = if ($body -match '<ID>TW_Companies</ID>') {
        $companies | ForEach-Object {
            "<COMPANY NAME=`"$(Escape-Xml $_.name)`">$(Tag 'NAME' $_.name)<ISINACTIVE>$(Yes (-not $_.active))</ISINACTIVE></COMPANY>"
        }
    } elseif ($body -match '<ID>TW_Ledgers</ID>') {
        $ledgers | ForEach-Object {
            "<LEDGER NAME=`"$(Escape-Xml $_.name)`">$(Tag 'PARENT' $_.parent)$(Tag 'PRIMARYGROUP' $_.primaryGroup)<ISREVENUE>$(Yes ([bool]$_.isRevenue))</ISREVENUE>$(Tag 'PARTYGSTIN' $_.gstin)</LEDGER>"
        }
    } elseif ($body -match '<ID>TW_StockItems</ID>') {
        $stockItems | ForEach-Object {
            "<STOCKITEM NAME=`"$(Escape-Xml $_.name)`">$(Tag 'ALIASNAME' $_.alias)$(Tag 'BASEUNITS' $_.unit)$(Tag 'GSTHSNCODE' $_.hsn)</STOCKITEM>"
        }
    } elseif ($body -match '<ID>TW_VoucherTypes</ID>') {
        $voucherTypes | ForEach-Object {
            "<VOUCHERTYPE NAME=`"$(Escape-Xml $_.name)`">$(Tag 'PARENT' $_.parent)<ISDEEMEDPOSITIVE>$(Yes ([bool]$_.deemedPositive))</ISDEEMEDPOSITIVE></VOUCHERTYPE>"
        }
    } else {
        return $null
    }

    "<ENVELOPE><HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER><BODY><DATA><COLLECTION>$($rows -join '')</COLLECTION></DATA></BODY></ENVELOPE>"
}

function Get-ImportResult([int]$created, [int]$altered, [int]$errors, [string]$lineError, [string]$voucherId) {
    $line = if ($lineError) { "<LINEERROR>$(Escape-Xml $lineError)</LINEERROR>" } else { '' }
    $last = if ($voucherId) { "<LASTVCHID>$voucherId</LASTVCHID>" } else { '' }
    "<ENVELOPE><HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER><BODY><DATA><IMPORTRESULT>" +
        "<CREATED>$created</CREATED><ALTERED>$altered</ALTERED><DELETED>0</DELETED>$last" +
        "<ERRORS>$errors</ERRORS><EXCEPTIONS>0</EXCEPTIONS>$line</IMPORTRESULT></DATA></BODY></ENVELOPE>"
}

function Write-Log([string]$message) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $message"
    Write-Host $line
    if ($LogPath) { Add-Content -LiteralPath $LogPath -Value $line }
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Log "Stand-in Tally listening on http://127.0.0.1:$Port (not TallyPrime; nothing is kept). Serving $mastersSource."

$nextVoucherId = 1001
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $status = 200
        $answer = ''
        try {
            $reader = [System.IO.StreamReader]::new($context.Request.InputStream, [System.Text.Encoding]::UTF8)
            $body = $reader.ReadToEnd()
            $reader.Dispose()

            if ($body -match '<VOUCHER\b[^>]*\bACTION="(Create|Alter)"') {
                $action = $Matches[1]
                $narration = if ($body -match '<NARRATION>(.*?)</NARRATION>') { $Matches[1] } else { '' }
                $number = if ($body -match '<VOUCHERNUMBER>(.*?)</VOUCHERNUMBER>') { $Matches[1] } else { '?' }
                if ($narration -cmatch 'UNKNOWN') {
                    $status = 500
                    $answer = 'Stand-in Tally: server error on request (narration contains UNKNOWN).'
                    Write-Log "voucher $action $number -> HTTP 500 (UNKNOWN in narration)"
                } elseif ($narration -cmatch 'FAIL') {
                    $answer = Get-ImportResult 0 0 1 'Stand-in Tally refused this voucher: the party name or note contains FAIL.' ''
                    Write-Log "voucher $action $number -> line error (FAIL in narration)"
                } elseif ($action -eq 'Create') {
                    $answer = Get-ImportResult 1 0 0 '' "$nextVoucherId"
                    Write-Log "voucher Create $number -> created, voucher id $nextVoucherId"
                    $nextVoucherId++
                } else {
                    $answer = Get-ImportResult 0 1 0 '' ''
                    Write-Log "voucher Alter $number -> altered"
                }
            } else {
                $answer = Get-Collection $body
                if ($null -eq $answer) {
                    $status = 400
                    $answer = 'Stand-in Tally does not know this request.'
                    Write-Log 'unknown request -> HTTP 400'
                } else {
                    $kind = if ($body -match '<ID>(TW_\w+)</ID>') { $Matches[1] } else { '?' }
                    Write-Log "collection $kind"
                }
            }
        }
        catch {
            $status = 500
            $answer = "Stand-in Tally error: $($_.Exception.Message)"
            Write-Log $answer
        }

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($answer)
        $context.Response.StatusCode = $status
        $context.Response.ContentType = 'text/xml; charset=utf-8'
        $context.Response.ContentLength64 = $bytes.Length
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
    }
}
finally {
    $listener.Close()
}
