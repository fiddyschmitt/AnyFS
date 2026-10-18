<#
    AnyFS test lab: makes the test account a member (contribute) of the test site, which is what the tests
    sign in as, and a site collection administrator of it: SharePoint lets only those set a file's Created
    and Modified times (anyone else's values are ignored without a word), and the SDK's contract tests set
    times. Run as the farm's setup account by sp-provision.ps1 through a scheduled task, since SharePoint's
    cmdlets want a real logon of a farm administrator (a DSC Script resource run as a credential fails with
    "An item with the same key has already been added", 18-Oct-2026). Prints ANYFS-OK at the end.
#>
param(
    [Parameter(Mandatory)] [string]$WebUrl,
    [Parameter(Mandatory)] [string]$Site,
    [Parameter(Mandatory)] [string]$TestUser
)
$ErrorActionPreference = 'Stop'
Import-Module SharePointServer -ErrorAction SilentlyContinue
Add-PSSnapin Microsoft.SharePoint.PowerShell -ErrorAction SilentlyContinue

$web = Get-SPWeb "$WebUrl$Site"
try {
    $group = $web.AssociatedMemberGroup
    if ($group.Users | Where-Object { $_.LoginName -like "*|$TestUser" }) {
        "$TestUser is already a member of '$($group.Name)' on $($web.Url)"
    } else {
        $group.AddUser($web.EnsureUser($TestUser))
        "$TestUser added to '$($group.Name)' on $($web.Url)"
    }
} finally {
    $web.Dispose()
}

# the secondary site collection administrator (Set-SPSite is a farm-level cmdlet: setting IsSiteAdmin on the
# user needs the caller to be a site collection administrator already, which the setup account's process
# turned out not to count as, 18-Oct-2026)
$collection = Get-SPSite "$WebUrl$Site" -ErrorAction Stop
try {
    if ($collection.SecondaryContact -and $collection.SecondaryContact.LoginName -like "*|$TestUser") {
        "$TestUser is already the secondary site collection administrator"
    } else {
        Set-SPSite -Identity $collection -SecondaryOwnerAlias $TestUser -ErrorAction Stop
        "$TestUser made the secondary site collection administrator (to set file times)"
    }
} finally {
    $collection.Dispose()
}
'ANYFS-OK'
