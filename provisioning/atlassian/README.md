# The Atlassian lab machine

`anyfs-atl-1` (192.168.0.78) runs Jira Software Data Center and Confluence Data Center in Docker on Debian 13, with one
PostgreSQL between them. The Jira and Confluence sources (spec 7.10) read from it. Each product signs people in with its
own user directory: there is no domain here, and the sign-ins the sources offer (a personal access token, a web session,
a user name and password) need none.

## The licences

Data Center products need a licence. This lab runs on Data Center **developer licences**: free with a Data Center
subscription, for non-production use. Each comes from one product's licence, so Jira Software and Confluence each need
their own:
- on my.atlassian.com, the licence's technical contact opens the product, then *View Developer License* under Actions;
- under Atlassian's newer billing, a billing admin opens admin.atlassian.com/billing, the licence, then *Copy developer
  license* in its ••• menu.

Save each key as a text file whose name ends `.lic` or `.txt` in `provisioning/atlassian/licences/` (`jira.lic` and
`confluence.lic`, say; the name does not matter: the setup offers each product every key and keeps the one it takes).
Copy and paste the key rather than retyping it: one wrong character and the products refuse it.
A licence key names the organisation that holds it, so the keys never go into this repository, `settings.yml` or a log:
git ignores that folder (`.gitignore`; `git check-ignore -v provisioning/atlassian/licences/jira.lic` shows the rule),
and the setup reads the keys through the shared folder (no copy is made in the VM) and never prints them. The servers
start without them: with no key the play stops before the setup, and a product that none of the keys fits waits at its
setup pages (the setup and the smoke test say so) while the other is set up and seeded.

## Building it

```powershell
vagrant up anyfs-atl-1
```

About twenty minutes the first time: Debian's updates, the three images (about 2 GB), the products' first starts, then
the setup and the seed. The playbook (`ansible/tasks/atlassian.yml`) does these steps:
- installs Docker, curl and jq;
- writes PostgreSQL's init script (a role and a database for each product), the Compose file
  (`templates/atlassian-compose.yml.j2`) and the values the scripts read (`/root/.atlassian-lab.env`);
- starts the containers and waits for both products to answer;
- sets both up with their licences (`files/atlassian-setup.sh`: the setup wizards, answered over HTTP with the fields
  the products' own setup pages have);
- seeds them (`files/atlassian-seed.sh`).

Each step looks before it makes, so `vagrant provision anyfs-atl-1` changes nothing that is already right.

## What it holds (settings.yml, `atlassian:`)

| | |
|---|---|
| Jira | `http://192.168.0.78:8080/jira`, `atlassian/jira-software:10.3.26` (the 10.3 long-term-support release), pinned |
| Confluence | `http://192.168.0.78:8090/confluence`, `atlassian/confluence:9.2.26` (the 9.2 long-term-support release), pinned |
| Database | `postgres:16.15` (the newest both releases support), databases `jiradb` and `confluencedb`, each its product's own role; not reachable from the LAN |
| Accounts | `atl_admin`, both products' administrator (the setup's and the seed's); the tester `atl_user`, an ordinary user of both. Their passwords are in settings.yml; the tester's personal access tokens, which the servers make, are written by the seed to `provisioning/atlassian/secrets/` (ignored by git) |

Both products sit at a context path on purpose: the sources find an installation below the host.

The values in settings.yml are throwaway, for this lab only. SSH in as the other Linux machines are reached:
`ssh -i ~/.ssh/anyfs_lab_ed25519 user@192.168.0.78`. The servers' logs: `sudo docker logs jira`,
`sudo docker logs confluence`.

The products' administration pages show the organisation the licences name: keep them out of screenshots, notes and
recorded test data.

Halt it when the session's lab work is done (`vagrant halt anyfs-atl-1`): it takes 12 GB.
