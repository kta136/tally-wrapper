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
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$companies = @(
    @{ Name = 'Scratch Jewellers (Test Books)'; Inactive = 'No' },
    @{ Name = 'Scratch Jewellers 2025-26'; Inactive = 'No' }
)
# Name, parent, primary group, is revenue. The first seven are the ledger names a new database starts with.
$ledgers = @(
    @('Sales', 'Sales Accounts', 'Sales Accounts', 'Yes'),
    @('Cash', 'Cash-in-Hand', 'Cash-in-Hand', 'No'),
    @('Card / UPI', 'Bank Accounts', 'Bank Accounts', 'No'),
    @('CGST', 'Duties & Taxes', 'Duties & Taxes', 'No'),
    @('SGST', 'Duties & Taxes', 'Duties & Taxes', 'No'),
    @('Round Off', 'Indirect Expenses', 'Indirect Expenses', 'Yes'),
    @('Discount', 'Indirect Expenses', 'Indirect Expenses', 'Yes'),
    @('Sales - Gold', 'Sales Accounts', 'Sales Accounts', 'Yes'),
    @('HDFC Bank', 'Bank Accounts', 'Bank Accounts', 'No')
)
# Name, alias, unit, HSN.
$stockItems = @(
    @('Gold Ornaments 22KT', 'G22', 'GMS', '7113'),
    @('Gold Ornaments 18KT', 'G18', 'GMS', '7113'),
    @('Gold Ornaments 14KT', 'G14', 'GMS', '7113'),
    @('Gold Coin 24KT', 'G24', 'GMS', '7118'),
    @('Silver Articles', 'SLV', 'GMS', '7114')
)
# Name, parent.
$voucherTypes = @(
    @('Sales', 'Sales'),
    @('Sales - Showroom', 'Sales'),
    @('Receipt', 'Receipt')
)

function Escape-Xml([string]$text) { [System.Security.SecurityElement]::Escape($text) }

function Get-Collection([string]$body) {
    $rows = if ($body -match '<ID>TW_Companies</ID>') {
        $companies | ForEach-Object {
            "<COMPANY NAME=`"$(Escape-Xml $_.Name)`"><NAME>$(Escape-Xml $_.Name)</NAME><ISINACTIVE>$($_.Inactive)</ISINACTIVE></COMPANY>"
        }
    } elseif ($body -match '<ID>TW_Ledgers</ID>') {
        $ledgers | ForEach-Object {
            "<LEDGER NAME=`"$(Escape-Xml $_[0])`"><PARENT>$(Escape-Xml $_[1])</PARENT><PRIMARYGROUP>$(Escape-Xml $_[2])</PRIMARYGROUP><ISREVENUE>$($_[3])</ISREVENUE></LEDGER>"
        }
    } elseif ($body -match '<ID>TW_StockItems</ID>') {
        $stockItems | ForEach-Object {
            "<STOCKITEM NAME=`"$(Escape-Xml $_[0])`"><ALIASNAME>$($_[1])</ALIASNAME><BASEUNITS>$($_[2])</BASEUNITS><GSTHSNCODE>$($_[3])</GSTHSNCODE></STOCKITEM>"
        }
    } elseif ($body -match '<ID>TW_VoucherTypes</ID>') {
        $voucherTypes | ForEach-Object {
            "<VOUCHERTYPE NAME=`"$(Escape-Xml $_[0])`"><PARENT>$(Escape-Xml $_[1])</PARENT><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE></VOUCHERTYPE>"
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
Write-Log "Stand-in Tally listening on http://127.0.0.1:$Port (not TallyPrime; nothing is kept)."

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
