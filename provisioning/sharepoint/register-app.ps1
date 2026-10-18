<#
.SYNOPSIS
Registers (or brings up to date) the Entra app registration AnyFS ships for SharePoint Online sign-in.

.DESCRIPTION
AnyFS's SharePoint Online source signs users in through the Microsoft identity platform, and has to say who
is asking: this app registration. Its Application (client) ID is compiled into the source as the default
(a user may paste their own organisation's registration instead). The registration lives in the Entra tenant
that owns it, which must outlive any test tenant: see provisioning/sharepoint/README.md for which one.

What it sets up, idempotently (run it again after changing the list of scopes):
  * a public client (no secret; sign-in happens in the user's browser or by device code),
  * for work and school accounts in any organisation (multi-tenant),
  * loopback and native-client redirect URIs, which is what MSAL's interactive and device-code flows use,
  * the delegated Microsoft Graph scopes the sources ask for: User.Read, offline_access,
    Files.ReadWrite.All, Sites.ReadWrite.All (SharePoint Online), FileStorageContainer.Selected (SharePoint
    Embedded; what it allows inside containers is granted per container type by whoever registers it),
  * the app's service principal in the owning tenant, so the owning tenant itself can consent and test.

It talks to Microsoft Graph directly (no modules): it signs you in with the device-code flow as the Microsoft
Graph Command Line Tools public client, which every tenant has, prints the code to enter at
https://microsoft.com/devicelogin, then waits for you. Sign in as an administrator of the owning tenant; the
first time, consent to Application.ReadWrite.All. Windows PowerShell 5.1 is enough. (The tenant's own display
name is not settable through Graph: that is Entra admin centre, Overview, Properties.)

.EXAMPLE
.\register-app.ps1 -TenantId 5a454c8a-e1a4-47e9-ac62-7fe98d92f5a6
#>
[CmdletBinding()]
param(
    # The owning tenant's id (or its *.onmicrosoft.com name).
    [Parameter(Mandatory)]
    [string]$TenantId,

    [string]$DisplayName = 'AnyFS',

    # The public client the sign-in runs as: Microsoft Graph Command Line Tools.
    [string]$SignInClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$graphUrl = 'https://graph.microsoft.com/v1.0'

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

# --- sign in: device code against the owning tenant --------------------------------------------------------
$authority = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0"
$scopes = 'https://graph.microsoft.com/User.Read https://graph.microsoft.com/Application.ReadWrite.All'
$device = Invoke-RestMethod -Method Post -Uri "$authority/devicecode" -Body @{ client_id = $SignInClientId; scope = $scopes }
Write-Host $device.message
$deadline = (Get-Date).AddSeconds([int]$device.expires_in)
$token = $null
while (-not $token) {
    if ((Get-Date) -gt $deadline) { throw 'The device code expired before the sign-in completed.' }
    Start-Sleep -Seconds ([int]$device.interval)
    try {
        $token = Invoke-RestMethod -Method Post -Uri "$authority/token" -Body @{
            grant_type  = 'urn:ietf:params:oauth:grant-type:device_code'
            client_id   = $SignInClientId
            device_code = $device.device_code
        }
    } catch {
        $body = Get-ErrorBody $_
        $code = try { ($body | ConvertFrom-Json).error } catch { '' }
        if ($code -eq 'authorization_pending') { continue }
        if ($code -eq 'slow_down') { Start-Sleep -Seconds 5; continue }
        throw "The sign-in failed: $body"
    }
}
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$me = Invoke-RestMethod -Uri "$graphUrl/me?`$select=userPrincipalName" -Headers $headers
Write-Host "Signed in to $TenantId as $($me.userPrincipalName)"

function Invoke-Graph {
    param([string]$Method, [string]$Path, $Body)
    $args = @{ Method = $Method; Uri = "$graphUrl/$Path"; Headers = $headers }
    if ($null -ne $Body) {
        $args.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10))
        $args.ContentType = 'application/json'
    }
    try { Invoke-RestMethod @args } catch { throw "$Method $Path failed: $(Get-ErrorBody $_)" }
}

# --- the delegated Graph scopes, resolved to their ids from Graph's own service principal ------------------
$graph = (Invoke-Graph GET "servicePrincipals?`$filter=appId eq '00000003-0000-0000-c000-000000000000'&`$select=appId,oauth2PermissionScopes").value | Select-Object -First 1
if (-not $graph) { throw 'The Microsoft Graph service principal was not found in the tenant.' }
$wanted = 'User.Read', 'offline_access', 'Files.ReadWrite.All', 'Sites.ReadWrite.All', 'FileStorageContainer.Selected'
$access = foreach ($name in $wanted) {
    $scope = $graph.oauth2PermissionScopes | Where-Object value -eq $name
    if (-not $scope) { throw "Microsoft Graph has no delegated scope named $name" }
    @{ id = $scope.id; type = 'Scope' }
}

# --- the registration ---------------------------------------------------------------------------------------
$settings = @{
    displayName            = $DisplayName
    signInAudience         = 'AzureADMultipleOrgs'
    isFallbackPublicClient = $true
    publicClient           = @{ redirectUris = @('http://localhost', 'https://login.microsoftonline.com/common/oauth2/nativeclient') }
    requiredResourceAccess = @(@{ resourceAppId = $graph.appId; resourceAccess = @($access) })
    notes                  = 'The client id AnyFS ships for its SharePoint Online and SharePoint Embedded sources (github.com/fiddyschmitt/AnyFS). Public client, delegated Graph scopes only. Managed by provisioning/sharepoint/register-app.ps1.'
}

$app = (Invoke-Graph GET "applications?`$filter=displayName eq '$DisplayName'").value | Select-Object -First 1
if ($app) {
    Invoke-Graph PATCH "applications/$($app.id)" $settings | Out-Null
    $app = Invoke-Graph GET "applications/$($app.id)"
    Write-Host "Updated the existing registration $($app.appId)"
} else {
    $app = Invoke-Graph POST 'applications' $settings
    Write-Host "Created the registration $($app.appId)"
}

$principal = (Invoke-Graph GET "servicePrincipals?`$filter=appId eq '$($app.appId)'").value | Select-Object -First 1
if (-not $principal) {
    $principal = Invoke-Graph POST 'servicePrincipals' @{ appId = $app.appId }
    Write-Host 'Created its service principal in the owning tenant'
}

[pscustomobject]@{
    TenantId       = $TenantId
    DisplayName    = $app.displayName
    ClientId       = $app.appId
    ObjectId       = $app.id
    SignInAudience = $app.signInAudience
    PublicClient   = $app.isFallbackPublicClient
    RedirectUris   = ($app.publicClient.redirectUris -join ' ')
    GraphScopes    = ($wanted -join ' ')
} | Format-List
