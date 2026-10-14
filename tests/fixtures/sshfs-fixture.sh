#!/usr/bin/env bash
# The tree the SSHFS tests expect on the SSH test server (tests/AnyFS.Sources.Tests/SshfsTests.cs), made in the
# home folder of the user the tests log in as: a readable file of known content, a folder, an empty folder, a
# writable scratch folder, a 50,000-byte file for ranged reads, a hidden file, a read-only file, a link to a
# folder and a dangling link. Run it as that user (the lab's provisioning does; or `vagrant ssh -c "bash -s" < this`).
set -eu
root=~/anyfs-fixture
rm -rf "$root"
mkdir -p "$root/docs" "$root/emptydir" "$root/scratch"
printf 'hello readme from sshd\n' > "$root/readme.txt"
printf 'aaa' > "$root/docs/a.txt"
head -c 50000 /dev/urandom > "$root/medium.bin"
printf 'hidden' > "$root/.hidden"
ln -s docs "$root/link-to-docs"
ln -s nowhere "$root/dangling"
chmod 444 "$root/docs/a.txt"
ls -la "$root"
