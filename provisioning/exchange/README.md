# The Exchange Server lab machine

`anyfs-ex-1` (192.168.0.89) is one Exchange Server Subscription Edition Mailbox server in the lab's domain
(`anyfs.lab`, the SharePoint farms' domain controller `anyfs-dc-1`), on a VM of its own as Microsoft asks: never on a
domain controller, never beside SharePoint. It is what the Outlook source's on-premises store (EWS, Outlook on the
web) reads from; Outlook on `anyfs-win11-1` can point at it too, with the cached-mode slider short, to reproduce "there
are more items in this folder on the server".

## Building it

```powershell
# the Exchange SE image (6.4 GB; Microsoft's Download Center, id 108244, SHA-256 cd2b13f2…77bd1) goes in tools/exchange:
#   on this PC a junction to D:\VMs\media\exchange
vagrant up anyfs-dc-1 anyfs-ex-1
```

Two to three hours the first time (Exchange's own setup took two). The provisioner (`windows/ex-provision.ps1`) works in rounds with the restarts the steps
need: joining the domain; the prerequisites (Windows features, .NET Framework 4.8.1, the Visual C++ 2012 and 2013
runtimes, IIS URL Rewrite, the UCMA 4.0 runtime from the image); Exchange's own setup (`/Mode:Install /Roles:Mailbox
/OrganizationName:AnyFSLab /InstallWindowsComponents`, an hour or so), then the configuration
(`windows/ex-configure.ps1`) in the Exchange Management Shell. Setup and the configuration need a schema, enterprise
and domain administrator, so they run as the domain's `Administrator` (the dc's login) through a scheduled task; the
WinRM session Vagrant provisions in is the local account's. Exchange SE runs as the trial edition with no product key
for 180 days, which is fine for a lab. The smoke test (`windows/ex-smoke-test.ps1`) checks the services, EWS, Outlook
on the web and Autodiscover answering, and the configuration marker.

## What it holds (settings.yml, `exchange:`)

| | |
|---|---|
| Organisation | `AnyFSLab` |
| Mailboxes | `ex_user` (the tests' account: its own mailbox with an archive), `ex_other` (who the seeded mail is from); passwords in settings.yml |
| Shared mailbox | "Lab Shared", `ex_user` with full access |
| Address list | "Lab People": the `ex_*` mailboxes |
| Public folders | a public folder mailbox and the mail-enabled folder "Lab Notices" |
| EWS | `https://anyfs-ex-1.anyfs.lab/EWS/Exchange.asmx`, Windows and Basic authentication, the server's self-signed certificate (AnyFS: Trust any certificate) |
| Outlook on the web | `https://anyfs-ex-1.anyfs.lab/owa` |
| IMAP | on, plain-text logins (993 SSL, 143 TLS) |
| Seed | four messages sent from `ex_other` to `ex_user` (one with an attachment, one with text beyond ASCII). The PST fixtures (`tests/LibOutlook.Tests/fixtures`) are offered to a mailbox import request as "Imported ansi" and "Imported unicode" (dated mail with attachments, a contact, an appointment); the first build refused them with "Unable to open PST file … Access to the path is denied" although the share and the folder grant Exchange Trusted Subsystem modify rights, so the configuration says so and goes on (an open item) |

`vagrant provision anyfs-ex-1` re-applies the configuration; every step of it is idempotent. The accounts are
throwaway values for this lab only. The machine's own `vagrant` account is reached as the other Windows machines are:
`ssh -i ~/.ssh/anyfs_lab_ed25519 vagrant@192.168.0.89` (PowerShell is the shell).

## The Outlook source against it

From this PC the server is reached by its address, since only the lab's DNS knows `anyfs-ex-1.anyfs.lab`: an Outlook
source with `https://192.168.0.89` in the first field, Sign in with "Windows account", the user `ANYFS\ex_user` and its
password from settings.yml, and Trust any certificate on (the certificate is the server's own and names the host, not
the address). The live test `OutlookExchangeServerTests.TheLabExchangeServerIsServedOverSoap` does the same when these
are set, and is skipped otherwise:

```powershell
$env:ANYFS_TEST_EWS_URL = 'https://192.168.0.89'
$env:ANYFS_TEST_EWS_USER = 'ANYFS\ex_user'
$env:ANYFS_TEST_EWS_PASSWORD = '<the tester password in settings.yml>'
dotnet test tests/AnyFS.Sources.Tests -c Release --filter "Name=TheLabExchangeServerIsServedOverSoap"
```

## Running the VM alongside the others

On the 64 GB host the Exchange VM (12 GB) runs beside the domain controller (2 GB), the Windows host (6 GB) and one
SharePoint farm (16 GB); both SharePoint farms and Exchange at once is too much, so they rotate as the two farms do.
