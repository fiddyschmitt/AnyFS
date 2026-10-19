# SharePoint test rigs

What the SharePoint source is developed and tested against, and how each piece was made. It serves
SharePoint Online and SharePoint Embedded (both Microsoft 365, reached through Microsoft Graph after an
app sign-in, or Online through its REST API on a web session) and SharePoint Server on-premises (reached
through its REST API with a Windows account). Nothing here holds a password: sign-ins are done by a
person at the keyboard, and the scripts print device codes for them.

## The shipped app registration

AnyFS's app sign-in, for SharePoint Online and Embedded, goes through the Microsoft identity platform as a *public
client*: no secret, the sign-in happens in the user's browser or by device code. The registration that
identifies it lives in an Entra tenant owned by the project, which must outlive any test tenant: an
app registration is tenant-bound, and the client id dies with its tenant.

| | |
|---|---|
| Owning tenant | `fiddyschmittgmail.onmicrosoft.com`, display name AnyFS, tenant id `5a454c8a-e1a4-47e9-ac62-7fe98d92f5a6` (a free Entra tenant, created 17-Oct-2026; it never expires) |
| Registration | AnyFS, client id `824dc5b9-f3c4-42b7-871d-6f0de8bc0a4a`, object id `148fc24e-4ae0-4a1e-801b-19083f9bf4ff` |
| Audience | work and school accounts in any organisation (`AzureADMultipleOrgs`) |
| Redirect URIs | `http://localhost` (MSAL's loopback) and `https://login.microsoftonline.com/common/oauth2/nativeclient` |
| Delegated Graph scopes | `User.Read`, `offline_access`, `Files.ReadWrite.All`, `Sites.ReadWrite.All`; the script now also asks for `FileStorageContainer.Selected` (SharePoint Embedded), not yet applied to the registration (it takes a rerun with the owning tenant's sign-in) |

`register-app.ps1 -TenantId <owning tenant>` creates or updates it (idempotent; rerun it after changing
the scopes). It talks to Graph directly, signing in with the device-code flow as the Microsoft Graph
Command Line Tools client; the first run asks the tenant administrator to consent to
`Application.ReadWrite.All`. Windows PowerShell 5.1 is enough. (The Microsoft.Graph PowerShell module
was tried first and failed on Windows PowerShell's device-code flow with "An error occurred when writing
to a listener".)

A second tenant named `anyfs.onmicrosoft.com` was not possible: Entra refuses additional tenants to an
account whose only Azure subscription is a benefit one ("Additional tenant creation is not allowed
without non-trial subscription"). A pay-as-you-go subscription would unlock it; moving the registration
costs nothing before the first release that ships the client id.

Publisher verification is not done (it needs a Partner Center account), so other organisations see
the app as unverified and an administrator there must consent once before their users can sign in.
A user whose organisation forbids third-party apps can paste their own registration's client id into
the source's settings instead, or, for SharePoint Online, sign in with the web session, which needs no
registration at all.

The registration exists only in Microsoft's global cloud: app registrations do not cross clouds. A
tenant in the US Government cloud (GCC High, sites under `.sharepoint.us`) registers its own for an app
sign-in, which refuses to start there without it; Automatic signs in to a SharePoint Online site there
with the web session until a client id is given. In that tenant's Entra admin
center: a new app registration; the Mobile and desktop applications platform with the redirect URI
`http://localhost`; Allow public client flows on (for the device code); the delegated Microsoft Graph
permissions `User.Read`, `Files.ReadWrite.All` and `Sites.ReadWrite.All` (`User.Read` and
`FileStorageContainer.Selected` for Embedded, with the app registered on the container type); admin
consent. Its Application (client) ID goes in the source's Client ID. `register-app.ps1` speaks to the
global cloud only.

## SharePoint Online and Embedded: the Microsoft 365 developer sandbox

| | |
|---|---|
| Tenant | `y0w7606wv.onmicrosoft.com` (an *instant* sandbox: Microsoft assigns the domain) |
| Administrator | `admin@y0w7606wv.onmicrosoft.com` |
| Licence | Microsoft 365 E5 developer, 9 user licences, 16 fictitious users with sample data |
| Data centre | Singapore (Australia is not offered for sandboxes) |
| Renewal | automatic while the linked Visual Studio Enterprise subscription is active |
| Dashboard | https://developer.microsoft.com/microsoft-365/profile |

Made 17-Oct-2026 through the Microsoft 365 Developer Program, which Visual Studio Professional and
Enterprise subscribers qualify for (`my.visualstudio.com`, Benefits, "Developer subscription (E5)").

The fixture tree the tests expect (`anyfs-fixture/` in a library or container) is made by the tests
themselves through the source. The web session's suite, the Server suite run over the session
(`ANYFS_TEST_SPS_SIGNIN=web`, DEVELOPING.md), makes its own `anyfs-fixture-rest/` beside it: the two
trees differ, and each suite knows its own by name.

### The SharePoint Embedded rig

SharePoint Embedded keeps files in *containers* of a *container type*, which is owned by an app
registration in the tenant that creates it (Microsoft's rule: the owning app must live in the owning
tenant, so AnyFS's shipped registration cannot own one). `embedded-rig.ps1 -TenantId <tenant> -Admin
<administrator>` builds the rig in the sandbox, idempotently, after one browser sign-in by the
administrator (the authorization-code flow with PKCE on a loopback port, as the Microsoft Graph Command
Line Tools client; the device-code flow is blocked by the sandbox's Conditional Access policy):

| | |
|---|---|
| Owning app | `AnyFS lab (Embedded owner)`, client id `3f847419-dd1e-46fc-bbf7-17a09f1d91d8`: single tenant, a public client plus a two-day secret the script makes on each run (only a confidential client may create containers, and only the script does) |
| Container type | `AnyFS lab`, id `eeed9b71-0a43-4258-a7be-21d3959d2c19`, trial: free, no Azure billing, 30 days (made 18-Oct-2026, expires 17-Nov-2026), five containers of 1 GB, this tenant only |
| Registration | the owning app has `full` delegated and app-only access to the containers; AnyFS's shipped app (`824dc5b9-…`) has `full` delegated access as a guest app, which is the arrangement AnyFS asks its users' administrators for |
| Containers | `anyfs-test` (the tests pin it) and `anyfs-other`, created app-only and activated, with the administrator and the test account as owners (Graph lists only containers the signed-in user is a direct member of) |
| Test account | `anyfs-tester@y0w7606wv.onmicrosoft.com`, a plain member the script makes and licenses (E5): Graph's container listing answers nothing for an account without a OneDrive, and the sandbox's administrator cannot have one, since a licence given to it is stripped again within minutes (seen three times on 18-Oct-2026). Its initial password is printed once by the script; the person sets a new one at the first sign-in (`tests/fixtures/SharePointSignIn --account anyfs-tester@…`). Nothing here holds it. |

When the trial expires, run the script again with `-Recreate` (a tenant has one trial container type at
a time, and the containers go with it). The tests take the ids from `ANYFS_TEST_SPE_*` (DEVELOPING.md).

## Azure

The Visual Studio Enterprise benefit's Azure subscription ("Visual Studio Enterprise Subscription",
`50f7178a-bd4f-4eae-997c-8075f60ccd41`, monthly credit) lives in the owning tenant. Activated
17-Oct-2026; nothing uses it yet.

## SharePoint Server: the on-premises lab

Two more VirtualBox VMs in the Vagrant lab (`../settings.yml`, `../Vagrantfile`), off by default:

| Machine | Address | What | Memory |
|---|---|---|---|
| `anyfs-dc-1` | 192.168.0.91 | Windows Server 2022 Core: the `anyfs.lab` forest (NetBIOS `ANYFS`), DNS, and the accounts SharePoint insists on | 2 GB |
| `anyfs-sp-1` | 192.168.0.92 | Windows Server 2022: SQL Server 2022 Developer and SharePoint Server Subscription Edition (180-day trial) as a one-server farm | 16 GB |
| `anyfs-sp-2` | 192.168.0.90 | Windows Server 2019: SQL Server 2022 Developer and SharePoint Server 2019 (180-day trial) as a one-server farm | 16 GB |

    ..\scripts\fetch-sharepoint.ps1              # once: the SharePoint and SQL Server images (2.6 GB) into tools\sharepoint
    vagrant up anyfs-dc-1 anyfs-sp-1             # in that order; well over an hour the first time
    vagrant up anyfs-sp-2                        # the 2019 farm, the same way; either farm runs without the other

The provisioners work in rounds. Each does what is still missing and, when the next step needs a restart
(becoming a domain controller; joining the domain; SQL Server; SharePoint's prerequisites, once or twice),
restarts the machine itself and leaves a marker; the Vagrantfile waits for the machine to come back,
provisions again and repeats until no restart is asked for, then runs the machine's smoke test. (The
machine restarts itself because a server that has just become a domain controller refuses WinRM logons
until it has restarted, so nothing Vagrant sent then would work.) `vagrant provision <name>` re-applies
either machine at any time.

What the farm looks like when it is up:

| | |
|---|---|
| Domain | `anyfs.lab`; the domain controller forwards other names to VirtualBox's resolver, so the VMs keep internet access |
| Accounts (`sharepoint.accounts` in `settings.yml`) | `ANYFS\sp_setup` installs and owns everything (local administrator of the SharePoint box, SQL sysadmin, site owner); `ANYFS\sp_farm` runs the farm and the web application pool; `ANYFS\sp_user` is what the tests sign in as: a member (contribute) of the test site and its secondary site collection administrator, which SharePoint requires for setting a file's times (`windows/sp-test-user.ps1`, run as the setup account after the farm is built) |
| SQL Server | 2022 Developer: the default instance, TCP on, MAXDOP 1, capped at 6 GB; installed from the ISO `scripts\fetch-sharepoint.ps1` fetches (the bootstrapper Microsoft's page offers refuses to run once it is a few months old, and in quiet mode just hangs) |
| SharePoint | installed and configured by [SharePointDsc](https://github.com/dsccommunity/SharePointDsc) 5.7.1 from `windows/sp-farm-configuration.ps1`: prerequisites (online), the binaries with the trial key, a `SingleServerFarm` with Central Administration on port 9999. WinRM's memory cap per shell is raised to 8 GB first: the box ships with 800 MB, and the child PowerShell that SharePointDsc creates a site collection in dies of it (`OutOfMemoryException`, exit code -532462766). The same scripts build the 2019 farm on `anyfs-sp-2`: the product, image, trial key, web URL and database names come from the machine's entry in `settings.yml` (`product`, `image`, `product_key`, `web_url`, `db_prefix`; `sql_server` would make a farm borrow another machine's SQL Server) |
| Web application | `http://anyfs-sp-1` on port 80, Windows authentication (NTLM); the Internet zone alternate URL `http://192.168.0.92` is how the other machines, which don't use the domain's DNS, reach it; the 2019 farm likewise at `http://anyfs-sp-2` and `http://192.168.0.90` |
| Sites | the root site collection and `/sites/anyfs` (team sites); the tests' fixture tool fills the libraries |
| Smoke test | `C:\anyfs-lab\smoke-test.ps1` on each machine (also run by `..\check.ps1`): on the SharePoint box it fetches `/sites/anyfs/_api/web/title` over NTLM as `sp_user`, by name and by address |

Why a domain controller: SharePoint Server refuses local accounts for the farm, and the test machines
that connect to it (Linux, macOS, Windows) are not domain members, so they authenticate with NTLM by
address, which is what the SharePoint source does with a Windows account too.

The `vagrant` account on the domain controller is gone after promotion (Windows deletes every local
account but Administrator), so that machine's `login_user` is `Administrator` with the lab's Windows
password; `vagrant ssh`, `check.ps1` and the tests use that.

SharePoint Server 2019, out of support since July 2026 but still widely run, is the second farm:
`anyfs-sp-2`, Windows Server 2019 with its own SQL Server 2022, built by the same scripts (first on
18-Oct-2026: SharePoint 2019 RTM, 16.0.10337). The Server live suite and the `sharepoint2019` E2E
collections point at `http://192.168.0.90/sites/anyfs` with the same accounts. Two things its first build
taught: the Server 2019 box ships Win32-OpenSSH under `Program Files\OpenSSH-Win64` rather than the Windows
capability, so the scripts take sshd's path from the service; and the DSC Local Configuration Manager must
not resume a configuration at boot on its own (`ActionAfterReboot = StopConfiguration`): the provisioner's
next round applies it again anyway, and its `Start-DscConfiguration -Force` aborted the resumed run while
the SharePoint setup that run had started kept going, so the round's own setup failed with "Only one
instance of setup may be run at a time". The provisioner now also waits for a busy manager or a running
setup before applying.
