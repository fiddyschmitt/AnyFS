<#
    AnyFS test lab: what the Exchange server holds, made in the Exchange Management Shell as the domain's
    Administrator (ex-provision.ps1 runs this through a scheduled task): the mailboxes from EX_MAILBOXES (the first,
    EX_TESTER, with an archive), the shared mailbox EX_SHARED the tester has full access to, an address list, a public
    folder mailbox with a public folder, EWS with Windows and Basic authentication, IMAP on; then the tester's mailbox
    seeded: the PST fixtures imported (dated mail with attachments, a contact, an appointment) and a few messages
    sent from the other mailbox. Every step is idempotent. Prints "configured: done" at the end.

    Inputs: SP_DOMAIN, SP_NETBIOS, EX_MAILBOXES, EX_TESTER, EX_SHARED, EX_ADMIN_USER (through the task's environment,
    which the provisioner writes to C:\anyfs-lab\ex-environment.json since a scheduled task starts clean).
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$settings = Get-Content 'C:\anyfs-lab\ex-environment.json' -Raw | ConvertFrom-Json
$domain = $settings.SP_DOMAIN
$netbios = $settings.SP_NETBIOS
$tester = $settings.EX_TESTER
$computer = $env:COMPUTERNAME

# The Exchange Management Shell as the real one opens: RemoteExchange.ps1 (it defines Connect-ExchangeServer) and a
# remote session to this server as the domain's Administrator. The in-process snap-in stands in when that session
# cannot be made (unsupported outside a lab, but every cmdlet is there). The first run of this lab (25-Oct-2026)
# called Connect-ExchangeServer without the dot-source and stopped on "not recognized".
$ErrorActionPreference = 'Continue'
$exchangeBin = Join-Path ([Environment]::GetEnvironmentVariable('ExchangeInstallPath', 'Machine')) 'bin'
try {
    . (Join-Path $exchangeBin 'RemoteExchange.ps1') *> $null
    Connect-ExchangeServer -auto -ClientApplication:ManagementShell *> $null
} catch {
    Write-Host "the remote Exchange shell did not connect ($($_.Exception.Message)); using the local snap-in"
}
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) {
    Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn
}
$ErrorActionPreference = 'Stop'
Write-Host "Exchange shell ready ($((Get-Command Get-Mailbox).CommandType) Get-Mailbox from $((Get-Command Get-Mailbox).Source))"

function Say($s) { Write-Host "$(Get-Date -Format HH:mm:ss) $s" }

