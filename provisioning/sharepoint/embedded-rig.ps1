<#
.SYNOPSIS
Builds, or brings up to date, the SharePoint Embedded test rig in a Microsoft 365 tenant: the owning app
registration, a trial container type, its registration in the tenant, and the containers the tests use.

.DESCRIPTION
SharePoint Embedded keeps files in *containers* that belong to a *container type*, which in turn belongs to
an app registration (the owning app) in the tenant that creates it. A trial container type costs nothing,
needs no Azure billing, lasts 30 days, allows five containers of 1 GB, and a tenant has one at a time; so
this script is run again when the previous one has expired (the tests say so). What it does, idempotently:

  1. Signs the administrator in, in the browser (the authorization-code flow with PKCE on a loopback port,
     as the Microsoft Graph Command Line Tools public client, which every tenant has). The device-code flow
     is not used: tenants often block it. The administrator must be a SharePoint Embedded Administrator or
     Global Administrator of the tenant. Then makes the test account (-TestUser), a plain member with a
     licence that includes SharePoint, and waits for its OneDrive: Graph lists containers only for accounts
     with a OneDrive, and only the ones they are direct members of. (The sandbox's administrator cannot be
     that account: a licence given to it was gone again within minutes, every time.)
  2. Creates or updates the owning app registration (single tenant, a public client too, with the Graph
     permissions SharePoint Embedded needs: FileStorageContainer.Selected and
     FileStorageContainerTypeReg.Selected, delegated and application), its service principal, and consents
     to those permissions for the tenant. Consents for every user to the scopes AnyFS's shipped
     registration (the guest app) asks for, since a plain member cannot consent to an unverified app. Adds
     a client secret to the owning app that lasts two days: only containers can be created with a
     confidential client, and only this script creates them.
  3. Creates the trial container type owned by that app (or keeps the one it owns; -Recreate replaces it),
     then registers it in the tenant as the owning app: the owning app gets full delegated and app-only
     access to its containers, and the guest app (what the tests and the AnyFS source sign in with) gets
     full delegated access. That is the arrangement AnyFS documents for its users: their administrator
     registers AnyFS on the container type.
  4. Creates the containers (as the owning app, app-only), activates them, and makes the administrator and
     the test account their owners, since Graph lists only containers the signed-in user is a direct
     member of.

Nothing is stored: the secret lives in this process. The end of the run prints the ids the tests need
(ANYFS_TEST_SPE_*). Windows PowerShell 5.1 is enough; no modules.

.EXAMPLE
.\embedded-rig.ps1 -TenantId y0w7606wv.onmicrosoft.com -Admin admin@y0w7606wv.onmicrosoft.com
#>
[CmdletBinding()]
param(
    # The tenant's id or its *.onmicrosoft.com name.
    [Parameter(Mandatory)]
    [string]$TenantId,

    # The administrator who signs in: pre-fills the sign-in page and becomes an owner of the containers.
    [Parameter(Mandatory)]
    [string]$Admin,

    [string]$OwnerAppName = 'AnyFS lab (Embedded owner)',

    [string]$ContainerTypeName = 'AnyFS lab',

    # The containers to have, by display name. The tests pin the first.
    [string[]]$Containers = @('anyfs-test', 'anyfs-other'),

    # The app granted delegated access on the container type besides the owner: AnyFS's shipped registration.
    [string]$GuestAppId = '824dc5b9-f3c4-42b7-871d-6f0de8bc0a4a',

    # The public client the administrator's sign-in runs as: Microsoft Graph Command Line Tools.
    [string]$SignInClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e',

    # Deletes the containers and the trial container type this rig owns, then makes new ones.
    [switch]$Recreate,

    # How long to wait for the administrator to finish the browser sign-in.
    [int]$SignInMinutes = 30,

    # The account the tests sign in as: a plain member made here (licensed, an owner of the containers), since
    # the sandbox's administrator loses any licence given to it within minutes and so has no OneDrive, without
    # which Graph lists no containers. The first time it is made, its initial password is printed once; the
    # person sets a new one at the first sign-in (tests/fixtures/SharePointSignIn --account <this user>).
    [string]$TestUser = 'anyfs-tester'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Web
$graphUrl = 'https://graph.microsoft.com/v1.0'
$authority = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0"
$graphAppId = '00000003-0000-0000-c000-000000000000'

function Get-ErrorBody {
    param($ErrorRecord)
    if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) { return $ErrorRecord.ErrorDetails.Message }
    $response = $ErrorRecord.Exception.Response
    if ($response) {
        $reader = New-Object IO.StreamReader($response.GetResponseStream())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    return $ErrorRecord.Exception.Message
}

function Invoke-Graph {
    param([string]$Method, [string]$Path, $Body, [string]$Token)
    $call = @{ Method = $Method; Uri = "$graphUrl/$Path"; Headers = @{ Authorization = "Bearer $Token" } }
    if ($null -ne $Body) {
        $call.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10))
        $call.ContentType = 'application/json'
    }
    try { Invoke-RestMethod @call } catch { throw "$Method $Path failed: $(Get-ErrorBody $_)" }
}

