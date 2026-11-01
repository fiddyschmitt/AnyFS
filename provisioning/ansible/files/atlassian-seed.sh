#!/bin/bash
# The AnyFS lab's Atlassian seed (provisioning/atlassian/README.md): the accounts, the tester's personal access tokens,
# and what the Jira and Confluence sources' tests read. Each step looks before it makes, so a re-run changes nothing; it
# says "made" for what it made. Runs as root in the VM after atlassian-setup.sh.
#
#   atlassian-seed.sh /root/.atlassian-lab.env
set -euo pipefail

while IFS='=' read -r key value; do
    [[ -z "$key" || "$key" == \#* ]] && continue
    export "$key=$value"
done < "$1"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
state=/root/.atlassian-seed
mkdir -p "$state" "$SECRETS"
chmod 700 "$state"

say() { echo "$*"; }

# --- signing in -------------------------------------------------------------------------------------------------------
# A session for an account: the two-step verification login of Jira 10.2+ / Confluence 9.1+, else Seraph's form.
# $1 product URL, $2 jar name, $3 user, $4 password, $5 Seraph's form path (login.jsp, dologin.action)
login() {
    local url=$1 jar="$work/$2.jar" user=$3 password=$4 form=$5 code
    rm -f "$jar"
    code=$(jq -n --arg u "$user" --arg p "$password" '{username: $u, password: $p}' |
        curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -H 'Content-Type: application/json' -H 'X-Atlassian-Token: no-check' \
            --data-binary @- "$url/rest/tsv/latest/authenticate")
    if [[ $code != 200 ]]; then
        curl -s -o /dev/null -c "$jar" -b "$jar" -H 'X-Atlassian-Token: no-check' --data-urlencode "os_username=$user" \
            --data-urlencode "os_password=$password" --data-urlencode "os_cookie=true" --data-urlencode "login=Log in" "$url/$form"
    fi
}

# A personal access token for an account, made once and kept ($state for the administrator's, $SECRETS for the tester's):
# $1 product URL, $2 jar, $3 file it is kept in
token_for() {
    local url=$1 jar="$work/$2.jar" file=$3 raw
    if [[ -s "$file" ]] && [[ $(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $(cat "$file")" "$url/rest/api/2/myself" 2>/dev/null) == 200 ||
        $(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $(cat "$file")" "$url/rest/api/user/current" 2>/dev/null) == 200 ]]; then
        return 0
    fi
    raw=$(curl -s -c "$jar" -b "$jar" -H 'Content-Type: application/json' -H 'X-Atlassian-Token: no-check' \
        --data '{"name":"anyfs-lab"}' "$url/rest/pat/latest/tokens" | jq -r '.rawToken // empty')
    [[ -n "$raw" ]] || { say "no personal access token from $url"; return 1; }
    umask 077
    printf '%s' "$raw" > "$file"
    say "made a personal access token ($(basename "$file"))"
}

# REST calls with a bearer token: $1 token file, then curl's arguments; JSON in and out
rest() { local t; t=$(cat "$1"); shift; curl -s -H "Authorization: Bearer $t" -H 'Content-Type: application/json' -H 'X-Atlassian-Token: no-check' "$@"; }
status_of() { local t; t=$(cat "$1"); shift; curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $t" -H 'X-Atlassian-Token: no-check' "$@"; }

# --- files to attach --------------------------------------------------------------------------------------------------
files() {
    local f="$work/files"
    mkdir -p "$f"
    # a small PNG (a 2x2 image), a PDF, a text file, the same name twice, and 50 MiB of a repeating pattern for Range reads
    base64 -d > "$f/diagram.png" <<'PNG'
iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==
PNG
    printf '%%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj 3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%%%EOF\n' > "$f/spec.pdf"
    printf 'Notes kept beside the issue.\nSecond line.\n' > "$f/notes.txt"
    mkdir -p "$f/second"
    printf 'Another file with the same name.\n' > "$f/second/notes.txt"
    if [[ ! -s "$f/large.bin" ]]; then
        # deterministic: the tests compare a hash
        yes 'AnyFS Atlassian lab large attachment 0123456789abcdef' | head -c 52428800 > "$f/large.bin"
    fi
    printf '# Not the page\n\nAn attachment whose name is the page'"'"'s own Markdown file'"'"'s.\n' > "$f/Architecture.md"
}

# =====================================================================================================================
# Jira
# =====================================================================================================================
J=$JIRA_URL
jira_admin_token="$state/jira-admin.token"
jira_tester_token="$SECRETS/jira-tester.token"

jira_issue_field() { rest "$jira_admin_token" "$J/rest/api/2/field" | jq -r --arg n "$1" '.[] | select(.name == $n) | .id' | head -1; }

# an issue by its summary in a project, made where missing: $1 project, $2 summary, $3 the rest of its fields (JSON);
# prints its key
jira_issue() {
    local project=$1 summary=$2 fields=$3 key
    key=$(rest "$jira_admin_token" -G "$J/rest/api/2/search" --data-urlencode "jql=project = $project AND summary ~ \"\\\"$(printf '%s' "$summary" | sed 's/[\\"]/\\&/g; s/[][?*~+^!(){}:|&-]/ /g')\\\"\"" \
        --data-urlencode "fields=summary" --data-urlencode "maxResults=50" | jq -r --arg s "$summary" '.issues[]? | select(.fields.summary == $s) | .key' | head -1)
    if [[ -z "$key" ]]; then
        key=$(jq -n --arg p "$project" --arg s "$summary" --argjson f "$fields" '{fields: ({project: {key: $p}, summary: $s} + $f)}' |
            rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/issue" | jq -r '.key // empty')
        [[ -n "$key" ]] || { say "jira: could not make the issue '$summary'" >&2; return 1; }
        say "jira: made $key" >&2
    fi
    printf '%s' "$key"
}

jira_attach() {
    local key=$1 file=$2 name
    name=$(basename "$file")
    if ! rest "$jira_admin_token" "$J/rest/api/2/issue/$key?fields=attachment" | jq -e --arg n "$name" --argjson s "$(stat -c %s "$file")" \
        '[.fields.attachment[] | select(.filename == $n and .size == $s)] | length > 0' > /dev/null; then
        curl -s -o /dev/null -H "Authorization: Bearer $(cat "$jira_admin_token")" -H 'X-Atlassian-Token: no-check' -F "file=@$file" "$J/rest/api/2/issue/$key/attachments"
        say "jira: made $key's attachment $name"
    fi
}

jira_comment() {
    local key=$1 body=$2 extra=${3:-'{}'}
    if ! rest "$jira_admin_token" "$J/rest/api/2/issue/$key/comment?maxResults=1000" | jq -e --arg b "$body" '[.comments[] | select(.body == $b)] | length > 0' > /dev/null; then
        jq -n --arg b "$body" --argjson x "$extra" '{body: $b} + $x' | rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/issue/$key/comment" > /dev/null
        say "jira: made a comment on $key"
    fi
}

jira_done() {
    local key=$1 status transition
    status=$(rest "$jira_admin_token" "$J/rest/api/2/issue/$key?fields=status" | jq -r '.fields.status.statusCategory.key')
    [[ $status == done ]] && return 0
    transition=$(rest "$jira_admin_token" "$J/rest/api/2/issue/$key/transitions" | jq -r '.transitions[] | select(.to.statusCategory.key == "done") | .id' | head -1)
    [[ -n "$transition" ]] || return 0
    jq -n --arg t "$transition" '{transition: {id: $t}}' | rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/issue/$key/transitions" > /dev/null
    say "jira: resolved $key"
}

seed_jira() {
    login "$J" jira-admin "$ADMIN_USER" "$ADMIN_PASSWORD" login.jsp
    token_for "$J" jira-admin "$jira_admin_token"

    # the tester, an ordinary user of Jira Software
    if [[ $(status_of "$jira_admin_token" "$J/rest/api/2/user?username=$TESTER_USER") != 200 ]]; then
        jq -n --arg u "$TESTER_USER" --arg p "$TESTER_PASSWORD" '{name: $u, password: $p, emailAddress: ($u + "@anyfs.lab"), displayName: "AnyFS Tester", applicationKeys: ["jira-software"]}' |
            rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/user" > /dev/null
        say "jira: made the tester $TESTER_USER"
    fi
    login "$J" jira-tester "$TESTER_USER" "$TESTER_PASSWORD" login.jsp
    token_for "$J" jira-tester "$jira_tester_token"

    # the projects: ANYFS (software, scrum), OPS (business), BULK (many issues, for paging), HIDDEN (not the tester's)
    for spec in "ANYFS|AnyFS Lab|software|com.pyxis.greenhopper.jira:gh-simplified-scrum-classic" \
                "OPS|Operations|business|com.atlassian.jira-core-project-templates:jira-core-simplified-task-tracking" \
                "BULK|Bulk issues|business|com.atlassian.jira-core-project-templates:jira-core-simplified-task-tracking" \
                "HIDDEN|Hidden from the tester|business|com.atlassian.jira-core-project-templates:jira-core-simplified-task-tracking"; do
        IFS='|' read -r key name type template <<< "$spec"
        if [[ $(status_of "$jira_admin_token" "$J/rest/api/2/project/$key") != 200 ]]; then
            jq -n --arg k "$key" --arg n "$name" --arg t "$type" --arg tp "$template" --arg l "$ADMIN_USER" \
                '{key: $k, name: $n, projectTypeKey: $t, projectTemplateKey: $tp, lead: $l, description: ("The AnyFS lab'"'"'s " + $n + " project.")}' |
                rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/project" > /dev/null
            say "jira: made the project $key"
        fi
    done

    # HIDDEN: browsed by administrators only
    if ! rest "$jira_admin_token" "$J/rest/api/2/project/HIDDEN/permissionscheme" | jq -e '.name == "AnyFS administrators only"' > /dev/null; then
        scheme=$(rest "$jira_admin_token" "$J/rest/api/2/permissionscheme" | jq -r '.permissionSchemes[] | select(.name == "AnyFS administrators only") | .id' | head -1)
        if [[ -z "$scheme" ]]; then
            scheme=$(jq -n '{name: "AnyFS administrators only", permissions: [{holder: {type: "group", parameter: "jira-administrators"}, permission: "BROWSE_PROJECTS"}, {holder: {type: "group", parameter: "jira-administrators"}, permission: "ADMINISTER_PROJECTS"}]}' |
                rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/permissionscheme" | jq -r '.id')
        fi
        jq -n --argjson id "$scheme" '{id: $id}' | rest "$jira_admin_token" -X PUT --data-binary @- "$J/rest/api/2/project/HIDDEN/permissionscheme" > /dev/null
        say "jira: HIDDEN is for administrators only"
    fi

    # ANYFS's components and versions
    for c in Core Docs; do
        if ! rest "$jira_admin_token" "$J/rest/api/2/project/ANYFS/components" | jq -e --arg c "$c" '[.[] | select(.name == $c)] | length > 0' > /dev/null; then
            jq -n --arg c "$c" --arg l "$ADMIN_USER" '{project: "ANYFS", name: $c, leadUserName: $l, description: ("The " + $c + " component.")}' |
                rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/component" > /dev/null
            say "jira: made the component $c"
        fi
    done
    for v in "1.0|true|2026-09-01" "1.1|false|"; do
        IFS='|' read -r name released date <<< "$v"
        if ! rest "$jira_admin_token" "$J/rest/api/2/project/ANYFS/versions" | jq -e --arg v "$name" '[.[] | select(.name == $v)] | length > 0' > /dev/null; then
            jq -n --arg v "$name" --argjson r "$released" --arg d "$date" '{project: "ANYFS", name: $v, released: $r} + (if $d == "" then {} else {releaseDate: $d} end)' |
                rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/version" > /dev/null
            say "jira: made the version $name"
        fi
    done

    files
    local f="$work/files"
    local described
    described=$(cat <<'WIKI'
h2. What happens

The mount *stops* answering after a _long_ listing. Steps:
# Mount the source
# Open a folder of 10,000 files
# Wait

||Setting||Value||
|Requests at a time|8|
|Keep folder listings|on|

{code:java}
var source = new JiraSource(settings);
await source.InitialiseAsync(ct);
{code}

{panel:title=Note}
A panel's text.
{panel}

{quote}Quoted from the log.{quote}

See [the docs|https://example.com/docs] and the diagram: !diagram.png!
Reported for [~TESTER].
WIKI
)
    described=${described//TESTER/$TESTER_USER}

    local epic_name_field
    epic_name_field=$(jira_issue_field "Epic Name")
    local epic story bug task sub resolved
    epic=$(jira_issue ANYFS "Read-only sources" "$(jq -n --arg f "$epic_name_field" '{issuetype: {name: "Epic"}} + (if $f == "" then {} else {($f): "Read-only sources"} end)')")
    story=$(jira_issue ANYFS "List a project's issues" "$(jq -n --arg d "$described" '{issuetype: {name: "Story"}, description: $d, labels: ["listing", "jira"], components: [{name: "Core"}], fixVersions: [{name: "1.1"}], priority: {name: "High"}}')")
    bug=$(jira_issue ANYFS 'Fix: path "C:\temp\a*b?" <fails> | sometimes' '{"issuetype": {"name": "Bug"}, "description": "A summary with every character Windows refuses in a name.", "versions": [{"name": "1.0"}]}')
    task=$(jira_issue ANYFS "Attachments with the same name" '{"issuetype": {"name": "Task"}, "description": "Two files called notes.txt, a PDF, an image and a large file."}')
    sub=$(jira_issue ANYFS "A sub-task of the story" "$(jq -n --arg p "$story" '{issuetype: {name: "Sub-task"}, parent: {key: $p}, description: "Done in an afternoon."}')")
    resolved=$(jira_issue ANYFS "An issue already done" '{"issuetype": {"name": "Task"}, "description": "Resolved, so Open issues only leaves it out."}')
    jira_issue ANYFS "Ünïcödé summary — 日本語 — emoji 🚀" '{"issuetype": {"name": "Task"}, "description": "A summary in other scripts."}' > /dev/null

    # the story under the epic (Jira Software's Epic Link), and a link between the bug and the story
    local epic_link
    epic_link=$(jira_issue_field "Epic Link")
    if [[ -n "$epic_link" ]]; then
        jq -n --arg f "$epic_link" --arg e "$epic" '{fields: {($f): $e}}' | rest "$jira_admin_token" -X PUT --data-binary @- "$J/rest/api/2/issue/$story" > /dev/null || true
    fi
    if ! rest "$jira_admin_token" "$J/rest/api/2/issue/$bug?fields=issuelinks" | jq -e '.fields.issuelinks | length > 0' > /dev/null; then
        jq -n --arg a "$bug" --arg b "$story" '{type: {name: "Blocks"}, inwardIssue: {key: $a}, outwardIssue: {key: $b}}' |
            rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/issueLink" > /dev/null
        say "jira: made a link between $bug and $story"
    fi

    jira_attach "$story" "$f/diagram.png"
    jira_attach "$task" "$f/notes.txt"
    jira_attach "$task" "$f/second/notes.txt"
    jira_attach "$task" "$f/spec.pdf"
    jira_attach "$task" "$f/large.bin"
    jira_comment "$story" "The first comment, with *bold* and a list:
* one
* two"
    jira_comment "$story" "A second comment, by the administrator again."
    jira_comment "$bug" "Seen on Windows 11 too."
    jira_done "$resolved"

    # OPS: plain tasks, one with a hundred comments
    local many
    many=$(jira_issue OPS "An issue with a hundred comments" '{"issuetype": {"name": "Task"}, "description": "Comments enough to page."}')
    if [[ $(rest "$jira_admin_token" "$J/rest/api/2/issue/$many/comment?maxResults=1" | jq -r '.total') -lt 100 ]]; then
        for n in $(seq 1 100); do
            jq -n --arg b "Comment number $n." '{body: $b}' | rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/issue/$many/comment" > /dev/null
        done
        say "jira: made a hundred comments on $many"
    fi
    for n in 1 2 3; do
        jira_issue OPS "Operations task $n" '{"issuetype": {"name": "Task"}, "description": "Routine."}' > /dev/null
    done
    jira_issue HIDDEN "A secret issue" '{"issuetype": {"name": "Task"}, "description": "The tester never sees this."}' > /dev/null

    # BULK: 250 issues (paging a hundred a request), made fifty a request
    local have
    have=$(rest "$jira_admin_token" -G "$J/rest/api/2/search" --data-urlencode "jql=project = BULK" --data-urlencode "maxResults=0" | jq -r '.total')
    if [[ "$have" -lt 250 ]]; then
        for batch in $(seq $((have / 50)) 4); do
            jq -n --argjson b "$batch" '{issueUpdates: [range(0; 50) | {fields: {project: {key: "BULK"}, issuetype: {name: "Task"}, summary: ("Bulk issue " + (($b * 50 + . + 1) | tostring)), description: "One of many."}}]}' |
                rest "$jira_admin_token" -X POST --data-binary @- "$J/rest/api/2/issue/bulk" > /dev/null
        done
        say "jira: made BULK's issues"
    fi
}

# =====================================================================================================================
# Confluence
# =====================================================================================================================
C=$CONFLUENCE_URL
conf_admin_token="$state/confluence-admin.token"
conf_tester_token="$SECRETS/confluence-tester.token"

# a page by its title in a space, made where missing: $1 space, $2 title, $3 parent id ("" for the space's top), $4 storage
# body, $5 type (page, blogpost); prints its id
conf_page() {
    local space=$1 title=$2 parent=$3 body=$4 type=${5:-page} id
    id=$(rest "$conf_admin_token" -G "$C/rest/api/content" --data-urlencode "spaceKey=$space" --data-urlencode "title=$title" --data-urlencode "type=$type" | jq -r '.results[0].id // empty')
    if [[ -z "$id" ]]; then
        id=$(jq -n --arg s "$space" --arg t "$title" --arg p "$parent" --arg b "$body" --arg ty "$type" \
            '{type: $ty, title: $t, space: {key: $s}, body: {storage: {value: $b, representation: "storage"}}} + (if $p == "" then {} else {ancestors: [{id: $p}]} end)' |
            rest "$conf_admin_token" -X POST --data-binary @- "$C/rest/api/content" | jq -r '.id // empty')
        [[ -n "$id" ]] || { say "confluence: could not make the $type '$title'" >&2; return 1; }
        say "confluence: made the $type '$title'" >&2
    fi
    printf '%s' "$id"
}

conf_attach() {
    local id=$1 file=$2 name
    name=$(basename "$file")
    if ! rest "$conf_admin_token" -G "$C/rest/api/content/$id/child/attachment" --data-urlencode "filename=$name" | jq -e '.results | length > 0' > /dev/null; then
        curl -s -o /dev/null -H "Authorization: Bearer $(cat "$conf_admin_token")" -H 'X-Atlassian-Token: no-check' -F "file=@$file" "$C/rest/api/content/$id/child/attachment"
        say "confluence: made page $id's attachment $name"
    fi
}

conf_comment() {
    local id=$1 body=$2 parent=${3:-}
    if ! rest "$conf_admin_token" "$C/rest/api/content/$id/child/comment?depth=all&expand=body.storage&limit=100" | jq -e --arg b "$body" '[.results[] | select(.body.storage.value == $b)] | length > 0' > /dev/null; then
        jq -n --arg id "$id" --arg b "$body" --arg p "$parent" '{type: "comment", container: {id: $id, type: "page"}, body: {storage: {value: $b, representation: "storage"}}} + (if $p == "" then {} else {ancestors: [{id: $p}]} end)' |
            rest "$conf_admin_token" -X POST --data-binary @- "$C/rest/api/content" > /dev/null
        say "confluence: made a comment on page $id"
    fi
}

seed_confluence() {
    login "$C" conf-admin "$ADMIN_USER" "$ADMIN_PASSWORD" dologin.action
    token_for "$C" conf-admin "$conf_admin_token"

    if [[ $(status_of "$conf_admin_token" "$C/rest/api/user?username=$TESTER_USER") != 200 ]]; then
        jq -n --arg u "$TESTER_USER" --arg p "$TESTER_PASSWORD" '{userName: $u, fullName: "AnyFS Tester", email: ($u + "@anyfs.lab"), password: $p, notifyViaEmail: false}' |
            rest "$conf_admin_token" -X POST --data-binary @- "$C/rest/api/admin/user" > /dev/null
        say "confluence: made the tester $TESTER_USER"
    fi
    login "$C" conf-tester "$TESTER_USER" "$TESTER_PASSWORD" dologin.action
    token_for "$C" conf-tester "$conf_tester_token"
    local tester_key
    tester_key=$(rest "$conf_admin_token" "$C/rest/api/user?username=$TESTER_USER" | jq -r '.userKey')

    for spec in "ANYFS|AnyFS Lab" "BULK|Bulk pages" "OLD|An archived space"; do
        IFS='|' read -r key name <<< "$spec"
        if [[ $(status_of "$conf_admin_token" "$C/rest/api/space/$key") != 200 ]]; then
            jq -n --arg k "$key" --arg n "$name" '{key: $k, name: $n, description: {plain: {value: ("The AnyFS lab'"'"'s " + $n + " space."), representation: "plain"}}}' |
                rest "$conf_admin_token" -X POST --data-binary @- "$C/rest/api/space" > /dev/null
            say "confluence: made the space $key"
        fi
    done

    files
    local f="$work/files" home arch services storage onboarding notes secret
    home=$(rest "$conf_admin_token" "$C/rest/api/space/ANYFS?expand=homepage" | jq -r '.homepage.id')
    arch=$(conf_page ANYFS "Architecture" "$home" "$(cat <<STORAGE
<h1>Architecture</h1>
<p>How AnyFS is put together. See <ac:link><ri:page ri:content-title="Onboarding" /></ac:link> first, and ask <ac:link><ri:user ri:userkey="$tester_key" /></ac:link>.</p>
<ac:structured-macro ac:name="toc" ac:schema-version="1" />
<h2>Layers</h2>
<ul><li>Sources</li><li>Mounts<ul><li>WinFsp</li><li>SMB</li></ul></li></ul>
<ac:structured-macro ac:name="info" ac:schema-version="1"><ac:parameter ac:name="title">Read-only</ac:parameter><ac:rich-text-body><p>The Atlassian sources only read.</p></ac:rich-text-body></ac:structured-macro>
<ac:structured-macro ac:name="code" ac:schema-version="1"><ac:parameter ac:name="language">csharp</ac:parameter><ac:plain-text-body><![CDATA[var source = new ConfluenceSource(settings);
if (a < b && c > d) { }]]></ac:plain-text-body></ac:structured-macro>
<table><tbody><tr><th>Layer</th><th>Project</th></tr><tr><td>SDK</td><td>AnyFS.Sdk</td></tr></tbody></table>
<p><ac:image ac:width="200"><ri:attachment ri:filename="diagram.png" /></ac:image></p>
<ac:structured-macro ac:name="expand" ac:schema-version="1"><ac:parameter ac:name="title">More detail</ac:parameter><ac:rich-text-body><p>Hidden until expanded.</p></ac:rich-text-body></ac:structured-macro>
<p>Status: <ac:structured-macro ac:name="status" ac:schema-version="1"><ac:parameter ac:name="title">In progress</ac:parameter><ac:parameter ac:name="colour">Yellow</ac:parameter></ac:structured-macro></p>
<ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>Draw the diagram</ac:task-body></ac:task><ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>Review it</ac:task-body></ac:task></ac:task-list>
<p>The spec: <ac:link><ri:attachment ri:filename="spec.pdf" /><ac:plain-text-link-body><![CDATA[spec.pdf]]></ac:plain-text-link-body></ac:link>. <ac:emoticon ac:name="smile" /></p>
<ac:structured-macro ac:name="warning" ac:schema-version="1"><ac:rich-text-body><p>Mind the gap.</p></ac:rich-text-body></ac:structured-macro>
<ac:structured-macro ac:name="children" ac:schema-version="2" />
STORAGE
)")
    services=$(conf_page ANYFS "Services" "$arch" "<p>The services, one level down.</p><ac:structured-macro ac:name=\"note\" ac:schema-version=\"1\"><ac:rich-text-body><p>A note.</p></ac:rich-text-body></ac:structured-macro>")
    storage=$(conf_page ANYFS "Storage" "$services" "<p>Three levels down. Back to <ac:link><ri:page ri:content-title=\"Architecture\" /></ac:link>.</p>")
    onboarding=$(conf_page ANYFS "Onboarding" "$home" "<h2>Day one</h2><ol><li>Install</li><li>Mount</li></ol><p>Notes: <ac:link><ri:attachment ri:filename=\"notes.txt\" /></ac:link></p>")
    notes=$(conf_page ANYFS 'What? Why: "quotes" <angles> / slash' "$home" "<p>A title with every character Windows refuses in a name.</p>")
    secret=$(conf_page ANYFS "Restricted page" "$home" "<p>The tester may not see this page.</p>")
    conf_page ANYFS "Ünïcödé title — 日本語" "$home" "<p>A title in other scripts.</p>" > /dev/null

    conf_attach "$arch" "$f/diagram.png"
    conf_attach "$arch" "$f/spec.pdf"
    conf_attach "$arch" "$f/Architecture.md"
    conf_attach "$onboarding" "$f/notes.txt"
    conf_attach "$services" "$f/large.bin"
    conf_attach "$home" "$f/notes.txt"
    conf_comment "$arch" "<p>A footer comment, with <strong>bold</strong>.</p>"
    conf_comment "$onboarding" "<p>Welcome aboard.</p>"

    # the restricted page: read by the administrator only
    if rest "$conf_admin_token" "$C/rest/api/content/$secret/restriction/byOperation/read" | jq -e '(.restrictions.user.results // []) | length == 0' > /dev/null; then
        jq -n --arg u "$ADMIN_USER" '[{operation: "read", restrictions: {user: [{type: "known", username: $u}]}}]' |
            rest "$conf_admin_token" -X PUT --data-binary @- "$C/rest/api/content/$secret/restriction" > /dev/null || true
        say "confluence: restricted page $secret to $ADMIN_USER"
    fi

    # blog posts
    for n in 1 2 3; do
        conf_page ANYFS "Release notes $n" "" "<p>What changed in release $n.</p>" blogpost > /dev/null
    done

    # BULK: 300 pages under its home page (paging)
    local bulk_home count
    bulk_home=$(rest "$conf_admin_token" "$C/rest/api/space/BULK?expand=homepage" | jq -r '.homepage.id')
    count=$(rest "$conf_admin_token" "$C/rest/api/content/$bulk_home/child/page?limit=1" | jq -r '.size')
    count=$(rest "$conf_admin_token" -G "$C/rest/api/search" --data-urlencode 'cql=space = BULK and type = page' --data-urlencode 'limit=0' | jq -r '.totalSize // 0')
    if [[ "$count" -lt 301 ]]; then
        for n in $(seq 1 300); do
            conf_page BULK "Bulk page $n" "$bulk_home" "<p>One of many: $n.</p>" > /dev/null 2>&1
        done
        say "confluence: made BULK's pages"
    fi

    # OLD: archived
    if rest "$conf_admin_token" "$C/rest/api/space/OLD" | jq -e '.status != "archived"' > /dev/null; then
        conf_page OLD "An old page" "" "<p>From an archived space.</p>" > /dev/null
        jq -n '{name: "An archived space", status: "archived"}' | rest "$conf_admin_token" -X PUT --data-binary @- "$C/rest/api/space/OLD" > /dev/null || true
        say "confluence: archived OLD"
    fi
}

# a product the setup left waiting for its licence is not running yet: it is seeded by the provision after its key
running() { [[ $(curl -s -m 10 "$1/status" | jq -r '.state // empty' 2>/dev/null) == RUNNING ]]; }
if running "$J"; then seed_jira; else say "jira: not set up yet (its licence), so not seeded"; fi
if running "$C"; then seed_confluence; else say "confluence: not set up yet (its licence), so not seeded"; fi
say "seed: done"
