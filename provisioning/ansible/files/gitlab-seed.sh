#!/bin/bash
# Seeds the lab's GitLab with what the GitLab source's tests read (provisioning/gitlab/README.md): the tester's account
# (an LDAP sign-in makes it) and tokens, a group with a subgroup, a project with a wiki, issues and a merge request, a
# project with the E2E fixture tree (its large file in LFS), branches, tags, a pipeline with artifacts and a release, and
# the runner that runs the pipeline. Run by the playbook with the lab's env file (root's only). Each step looks before
# it makes, so a re-run changes nothing; it prints what it made or kept, never a secret.
set -euo pipefail

env_file=${1:?usage: gitlab-seed.sh /root/.gitlab-lab.env}
while IFS='=' read -r key value; do
    [[ -z "$key" || "$key" == \#* ]] && continue
    export "$key=$value"
done < "$env_file"

url="http://$GITLAB_HOST"
api="$url/api/v4"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# One API call as root; prints the body. An HTTP error says which call and what GitLab answered, and stops the seed.
call() {
    local method=$1 path=$2 status
    shift 2
    status=$(curl -sS -o "$work/body" -w '%{http_code}' -X "$method" -H "PRIVATE-TOKEN: $ROOT_TOKEN" "$api/$path" "$@")
    if [[ $status -ge 400 ]]; then
        echo "seed: $method $path answered $status: $(head -c 600 "$work/body")" >&2
        exit 1
    fi
    cat "$work/body"
}

# A GET as root where 404 is an answer: prints the body, or returns 1 when there is no such thing.
lookup() {
    local status
    status=$(curl -sS -o "$work/body" -w '%{http_code}' -H "PRIVATE-TOKEN: $ROOT_TOKEN" "$api/$1")
    [[ $status == 404 ]] && return 1
    if [[ $status -ge 400 ]]; then
        echo "seed: GET $1 answered $status: $(head -c 600 "$work/body")" >&2
        exit 1
    fi
    cat "$work/body"
}

enc() { jq -rn --arg s "$1" '$s|@uri'; }

# Whether a token opens the API now.
token_works() { [[ $(curl -s -o /dev/null -w '%{http_code}' -H "PRIVATE-TOKEN: $1" "$api/user") == 200 ]]; }

# The tokens settings.yml gives root and the tester, made inside GitLab (gitlab-rails runner: a minute or so).
make_tokens() {
    docker cp /opt/anyfs/gitlab-tokens.rb gitlab:/tmp/gitlab-tokens.rb
    docker exec --env-file "$env_file" gitlab gitlab-rails runner /tmp/gitlab-tokens.rb
}

# --- GitLab answers --------------------------------------------------------------------------------------------------
for _ in $(seq 1 60); do
    [[ $(curl -s -o /dev/null -w '%{http_code}' "$url/users/sign_in") == 200 ]] && break
    sleep 5
done

if ! token_works "$ROOT_TOKEN"; then
    make_tokens
fi

call PUT "application/settings?signup_enabled=false&auto_devops_enabled=false" >/dev/null

# --- The tester: an LDAP sign-in makes the account; then its token ---------------------------------------------------
if [[ $(lookup "users?username=$(enc "$TESTER_USER")" | jq length) == 0 ]]; then
    jar="$work/jar"
    page=$(curl -s -c "$jar" -b "$jar" "$url/users/sign_in")
    csrf=$(grep -o 'name="csrf-token" content="[^"]*"' <<<"$page" | head -n 1 | sed 's/.*content="//; s/"$//')
    csrf=$(python3 -c 'import html, sys; print(html.unescape(sys.argv[1]))' "$csrf")
    printf '%s' "$TESTER_PASSWORD" >"$work/password"
    answer=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -c "$jar" -b "$jar" \
        --data-urlencode "authenticity_token=$csrf" --data-urlencode "username=$TESTER_USER" \
        --data-urlencode "password@$work/password" "$url/users/auth/ldapmain/callback")
    rm -f "$work/password"
    # signed in: GitLab sends the browser on (to the dashboard); refused: back to the sign-in page
    if [[ $answer != 302* || $answer == *"/users/sign_in"* ]] || [[ $(lookup "users?username=$(enc "$TESTER_USER")" | jq length) == 0 ]]; then
        echo "seed: the LDAP sign-in as $TESTER_USER did not get through (answer: $answer); is anyfs-dc-1 up?" >&2
        exit 1
    fi
    echo "tester $TESTER_USER: made (an LDAP sign-in)"
fi
tester=$(lookup "users?username=$(enc "$TESTER_USER")" | jq -r '.[0].id')
if ! token_works "$TESTER_TOKEN"; then
    make_tokens
fi
token_works "$TESTER_TOKEN" || { echo "seed: the tester's token does not work after it was made" >&2; exit 1; }

# --- Groups and projects ----------------------------------------------------------------------------------------------
anyfs=$(lookup "groups/anyfs" | jq -r .id) ||
    { anyfs=$(call POST groups --data name=AnyFS --data path=anyfs --data visibility=private | jq -r .id); echo "group anyfs: made"; }
tools=$(lookup "groups/$(enc anyfs/tools)" | jq -r .id) ||
    { tools=$(call POST groups --data name=Tools --data path=tools --data "parent_id=$anyfs" --data visibility=private | jq -r .id); echo "group anyfs/tools: made"; }
lookup "groups/$anyfs/members/$tester" >/dev/null ||
    { call POST "groups/$anyfs/members" --data "user_id=$tester" --data access_level=30 >/dev/null; echo "$TESTER_USER a Developer of anyfs: made"; }

docs=$(lookup "projects/$(enc anyfs/docs)" | jq -r .id) ||
    { docs=$(call POST projects --data name=Docs --data path=docs --data "namespace_id=$anyfs" --data initialize_with_readme=true \
        --data visibility=private --data wiki_access_level=enabled | jq -r .id); echo "project anyfs/docs: made"; }
app=$(lookup "projects/$(enc anyfs/tools/app)" | jq -r .id) ||
    { app=$(call POST projects --data name=App --data path=app --data "namespace_id=$tools" --data visibility=private | jq -r .id); echo "project anyfs/tools/app: made"; }

# --- anyfs/docs: the wiki (nested pages, another markup, an attachment) -----------------------------------------------
# a 16x16 PNG for the attachments
python3 - "$work/diagram.png" <<'EOF'
import struct, sys, zlib
w = h = 16
rows = b''.join(b'\x00' + b''.join(bytes((x * 16, y * 16, 160)) for x in range(w)) for y in range(h))
def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
EOF
page() {   # slug, title, format, content
    if ! lookup "projects/$docs/wikis/$(enc "$1")" >/dev/null; then
        call POST "projects/$docs/wikis" --data-urlencode "title=$2" --data "format=$3" --data-urlencode "content=$4" >/dev/null
        echo "wiki page $1: made"
    fi
}
if ! lookup "projects/$docs/wikis/home" >/dev/null; then
    picture=$(call POST "projects/$docs/wikis/attachments" -F "file=@$work/diagram.png" | jq -r .link.markdown)
    page home home markdown "# Welcome

The AnyFS lab's wiki. Start with [installing](guides/install).

$picture
"
fi
page guides/install guides/install markdown "# Installing

Download the build for your system, then see [Linux](guides/install/linux).
"
page guides/install/linux guides/install/linux markdown "# On Linux

\`chmod +x anyfs\`, then \`./anyfs serve\`.
"
page notes notes asciidoc "= Notes

Some *bold* text, in AsciiDoc.
"
page "Ünïcödé-résumé" "Ünïcödé résumé" markdown "# Ünïcödé résumé

A page whose title is not ASCII: 日本語.
"

# --- anyfs/docs: issues (an attachment, comments, a milestone; closed; names Windows refuses) -------------------------
milestone=$(lookup "projects/$docs/milestones?title=v1" | jq -r '.[0].id // empty')
[[ -n $milestone ]] || { milestone=$(call POST "projects/$docs/milestones" --data title=v1 | jq -r .id); echo "milestone v1: made"; }
issue() {   # iid, title, description, extra curl arguments...
    local iid=$1 title=$2 description=$3
    shift 3
    if ! lookup "projects/$docs/issues/$iid" >/dev/null; then
        made=$(call POST "projects/$docs/issues" --data-urlencode "title=$title" --data-urlencode "description=$description" "$@" | jq -r .iid)
        [[ $made == "$iid" ]] || { echo "seed: issue '$title' became #$made, not #$iid" >&2; exit 1; }
        echo "issue #$iid: made"
        return 0
    fi
    return 1
}
printf 'Steps to reproduce, as text.\n' >"$work/steps.txt"
head -c 102400 /dev/urandom >"$work/data.bin"
if ! lookup "projects/$docs/issues/1" >/dev/null; then
    steps=$(call POST "projects/$docs/uploads" -F "file=@$work/steps.txt" | jq -r .markdown)
    shot=$(call POST "projects/$docs/uploads" -F "file=@$work/diagram.png" | jq -r .markdown)
    data=$(call POST "projects/$docs/uploads" -F "file=@$work/data.bin" | jq -r .markdown)
    issue 1 "Install fails on Debian" "The installer stops half way.

$steps

$shot

The log: $data" --data labels=bug,install --data "milestone_id=$milestone" --data "assignee_ids[]=$tester"
    call POST "projects/$docs/issues/1/notes" --data-urlencode "body=It works on Debian 12." >/dev/null
    call POST "projects/$docs/issues/1/notes" --data-urlencode "body=Fixed in the next build." >/dev/null
fi
if issue 2 "An old question" "Answered long ago."; then
    call PUT "projects/$docs/issues/2?state_event=close" >/dev/null
fi
issue 3 "Ünïcödé — résumé ✓ 日本語" "A title that is not ASCII." || true
issue 4 'Paths: a/b \ c * ? "q" <x> |p|' "A title with every character Windows refuses in a name." || true

# --- anyfs/docs: a merge request from a branch -------------------------------------------------------------------------
lookup "projects/$docs/repository/branches/$(enc feature/docs)" >/dev/null ||
    { call POST "projects/$docs/repository/branches" --data branch=feature/docs --data ref=main >/dev/null; echo "branch feature/docs: made"; }
if ! lookup "projects/$docs/merge_requests/1" >/dev/null; then
    jq -n '{branch: "feature/docs", commit_message: "Docs: say more", actions: [
        {action: "create", file_path: "docs/more.md", content: "# More\n\nA page the merge request adds.\n"},
        {action: "update", file_path: "README.md", content: "# Docs\n\nThe AnyFS lab'"'"'s documentation project, changed on a branch.\n"}]}' >"$work/commit.json"
    call POST "projects/$docs/repository/commits" -H 'Content-Type: application/json' --data "@$work/commit.json" >/dev/null
    call POST "projects/$docs/merge_requests" --data source_branch=feature/docs --data target_branch=main --data-urlencode "title=Docs: say more" \
        --data-urlencode "description=Adds a page and changes the README." >/dev/null
    call POST "projects/$docs/merge_requests/1/notes" --data-urlencode "body=Looks good to me." >/dev/null
    echo "merge request !1: made"
fi

# --- The runner (an instance runner, shell executor, in its own container) ---------------------------------------------
if ! docker exec gitlab-runner grep -q '^\[\[runners\]\]' /etc/gitlab-runner/config.toml 2>/dev/null; then
    runner=$(call POST user/runners --data runner_type=instance_type --data description=anyfs-lab --data run_untagged=true | jq -r .token)
    docker exec gitlab-runner gitlab-runner register --non-interactive --url "$url" --token "$runner" --executor shell \
        --description anyfs-lab >/dev/null 2>&1
    echo "runner anyfs-lab: made"
fi

# --- anyfs/tools/app: the E2E fixture tree (large.bin in LFS), branches, tags, a pipeline ------------------------------
if ! lookup "projects/$app/repository/branches/main" >/dev/null; then
    [[ -x $MAKETREE || -f $MAKETREE ]] || { echo "seed: $MAKETREE is missing: run provisioning\\publish.ps1 on the PC first" >&2; exit 1; }
    install -m 0755 "$MAKETREE" "$work/MakeTree"
    repo="$work/app"
    mkdir -p "$repo"
    cd "$repo"
    git init -q -b main
    git lfs install --local >/dev/null
    git config user.name "AnyFS Lab"
    git config user.email "lab@anyfs.lab"
    "$work/MakeTree" e2e >/dev/null
    git lfs track "e2e/large.bin" >/dev/null
    printf '# App\n\nThe E2E fixture tree (tests/fixtures/MakeTree) under e2e/, its large file in Git LFS.\n' >README.md
    cat >.gitlab-ci.yml <<'EOF'
variables:
  GIT_DEPTH: "1"
  GIT_LFS_SKIP_SMUDGE: "1"

build:
  script:
    - mkdir -p out/sub
    - echo "built from $CI_COMMIT_SHORT_SHA" > out/hello.txt
    - seq 1 400000 > out/sub/numbers.txt
  artifacts:
    paths:
      - out/

test:
  needs: [build]
  script:
    - test -s out/hello.txt
    - echo "the tests passed"
EOF
    git add -A
    git commit -q -m "The E2E fixture tree"
    git tag v1.0
    git checkout -q -b feature/x
    printf 'A change on a branch.\n' >feature.txt
    git add feature.txt
    git commit -q -m "A change on feature/x"
    git checkout -q main
    printf '1.1\n' >VERSION
    git add VERSION
    git commit -q -m "Version 1.1"
    git tag v1.1
    printf 'http://oauth2:%s@%s\n' "$ROOT_TOKEN" "$GITLAB_HOST" >"$work/credentials"
    git remote add origin "$url/anyfs/tools/app.git"
    git -c credential.helper="store --file=$work/credentials" push -q origin main feature/x v1.0 v1.1
    cd /
    echo "anyfs/tools/app: the fixture tree, feature/x, v1.0 and v1.1 pushed"
fi

if ! lookup "projects/$app/releases/v1.0" >/dev/null; then
    printf 'What is new in v1.0.\n' >"$work/release-notes.txt"
    notes=$(call POST "projects/$app/uploads" -F "file=@$work/release-notes.txt" | jq -r .full_path)
    jq -n --arg notes "$url$notes" '{tag_name: "v1.0", name: "Version 1.0", description: "The first release: the fixture tree.",
        assets: {links: [{name: "release-notes.txt", url: $notes, link_type: "other"},
                         {name: "AnyFS on GitHub", url: "https://github.com/fiddyschmitt/AnyFS", link_type: "other"}]}}' >"$work/release.json"
    call POST "projects/$app/releases" -H 'Content-Type: application/json' --data "@$work/release.json" >/dev/null
    echo "release v1.0: made"
fi

# --- The pipeline on main finished (the tests read its job's log and artifacts) ----------------------------------------
for _ in $(seq 1 60); do
    status=$(lookup "projects/$app/pipelines?ref=main&per_page=1" | jq -r '.[0].status // "none"')
    [[ $status == success || $status == failed || $status == canceled ]] && break
    sleep 10
done
echo "pipeline on main: $status"
[[ $status == success ]] || { echo "seed: the pipeline on main is $status" >&2; exit 1; }
