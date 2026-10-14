#!/bin/bash
# AnyFS test lab: the one-time setup of the Mac as the macOS VM's
# host. `.\mac.ps1 setup` runs it on the Mac. Safe to repeat.
#
# Installs, with Homebrew:
#   tart          runs macOS VMs on Apple's Virtualization framework
#   Vagrant       and its tart provider, vagrant-tart, at the version given ($1)
set -euo pipefail
export PATH="/opt/homebrew/bin:/opt/homebrew/sbin:$PATH"
vagrant_tart_version=${1:?usage: setup-host.sh <vagrant-tart version>}

command -v brew >/dev/null || { echo "Homebrew is needed first: https://brew.sh" >&2; exit 1; }

if ! command -v tart >/dev/null; then
  # Homebrew only loads formulae from a third-party tap once it's trusted.
  brew tap cirruslabs/cli
  brew trust cirruslabs/cli
  brew install cirruslabs/cli/tart
fi

if ! command -v vagrant >/dev/null; then
  brew install --cask vagrant
fi

if ! vagrant plugin list | grep -qF "vagrant-tart ($vagrant_tart_version,"; then
  vagrant plugin install vagrant-tart --plugin-version "$vagrant_tart_version"
fi

echo "tart $(tart --version), $(vagrant --version), $(vagrant plugin list | grep vagrant-tart)"