# Retries a call while a just-made object or consent propagates (Entra and Graph take seconds to minutes).
function Invoke-Patiently {
    param([scriptblock]$Call, [string]$Because, [int]$Tries = 12, [int]$DelaySeconds = 15)
    for ($i = 1; ; $i++) {
        try { return & $Call }
        catch {
            if ($i -ge $Tries) { throw }
            Write-Host "  not yet ($($_.Exception.Message.Split("`n")[0].Trim())); waiting $DelaySeconds s for $Because ($i/$Tries)"
            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

function ConvertTo-Base64Url([byte[]]$Bytes) { [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

# The authorization-code flow with PKCE, the reply caught on a loopback port: a sign-in page in the browser.
function Get-BrowserToken {
    param([string]$ClientId, [string]$Scopes, [string]$LoginHint)
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        $redirect = "http://localhost:$($listener.LocalEndpoint.Port)/"
        $random = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
        $bytes = New-Object byte[] 48
        $random.GetBytes($bytes)
        $verifier = ConvertTo-Base64Url $bytes
        $challenge = ConvertTo-Base64Url ([System.Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)))
        $state = [Guid]::NewGuid().ToString('N')
        $url = "$authority/authorize?client_id=$ClientId&response_type=code&response_mode=query" +
            "&redirect_uri=$([Uri]::EscapeDataString($redirect))&scope=$([Uri]::EscapeDataString($Scopes))" +
            "&code_challenge=$challenge&code_challenge_method=S256&state=$state&prompt=select_account"
        if ($LoginHint) { $url += "&login_hint=$([Uri]::EscapeDataString($LoginHint))" }
        Write-Host "Sign in as $LoginHint in the browser that opens (consent to the permissions it lists). If no browser appears, open:"
        Write-Host "  $url"
        Start-Process $url | Out-Null

        # browsers also open connections they never use, and ask for a favicon: only a request with the
        # code (or an error) counts; the rest get a 404 or time out
        $deadline = (Get-Date).AddMinutes($SignInMinutes)
        $reply = $null
        while (-not $reply) {
            while (-not $listener.Pending()) {
                if ((Get-Date) -gt $deadline) { throw "Nobody finished the browser sign-in within $SignInMinutes minutes." }
                Start-Sleep -Milliseconds 250
            }
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $stream.ReadTimeout = 5000
                $reader = New-Object IO.StreamReader($stream)
                $requestLine = $reader.ReadLine()
                if (-not $requestLine) { continue }
                while ($true) {
                    $line = $reader.ReadLine()
                    if ($null -eq $line -or $line -eq '') { break }
                }
                $query = ([Uri]("http://localhost" + $requestLine.Split(' ')[1])).Query
                $parsed = [System.Web.HttpUtility]::ParseQueryString($query)
                if ($parsed['code'] -or $parsed['error']) {
                    $status = '200 OK'
                    $html = if ($parsed['code']) {
                        '<html><body style="font-family:sans-serif"><h3>AnyFS lab: signed in.</h3><p>You can close this tab.</p></body></html>'
                    } else {
                        "<html><body style=""font-family:sans-serif""><h3>AnyFS lab: the sign-in failed.</h3><p>$([System.Web.HttpUtility]::HtmlEncode($parsed['error'])): $([System.Web.HttpUtility]::HtmlEncode($parsed['error_description']))</p></body></html>"
                    }
                } else {
                    $status = '404 Not Found'
                    $html = '<html><body>not here</body></html>'
                }
                $body = [Text.Encoding]::UTF8.GetBytes($html)
                $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                $stream.Write($header, 0, $header.Length)
                $stream.Write($body, 0, $body.Length)
                $stream.Flush()
                if ($status -eq '200 OK') { $reply = $parsed }
            } catch [System.IO.IOException] {
                continue
            } finally {
                $client.Close()
            }
        }
        if ($reply['state'] -ne $state) { throw 'The sign-in reply carried the wrong state.' }
        if (-not $reply['code']) { throw "The sign-in failed: $($reply['error']): $($reply['error_description'])" }
        try {
            return Invoke-RestMethod -Method Post -Uri "$authority/token" -Body @{
                client_id = $ClientId; grant_type = 'authorization_code'; code = $reply['code']
                redirect_uri = $redirect; code_verifier = $verifier; scope = $Scopes
            }
        } catch { throw "Redeeming the sign-in failed: $(Get-ErrorBody $_)" }
    } finally {
        $listener.Stop()
    }
}

# An app-only token for the owning app (client credentials with its secret).
function Get-AppToken {
    param([string]$ClientId, [string]$Secret)
    try {
        (Invoke-RestMethod -Method Post -Uri "$authority/token" -Body @{
            client_id = $ClientId; client_secret = $Secret; grant_type = 'client_credentials'; scope = 'https://graph.microsoft.com/.default'
        }).access_token
    } catch { throw "The owning app's token request failed: $(Get-ErrorBody $_)" }
}

# --- 1. the administrator ---------------------------------------------------------------------------------
$adminScopes = 'https://graph.microsoft.com/User.Read https://graph.microsoft.com/Application.ReadWrite.All ' +
    'https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All https://graph.microsoft.com/DelegatedPermissionGrant.ReadWrite.All ' +
    'https://graph.microsoft.com/FileStorageContainerType.Manage.All https://graph.microsoft.com/Organization.Read.All ' +
    'https://graph.microsoft.com/User.ReadWrite.All https://graph.microsoft.com/Files.Read.All'
$adminToken = (Get-BrowserToken -ClientId $SignInClientId -Scopes $adminScopes -LoginHint $Admin).access_token
$me = Invoke-Graph GET 'me?$select=userPrincipalName,id' -Token $adminToken
Write-Host "Signed in to $TenantId as $($me.userPrincipalName)"
$adminUpn = $me.userPrincipalName

$graphSp = (Invoke-Graph GET "servicePrincipals?`$filter=appId eq '$graphAppId'&`$select=id,appId,oauth2PermissionScopes,appRoles" -Token $adminToken).value | Select-Object -First 1
if (-not $graphSp) { throw 'The Microsoft Graph service principal was not found in the tenant.' }
function Get-GraphScope([string]$Name) {
    $scope = $graphSp.oauth2PermissionScopes | Where-Object value -eq $Name
    if (-not $scope) { throw "Microsoft Graph has no delegated scope named $Name" }
    $scope
}
function Get-GraphRole([string]$Name) {
    $role = $graphSp.appRoles | Where-Object value -eq $Name
    if (-not $role) { throw "Microsoft Graph has no application permission named $Name" }
    $role
}

# --- 1b. the test account: a member with a licence (SharePoint) and a OneDrive ---------------------------------
# Graph's container listing answers nothing for an account without a OneDrive (a container opened by id works).
# The administrator cannot be that account: a licence given to it is gone again within minutes (18-Oct-2026).
$domain = $adminUpn.Split('@')[1]
$testUpn = "$TestUser@$domain"
$tester = (Invoke-Graph GET "users?`$filter=userPrincipalName eq '$testUpn'&`$select=id,userPrincipalName,assignedLicenses,usageLocation" -Token $adminToken).value | Select-Object -First 1
$initialPassword = $null
if (-not $tester) {
    $bytes = New-Object byte[] 18
    (New-Object System.Security.Cryptography.RNGCryptoServiceProvider).GetBytes($bytes)
    $initialPassword = 'Af-' + ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', 'x').Substring(0, 16) + '!'
    $tester = Invoke-Graph POST 'users' @{
        accountEnabled    = $true
        displayName       = 'AnyFS tester'
        mailNickname      = $TestUser
        userPrincipalName = $testUpn
        usageLocation     = 'AU'
        passwordProfile   = @{ forceChangePasswordNextSignIn = $true; password = $initialPassword }
    } -Token $adminToken
    Write-Host "Test account: created $testUpn"
} else {
    Write-Host "Test account: $testUpn exists"
    if (-not $tester.usageLocation) { Invoke-Graph PATCH "users/$($tester.id)" @{ usageLocation = 'AU' } -Token $adminToken | Out-Null }
}

function Test-HasSharePoint($sku) { @($sku.servicePlans | Where-Object { $_.servicePlanName -like 'SHAREPOINT*' -and $_.provisioningStatus -ne 'Disabled' }).Count -gt 0 }
$skus = @((Invoke-Graph GET 'subscribedSkus' -Token $adminToken).value)
$licensed = @($tester.assignedLicenses | ForEach-Object { $id = $_.skuId; $skus | Where-Object { $_.skuId -eq $id } } | Where-Object { Test-HasSharePoint $_ })
if ($licensed.Count -eq 0) {
    $sku = $skus | Where-Object { (Test-HasSharePoint $_) -and $_.capabilityStatus -eq 'Enabled' -and ($_.prepaidUnits.enabled - $_.consumedUnits) -gt 0 } |
        Sort-Object { $_.prepaidUnits.enabled - $_.consumedUnits } -Descending | Select-Object -First 1
    if (-not $sku) { throw "No licence with SharePoint is free in the tenant for $testUpn, and without one Graph lists no containers for the account." }
    Invoke-Graph POST "users/$($tester.id)/assignLicense" @{ addLicenses = @(@{ skuId = $sku.skuId; disabledPlans = @() }); removeLicenses = @() } -Token $adminToken | Out-Null
    Write-Host "  licence: gave $testUpn $($sku.skuPartNumber) ($($sku.prepaidUnits.enabled - $sku.consumedUnits - 1) left)"
    # read it back: an assignment the administrator got had vanished again within half an hour (18-Oct-2026)
    Invoke-Patiently -Because 'the licence to show on the account' -Tries 12 -DelaySeconds 10 -Call {
        $u = Invoke-Graph GET "users/$($tester.id)?`$select=assignedLicenses" -Token $adminToken
        if (@($u.assignedLicenses).Count -eq 0) { throw 'no licence on the account yet' }
        $u
    } | Out-Null
    Write-Host '  licence: on the account'
} else {
    Write-Host "  licence: $testUpn has $($licensed[0].skuPartNumber)"
}
# asking for the drive provisions it, once the licence has reached SharePoint (minutes, sometimes longer: not fatal)
try {
    $drive = Invoke-Patiently -Because 'OneDrive to be provisioned' -Tries 15 -DelaySeconds 20 -Call { Invoke-Graph GET "users/$($tester.id)/drive?`$select=id,webUrl" -Token $adminToken }
    Write-Host "  OneDrive: $($drive.webUrl)"
} catch {
    Write-Warning "OneDrive is not provisioned yet for $testUpn ($($_.Exception.Message.Split("`n")[0])). Until it is, Graph lists no containers for the account (a container pinned by id still works); run this again later."
}

# --- 2. the owning app ------------------------------------------------------------------------------------
$delegated = 'User.Read', 'offline_access', 'FileStorageContainer.Selected', 'FileStorageContainerTypeReg.Selected'
$application = 'FileStorageContainer.Selected', 'FileStorageContainerTypeReg.Selected'
$access = @()
$access += foreach ($name in $delegated) { @{ id = (Get-GraphScope $name).id; type = 'Scope' } }
$access += foreach ($name in $application) { @{ id = (Get-GraphRole $name).id; type = 'Role' } }
$appSettings = @{
    displayName            = $OwnerAppName
    signInAudience         = 'AzureADMyOrg'
    isFallbackPublicClient = $true
    publicClient           = @{ redirectUris = @('http://localhost') }
    requiredResourceAccess = @(@{ resourceAppId = $graphAppId; resourceAccess = @($access) })
    notes                  = 'Owns the AnyFS test rig''s SharePoint Embedded container type. Made by provisioning/sharepoint/embedded-rig.ps1 (github.com/fiddyschmitt/AnyFS); its secret lasts two days and is used only while that script runs.'
}
$app = (Invoke-Graph GET "applications?`$filter=displayName eq '$($OwnerAppName.Replace("'", "''"))'" -Token $adminToken).value | Select-Object -First 1
if ($app) {
    Invoke-Graph PATCH "applications/$($app.id)" $appSettings -Token $adminToken | Out-Null
    $app = Invoke-Graph GET "applications/$($app.id)" -Token $adminToken
    Write-Host "Owning app: updated $($app.displayName) ($($app.appId))"
} else {
    $app = Invoke-Graph POST 'applications' $appSettings -Token $adminToken
    Write-Host "Owning app: created $($app.displayName) ($($app.appId))"
}

$sp = (Invoke-Graph GET "servicePrincipals?`$filter=appId eq '$($app.appId)'" -Token $adminToken).value | Select-Object -First 1
if (-not $sp) {
    $sp = Invoke-Patiently -Because 'the new app to be visible' -Tries 6 -DelaySeconds 10 -Call { Invoke-Graph POST 'servicePrincipals' @{ appId = $app.appId } -Token $adminToken }
    Write-Host '  created its service principal'
}

# admin consent: the application permissions as app role assignments, the delegated ones as one grant for all users
$assigned = (Invoke-Graph GET "servicePrincipals/$($sp.id)/appRoleAssignments" -Token $adminToken).value
foreach ($name in $application) {
    $role = Get-GraphRole $name
    if (-not ($assigned | Where-Object appRoleId -eq $role.id)) {
        Invoke-Patiently -Because 'the service principal to settle' -Tries 6 -DelaySeconds 10 -Call {
            Invoke-Graph POST "servicePrincipals/$($sp.id)/appRoleAssignments" @{ principalId = $sp.id; resourceId = $graphSp.id; appRoleId = $role.id } -Token $adminToken
        } | Out-Null
        Write-Host "  consented to application permission $name"
    }
}
$grant = (Invoke-Graph GET "oauth2PermissionGrants?`$filter=clientId eq '$($sp.id)' and consentType eq 'AllPrincipals'" -Token $adminToken).value | Where-Object resourceId -eq $graphSp.id | Select-Object -First 1
$scopeString = $delegated -join ' '
if ($grant) {
    if ($grant.scope.Trim() -ne $scopeString) {
        Invoke-Graph PATCH "oauth2PermissionGrants/$($grant.id)" @{ scope = $scopeString } -Token $adminToken | Out-Null
        Write-Host "  consented to delegated permissions $scopeString"
    }
} else {
    Invoke-Graph POST 'oauth2PermissionGrants' @{ clientId = $sp.id; consentType = 'AllPrincipals'; resourceId = $graphSp.id; scope = $scopeString } -Token $adminToken | Out-Null
    Write-Host "  consented to delegated permissions $scopeString"
}

# admin consent for the guest app (AnyFS's shipped registration) too, for every user: the tenant's policy keeps a
# plain member from consenting to an unverified app ("Need admin approval", 18-Oct-2026). The scopes are the ones
# AnyFS's SharePoint sources ask for; the grant is what a customer's administrator gives once as well.
$guestSp = (Invoke-Graph GET "servicePrincipals?`$filter=appId eq '$GuestAppId'" -Token $adminToken).value | Select-Object -First 1
if (-not $guestSp) {
    $guestSp = Invoke-Graph POST 'servicePrincipals' @{ appId = $GuestAppId } -Token $adminToken
    Write-Host "Guest app: created the service principal of $GuestAppId in the tenant"
}
$guestScopes = 'User.Read offline_access Files.ReadWrite.All Sites.ReadWrite.All FileStorageContainer.Selected'
$guestGrant = (Invoke-Graph GET "oauth2PermissionGrants?`$filter=clientId eq '$($guestSp.id)' and consentType eq 'AllPrincipals'" -Token $adminToken).value | Where-Object resourceId -eq $graphSp.id | Select-Object -First 1
if ($guestGrant) {
    $have = $guestGrant.scope -split ' ' | Where-Object { $_ }
    $want = ($have + ($guestScopes -split ' ')) | Sort-Object -Unique
    if (@($want).Count -ne @($have).Count) {
        Invoke-Graph PATCH "oauth2PermissionGrants/$($guestGrant.id)" @{ scope = ($want -join ' ') } -Token $adminToken | Out-Null
        Write-Host "Guest app: consented for all users to $($want -join ' ')"
    } else {
        Write-Host "Guest app: consent for all users in place ($($have -join ' '))"
    }
} else {
    Invoke-Graph POST 'oauth2PermissionGrants' @{ clientId = $guestSp.id; consentType = 'AllPrincipals'; resourceId = $graphSp.id; scope = $guestScopes } -Token $adminToken | Out-Null
    Write-Host "Guest app: consented for all users to $guestScopes"
}

# a fresh two-day secret, older ones of this script's making removed
foreach ($old in @($app.passwordCredentials | Where-Object { $_.displayName -like 'embedded-rig *' })) {
    Invoke-Graph POST "applications/$($app.id)/removePassword" @{ keyId = $old.keyId } -Token $adminToken | Out-Null
}
$secret = (Invoke-Graph POST "applications/$($app.id)/addPassword" @{
    passwordCredential = @{ displayName = "embedded-rig $(Get-Date -Format yyyy-MM-dd)"; endDateTime = (Get-Date).ToUniversalTime().AddDays(2).ToString('o') }
} -Token $adminToken).secretText
Write-Host '  added a two-day client secret'
$appToken = Invoke-Patiently -Because 'the secret and the consents to propagate' -Call { Get-AppToken -ClientId $app.appId -Secret $secret }
Write-Host '  the owning app can sign in on its own'

# --- 3. the trial container type and its registration -------------------------------------------------------
$types = @((Invoke-Graph GET 'storage/fileStorage/containerTypes' -Token $adminToken).value)
$type = $types | Where-Object owningAppId -eq $app.appId | Select-Object -First 1
if ($type -and $Recreate) {
    Write-Host "Container type: removing $($type.name) ($($type.id)) and its containers"
    try {
        foreach ($c in @((Invoke-Graph GET "storage/fileStorage/containers?`$filter=containerTypeId eq $($type.id)" -Token $appToken).value)) {
            Invoke-Graph POST "storage/fileStorage/containers/$($c.id)/permanentDelete" -Token $appToken | Out-Null
            Write-Host "  deleted container $($c.displayName)"
        }
    } catch { Write-Warning "listing or deleting the containers failed ($($_.Exception.Message)); trying to delete the type anyway" }
    Invoke-Graph DELETE "storage/fileStorage/containerTypes/$($type.id)" -Token $adminToken | Out-Null
    $type = $null
}
if (-not $type) {
    $otherTrial = $types | Where-Object { $_.billingClassification -eq 'trial' -and $_.owningAppId -ne $app.appId }
    if ($otherTrial) {
        throw "The tenant already has a trial container type ($($otherTrial.name), $($otherTrial.id), owned by app $($otherTrial.owningAppId)) and allows one at a time; delete it and its containers first."
    }
    $type = Invoke-Graph POST 'storage/fileStorage/containerTypes' @{ name = $ContainerTypeName; owningAppId = $app.appId; billingClassification = 'trial' } -Token $adminToken
    Write-Host "Container type: created $($type.name) ($($type.id)), trial, expires $($type.expirationDateTime)"
} else {
    Write-Host "Container type: keeping $($type.name) ($($type.id)), $($type.billingClassification), expires $($type.expirationDateTime)"
}

$registration = @{
    applicationPermissionGrants = @(
        @{ appId = $app.appId; delegatedPermissions = @('full'); applicationPermissions = @('full') },
        @{ appId = $GuestAppId; delegatedPermissions = @('full'); applicationPermissions = @('none') }
    )
}
Invoke-Patiently -Because 'the container type to be registrable (a new tenant can take an hour)' -Tries 20 -DelaySeconds 30 -Call {
    Invoke-Graph PUT "storage/fileStorage/containerTypeRegistrations/$($type.id)" $registration -Token $appToken
} | Out-Null
Write-Host "  registered in the tenant: $($app.appId) full; $GuestAppId delegated full"

# --- 4. the containers --------------------------------------------------------------------------------------
$existing = @((Invoke-Patiently -Because 'the registration to take effect' -Tries 10 -DelaySeconds 30 -Call {
    Invoke-Graph GET "storage/fileStorage/containers?`$filter=containerTypeId eq $($type.id)" -Token $appToken
}).value)
$made = foreach ($name in $Containers) {
    $container = $existing | Where-Object displayName -eq $name | Select-Object -First 1
    if ($container) {
        Write-Host "Container: keeping $name ($($container.id))"
    } else {
        $container = Invoke-Graph POST 'storage/fileStorage/containers' @{ displayName = $name; description = 'AnyFS test rig'; containerTypeId = $type.id } -Token $appToken
        Write-Host "Container: created $name ($($container.id))"
    }
    $container = Invoke-Graph GET "storage/fileStorage/containers/$($container.id)" -Token $appToken
    if ($container.status -ne 'active') {
        Invoke-Graph POST "storage/fileStorage/containers/$($container.id)/activate" -Token $appToken | Out-Null
        Write-Host '  activated'
    }
    foreach ($owner in @($adminUpn, $testUpn)) {
        Invoke-Graph POST "storage/fileStorage/containers/$($container.id)/permissions" @{
            '@microsoft.graph.conflictBehavior' = 'replace'
            roles = @('owner')
            grantedToV2 = @{ user = @{ userPrincipalName = $owner } }
        } -Token $appToken | Out-Null
        Write-Host "  $owner is an owner"
    }
    $container
}

Write-Host ''
[pscustomobject]@{
    Tenant              = $TenantId
    OwningApp           = "$($app.displayName) ($($app.appId))"
    ContainerType       = "$($type.name) ($($type.id)), $($type.billingClassification), expires $($type.expirationDateTime)"
    GuestApp            = "$GuestAppId (delegated: full)"
    Containers          = (($made | ForEach-Object { "$($_.displayName) ($($_.id))" }) -join '; ')
    Owners              = "$adminUpn, $testUpn"
    TestAccount         = if ($initialPassword) { "$testUpn, initial password (shown once; a new one is set at the first sign-in): $initialPassword" } else { $testUpn }
} | Format-List
Write-Host 'For the live tests (tests/AnyFS.Sources.Tests, SharePointEmbedded*):'
Write-Host "  ANYFS_TEST_SPE_CONTAINER_TYPE=$($type.id)"
Write-Host "  ANYFS_TEST_SPE_CONTAINER=$($Containers[0])"
Write-Host "  ANYFS_TEST_SPE_ACCOUNT=$testUpn"
Write-Host "and a sign-in as $testUpn from tests/fixtures/SharePointSignIn --account $testUpn (it asks for the Embedded scope too)."
