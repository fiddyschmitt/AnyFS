# The GitLab lab machine

`anyfs-gitlab-1` (192.168.0.85) is a GitLab server in the lab's domain (`anyfs.lab`, whose domain controller is
`anyfs-dc-1`). It runs GitLab Enterprise Edition and a GitLab Runner in Docker on Debian 13. The GitLab source (spec 7.8)
reads from it. GitLab needs no licence: without one, the Enterprise Edition runs as the Free tier. The Enterprise image is
used for its Kerberos sign-in, which the Community Edition lacks. It signs people in through the domain: LDAP, and
Kerberos with a keytab the domain controller makes.

## Building it

```powershell
# the domain controller first: it makes GitLab's service account, its Kerberos principal and keytab, and the DNS record
vagrant up anyfs-dc-1 anyfs-gitlab-1
```

Half an hour the first time. Debian's updates come first, then the GitLab and runner images (about 2 GB), GitLab's first
start (several minutes), then the seed. The playbook (`ansible/tasks/gitlab.yml`) does these steps:
- installs Docker, git with LFS and the Kerberos client tools;
- fetches the keytab from the domain controller's share `\\192.168.0.91\gitlab` as the service account itself, so no key
  leaves this PC for a VM;
- writes `gitlab.rb` (`templates/gitlab.rb.j2`) and the Compose file (`templates/gitlab-compose.yml.j2`);
- starts both containers and waits for GitLab;
- seeds it (`files/gitlab-seed.sh`).

The seed needs the PC's publish folder for the fixture generator: run `.\publish.ps1` first.

Each step looks before it makes, so `vagrant provision anyfs-gitlab-1` changes nothing that is already right. A changed
`gitlab.rb` or keytab is applied with `gitlab-ctl reconfigure`.

The smoke test (`/opt/anyfs/smoke-test.sh`) checks these:
- both containers are up;
- the keytab names `HTTP/anyfs-gitlab-1.anyfs.lab@ANYFS.LAB` and GitLab can read it;
- both tokens work;
- the runner is online;
- the seeded pipeline passed;
- a Kerberos ticket for the tester, from the domain controller, signs in to GitLab.

## What it holds (settings.yml, `gitlab:`)

| | |
|---|---|
| Address | `http://anyfs-gitlab-1.anyfs.lab` (plain HTTP: Kerberos needs no TLS and the lab has no CA), also at `http://192.168.0.85` |
| Image | `gitlab/gitlab-ee:19.5.1-ee.0` and `gitlab/gitlab-runner:v19.5.1`, pinned (GitLab releases monthly) |
| Accounts | `root` (its password and a token with `api` and `sudo` in settings.yml: the seed's); the tester `sp_user`, the domain's `ANYFS\sp_user`, who signs in through LDAP and Kerberos (its token, `read_api` and `read_repository`, in settings.yml) |
| Domain | LDAP sign-in against `anyfs-dc-1` as `svc_gitlab`; Kerberos for `HTTP/anyfs-gitlab-1.anyfs.lab`, the principal mapped to `svc_gitlab` (AES only), a Kerberos sign-in linked to the account's LDAP identity |
| Group `anyfs` | the tester a Developer; subgroup `anyfs/tools` |
| `anyfs/docs` | a wiki (nested pages, an AsciiDoc page, a page whose title is not ASCII, an attachment), four issues (attachments, comments, a milestone, a closed one, titles in other scripts and with every character Windows refuses), a merge request from `feature/docs` with a comment |
| `anyfs/tools/app` | the E2E fixture tree (`tests/fixtures/MakeTree`) under `e2e/`, `large.bin` in Git LFS; branches `main` and `feature/x`; tags `v1.0` and `v1.1`; a pipeline per ref (`build` writes `out/` as artifacts, `test` after it); release `v1.0` with an uploaded file and a link |
| Runner | `anyfs-lab`, an instance runner, shell executor, in its own container |

The values in settings.yml are throwaway, for this lab only. SSH in as the other Linux machines are reached:
`ssh -i ~/.ssh/anyfs_lab_ed25519 user@192.168.0.85`. GitLab's own tools are in its container:
`sudo docker exec -it gitlab gitlab-rails console`.

## The GitLab source against it

Only the lab's DNS knows `anyfs-gitlab-1.anyfs.lab`. This PC has one hosts line for it, `192.168.0.85
anyfs-gitlab-1.anyfs.lab`, so the web session and GitLab's own links work by name. `anyfs-win11-1` reaches the whole
domain through a name resolution policy rule for `.anyfs.lab` (`windows/provision.ps1`), which also finds the domain's
KDC, so Kerberos works there without joining the domain.

- **Access token:** the address `http://anyfs-gitlab-1.anyfs.lab/anyfs/tools/app` (or the whole server, or a group),
  and the tester's token.
- **Web session:** the same address; sign in on GitLab's page with the LDAP tab, as `sp_user`.
- **Windows account:** the name must be the server's (Kerberos asks for `HTTP/<host>`, never an address), and the
  computer must reach the domain's KDC: from `anyfs-win11-1`, as `sp_user@anyfs.lab` and its password. As yourself
  needs a domain sign-in.

The live tests run when these are set, and are skipped otherwise:

```powershell
$env:ANYFS_TEST_GITLAB_URL = 'http://anyfs-gitlab-1.anyfs.lab'
$env:ANYFS_TEST_GITLAB_TOKEN = '<gitlab.tester_token in settings.yml>'
dotnet test tests/AnyFS.Sources.Tests -c Release --filter "FullyQualifiedName~GitLabLive"
```

## Running the VM alongside the others

The GitLab VM takes 8 GB. With the domain controller (2 GB), the Windows host (6 GB) and the Linux machines (5 GB), the
lab runs on the 64 GB PC beside one SharePoint farm or the Exchange server. Halt it when the session's lab work is done.