# --- The mailboxes, in the lab's OU ------------------------------------------------------------------------------
$ou = "OU=AnyFS Lab,$((Get-ADDomain).DistinguishedName)"
if (-not (Get-ADOrganizationalUnit -Filter "Name -eq 'AnyFS Lab'" -ErrorAction SilentlyContinue)) {
    New-ADOrganizationalUnit -Name 'AnyFS Lab' -Path (Get-ADDomain).DistinguishedName -ProtectedFromAccidentalDeletion $false | Out-Null
}
$mailboxes = @{}
foreach ($pair in $settings.EX_MAILBOXES -split ';') {
    $name, $password = $pair -split '=', 2
    $mailboxes[$name] = $password
    if (-not (Get-Mailbox -Identity $name -ErrorAction SilentlyContinue)) {
        # Active Directory refuses a password holding three or more letters of a token of the display name ("Lab" is in
        # "AnyFS-lab-...": the first run, 25-Oct-2026), so the names say Exchange, not Lab
        $suffix = (Get-Culture).TextInfo.ToTitleCase(($name -replace '^ex_', ''))
        $display = switch ($name) { $tester { 'Exchange Tester' } default { 'Exchange ' + $suffix } }
        New-Mailbox -Name $name -DisplayName $display -UserPrincipalName "$name@$domain" -SamAccountName $name -OrganizationalUnit $ou `
            -Password (ConvertTo-SecureString $password -AsPlainText -Force) -ResetPasswordOnNextLogon $false -FirstName 'Exchange' -LastName $suffix | Out-Null
        Say "mailbox $name made"
    }
    Set-ADAccountPassword -Identity $name -Reset -NewPassword (ConvertTo-SecureString $password -AsPlainText -Force)
    Set-ADUser -Identity $name -PasswordNeverExpires $true -Enabled $true
}

# an archive for the tester
if (-not (Get-Mailbox -Identity $tester).ArchiveDatabase) {
    Enable-Mailbox -Identity $tester -Archive | Out-Null
    Say "archive enabled for $tester"
}

# the shared mailbox, the tester with full access
$shared = $settings.EX_SHARED
$sharedAlias = ($shared -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
if (-not (Get-Mailbox -Identity $sharedAlias -ErrorAction SilentlyContinue)) {
    New-Mailbox -Shared -Name $shared -Alias $sharedAlias -DisplayName $shared -OrganizationalUnit $ou | Out-Null
    Say "shared mailbox '$shared' made"
}
if (-not (Get-MailboxPermission -Identity $sharedAlias -User $tester -ErrorAction SilentlyContinue | Where-Object { $_.AccessRights -contains 'FullAccess' })) {
    Add-MailboxPermission -Identity $sharedAlias -User $tester -AccessRights FullAccess -AutoMapping $true | Out-Null
    Say "$tester has full access to '$shared'"
}

# an address list of the lab's people
if (-not (Get-AddressList -Identity 'Lab People' -ErrorAction SilentlyContinue)) {
    New-AddressList -Name 'Lab People' -RecipientFilter "(RecipientType -eq 'UserMailbox') -and (Name -like 'ex_*')" | Out-Null
    Update-AddressList -Identity 'Lab People' -ErrorAction SilentlyContinue
    Say "address list 'Lab People' made"
}

# directory details on ex_other so a card has a title, department, office, phones and an address (read live over NSPI);
# idempotent, and kept here so a rebuilt lab still shows them (they were set by hand when the address books were first tried)
if (Get-Mailbox -Identity 'ex_other' -ErrorAction SilentlyContinue) {
    Set-User -Identity 'ex_other' -Title 'Lab Engineer' -Department 'Testing' -Company 'AnyFS Lab' -Office 'Building 4, 2.10' `
        -Phone '+61 2 5550 0101' -MobilePhone '+61 400 000 001' `
        -StreetAddress '1 Lab Street' -City 'Sydney' -StateOrProvince 'NSW' -PostalCode '2000' -CountryOrRegion 'AU' | Out-Null
    Say 'ex_other given a title, phones, office and address'
}

# public folders: a mailbox for them and one folder
if (-not (Get-Mailbox -PublicFolder -ErrorAction SilentlyContinue)) {
    New-Mailbox -PublicFolder -Name 'PF Mailbox 1' -OrganizationalUnit $ou | Out-Null
    Say 'public folder mailbox made'
}
if (-not (Get-PublicFolder -Identity '\Lab Notices' -ErrorAction SilentlyContinue)) {
    New-PublicFolder -Name 'Lab Notices' | Out-Null
    Enable-MailPublicFolder -Identity '\Lab Notices' -ErrorAction SilentlyContinue
    Add-PublicFolderClientPermission -Identity '\Lab Notices' -User Default -AccessRights Author -ErrorAction SilentlyContinue
    Say "public folder 'Lab Notices' made"
}

# EWS: Windows and Basic authentication over the server's own (self-signed) certificate; IMAP on
Get-WebServicesVirtualDirectory -Server $computer | Set-WebServicesVirtualDirectory -WindowsAuthentication $true -BasicAuthentication $true `
    -InternalUrl "https://$computer.$domain/EWS/Exchange.asmx" -ExternalUrl "https://$computer.$domain/EWS/Exchange.asmx"
Get-OwaVirtualDirectory -Server $computer | Set-OwaVirtualDirectory -InternalUrl "https://$computer.$domain/owa" -ExternalUrl "https://$computer.$domain/owa"
Set-Service MSExchangeIMAP4 -StartupType Automatic
Set-Service MSExchangeIMAP4BE -StartupType Automatic
Start-Service MSExchangeIMAP4BE
Start-Service MSExchangeIMAP4
Set-ImapSettings -LoginType PlainTextLogin -ExternalConnectionSettings "$computer.${domain}:993:SSL", "$computer.${domain}:143:TLS" -ErrorAction SilentlyContinue
Say 'EWS and IMAP configured'

# --- The tester's mailbox seeded ------------------------------------------------------------------------------------
# the PST fixtures: imported once each (dated mail with attachments, a contact, an appointment)
if (-not (Get-ManagementRoleAssignment -RoleAssignee $settings.EX_ADMIN_USER -Role 'Mailbox Import Export' -ErrorAction SilentlyContinue)) {
    New-ManagementRoleAssignment -Role 'Mailbox Import Export' -User $settings.EX_ADMIN_USER | Out-Null
    Say 'import role granted'
}
if (-not (Get-Command Get-MailboxImportRequest -ErrorAction SilentlyContinue)) {
    # the remote shell only carries the cmdlets of the roles held when its session was made: a new session for the
    # role just granted, and the in-process snap-in (no RBAC) when the role has not reached it yet (first run, 25-Oct-2026)
    $ErrorActionPreference = 'Continue'
    try { Connect-ExchangeServer -auto -ForceNewConnection -ClientApplication:ManagementShell *> $null } catch { }
    if (-not (Get-Command Get-MailboxImportRequest -ErrorAction SilentlyContinue)) {
        Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn
        Say 'import cmdlets from the local snap-in'
    }
    $ErrorActionPreference = 'Stop'
}
foreach ($pst in Get-ChildItem 'C:\anyfs-lab\pst-local' -Filter *.pst -ErrorAction SilentlyContinue) {
    $requestName = "fixture-$($pst.BaseName)"
    if (-not (Get-MailboxImportRequest -Name $requestName -Mailbox $tester -ErrorAction SilentlyContinue)) {
        # best effort: on the first build (25-Oct-2026) the request was refused with "Unable to open PST file ... Access to
        # the path is denied" although the share and the folder grant Exchange Trusted Subsystem modify rights; the
        # seeded messages below do not depend on it, so a refusal is said and the rest goes on
        try {
            New-MailboxImportRequest -Name $requestName -Mailbox $tester -FilePath "\\$computer\pst\$($pst.Name)" -TargetRootFolder "Imported $($pst.BaseName)" | Out-Null
            Say "importing $($pst.Name) into $tester"
        } catch {
            Say "WARNING: importing $($pst.Name) was refused: $($_.Exception.Message -replace '\s+', ' ')"
        }
    }
}

# a handful of messages from the other mailbox, attachments on some; dates are the time of sending
$other = ($mailboxes.Keys | Where-Object { $_ -ne $tester } | Select-Object -First 1)
if ($other -and -not (Test-Path 'C:\anyfs-lab\exchange-seeded')) {
    $csv = Join-Path $env:TEMP 'figures.csv'
    [IO.File]::WriteAllText($csv, "quarter,amount`nQ3,42`nQ4,58`n")
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Service MSExchangeFrontEndTransport).Status -ne 'Running' -or (Get-Service MSExchangeTransport).Status -ne 'Running') {
        if ((Get-Date) -gt $deadline) { throw 'the transport services are not running' }
        Start-Sleep -Seconds 10
    }
    $from = "$other@$domain"
    $to = "$tester@$domain"
    $sent = 0
    foreach ($spec in @(
        @{ Subject = 'Welcome to the lab'; Body = '<p>Hello from the <b>Exchange</b> lab.</p>'; Attach = $null },
        @{ Subject = 'Quarterly figures'; Body = '<p>Figures attached.</p>'; Attach = $csv },
        @{ Subject = 'RE: Project kick-off'; Body = '<p>In the Inbox this time.</p>'; Attach = $null },
        @{ Subject = 'Umlauts: Übung macht den Meister'; Body = '<p>naïve café – 日本語</p>'; Attach = $null })) {
        $arguments = @{ SmtpServer = $computer; Port = 25; From = $from; To = $to; Subject = $spec.Subject; Body = $spec.Body; BodyAsHtml = $true; Encoding = ([Text.Encoding]::UTF8) }
        if ($spec.Attach) { $arguments.Attachments = $spec.Attach }
        try { Send-MailMessage @arguments; $sent++ } catch { Say "sending '$($spec.Subject)': $($_.Exception.Message)" }
        Start-Sleep -Seconds 2
    }
    Say "$sent messages sent from $other to $tester"
    Set-Content -Path 'C:\anyfs-lab\exchange-seeded' -Value (Get-Date).ToString('o') -Encoding ascii
}

$count = (Get-Mailbox -ResultSize Unlimited | Measure-Object).Count
Say "mailboxes: $count; EWS: $((Get-WebServicesVirtualDirectory -Server $computer).InternalUrl); IMAP: $((Get-Service MSExchangeIMAP4).Status)"
Write-Host 'configured: done'
