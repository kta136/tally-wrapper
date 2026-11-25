# A stand-in for TallyPrime's XML endpoint. run-scratch.ps1 starts it; it is
# for the scratch environment only and keeps nothing.
#
# It answers the three things the API asks Tally for:
#   - the four master collections (companies, ledgers, stock items, voucher types)
#   - a voucher import (Create or Alter)
#   - which voucher has a given master id, or which vouchers carry a given
#     number (asked before an edited bill is pushed); it knows the vouchers of
#     this run only
#
# An Alter is matched the way TallyPrime matches it: by its TAGVALUE master id
# together with its DATE attribute, which must be the date the voucher has now.
# When they do not match a voucher, it creates a new one (CREATED=1), as
# TallyPrime does, and the API reports the bill as needing reconciliation.
#
# To see the bill states that need Tally to misbehave, put one of these words in
# the bill's party name or note (both go into the voucher narration):
#   FAIL     Tally refuses the voucher with a line error  -> bill becomes "failed"
#   UNKNOWN  Tally keeps the voucher but answers with a server error
#                                                         -> bill needs reconciliation
# After UNKNOWN, Mark as Pushed is the true answer: edit the bill (take the word
# out) and push, and the voucher is found by its number and altered.

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

# The first is the company a new database names as the active one. Push checks that the
# active company is open in Tally, so without it nothing could be pushed in a fresh
# scratch run until another company was picked in Settings.
$companies = @(
    @{ name = 'Development Company'; active = $true },
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

function Get-VoucherRow([string]$masterId, $voucher) {
    "<VOUCHER VCHTYPE=`"$(Escape-Xml $voucher.type)`"><DATE TYPE=`"Date`">$($voucher.date)</DATE><MASTERID>$masterId</MASTERID>$(Tag 'VOUCHERNUMBER' $voucher.number)$(Tag 'VOUCHERTYPENAME' $voucher.type)$(Tag 'AMOUNT' $voucher.amount)<ISDELETED>No</ISDELETED></VOUCHER>"
}

# The company a request names (SVCURRENTCOMPANY). Master ids belong to a company:
# a voucher is found, and an Alter matched, only in the company it was taken in.
function Get-RequestCompany([string]$body) {
    if ($body -match '<SVCURRENTCOMPANY>(.*?)</SVCURRENTCOMPANY>') { [System.Net.WebUtility]::HtmlDecode($Matches[1]).Trim() } else { '' }
}

function Get-Collection([string]$body) {
    $company = Get-RequestCompany $body
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
    } elseif ($body -match '<ID>TW_VouchersByNumber</ID>') {
        $wanted = if ($body -match '\$VoucherNumber = "([^"]*)"') { [System.Net.WebUtility]::HtmlDecode($Matches[1]) } else { '' }
        $vouchers.GetEnumerator() | Where-Object { $_.Value.company -eq $company -and $_.Value.number -eq $wanted } | ForEach-Object { Get-VoucherRow $_.Key $_.Value }
    } elseif ($body -match '<ID>TW_VoucherById</ID>') {
        $wanted = if ($body -match '\$MasterID = (\d+)') { $Matches[1] } else { '' }
        $vouchers.GetEnumerator() | Where-Object { $_.Value.company -eq $company -and $_.Key -eq $wanted } | ForEach-Object { Get-VoucherRow $_.Key $_.Value }
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
# The vouchers taken in this run, by master id, for the lookup by number.
$vouchers = [ordered]@{}
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
                $number = if ($body -match '<VOUCHERNUMBER>(.*?)</VOUCHERNUMBER>') { [System.Net.WebUtility]::HtmlDecode($Matches[1]) } else { '?' }
                $type = if ($body -match '<VOUCHER\b[^>]*\bVCHTYPE="([^"]*)"') { [System.Net.WebUtility]::HtmlDecode($Matches[1]) } else { 'Sales' }
                $target = if ($body -match '<VOUCHER\b[^>]*\bTAGVALUE="(\d+)"') { $Matches[1] } else { '' }
                $dateNamed = if ($body -match '<VOUCHER\b[^>]*\bDATE="(\d{8})"') { $Matches[1] } else { '' }
                $date = if ($body -match '<DATE>(\d{8})</DATE>') { $Matches[1] } else { '' }
                # The voucher's total as Tally lists it: the party line, the first ledger line the API sends.
                $amount = if ($body -match '<AMOUNT>(-?[\d.]+)</AMOUNT>') { $Matches[1] } else { '' }
                $company = Get-RequestCompany $body
                # TallyPrime finds the voucher to alter by master id and the date it has now, within the company.
                $matched = $action -eq 'Alter' -and $target -and $vouchers.Contains($target) -and
                    $vouchers[$target].company -eq $company -and $dateNamed -eq $vouchers[$target].date
                if ($narration -cmatch 'FAIL') {
                    $answer = Get-ImportResult 0 0 1 'Stand-in Tally refused this voucher: the party name or note contains FAIL.' ''
                    Write-Log "voucher $action $number -> line error (FAIL in narration)"
                } else {
                    # Tally takes the voucher, whether or not its answer gets back.
                    $voucherId = ''
                    if ($matched) {
                        $vouchers[$target] = @{ number = $number; type = $type; date = $date; amount = $amount; company = $company }
                    } else {
                        $voucherId = "$nextVoucherId"
                        $vouchers[$voucherId] = @{ number = $number; type = $type; date = $date; amount = $amount; company = $company }
                        $nextVoucherId++
                    }

                    if ($narration -cmatch 'UNKNOWN') {
                        $status = 500
                        $answer = 'Stand-in Tally: server error on request (narration contains UNKNOWN).'
                        Write-Log "voucher $action $number -> kept, but HTTP 500 (UNKNOWN in narration)"
                    } elseif ($matched) {
                        $answer = Get-ImportResult 0 1 0 '' $target
                        Write-Log "voucher Alter $number -> altered, voucher id $target"
                    } else {
                        $answer = Get-ImportResult 1 0 0 '' $voucherId
                        if ($action -eq 'Alter') {
                            Write-Log "voucher Alter $number -> no voucher $target dated '$dateNamed': created voucher id $voucherId instead, as TallyPrime does"
                        } else {
                            Write-Log "voucher Create $number -> created, voucher id $voucherId"
                        }
                    }
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
