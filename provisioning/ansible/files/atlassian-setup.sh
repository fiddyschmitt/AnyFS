#!/bin/bash
# The AnyFS lab's Atlassian setup (provisioning/atlassian/README.md): Jira's and Confluence's setup wizards answered over
# HTTP as a browser answers them, with the Data Center licence keys in $LICENCES. The steps and their fields are the
# products' own: Jira's actions.xml and setup pages, Confluence's struts.xml and setup templates, the same in Jira 10.3
# and 11.3 and in Confluence 9.2 and 10.2.
#
# Whatever is set up already is left as it is: each product's wizard is walked from wherever its first page leads a
# browser. Each product is offered every key in $LICENCES until one is taken, so a key's file name does not matter; a
# product that none of them fits waits for one, which is said and is not an error. It prints status lines only, never a
# page: a wizard's page can echo a key back or name the organisation a key belongs to.
#
#   atlassian-setup.sh /root/.atlassian-lab.env
set -euo pipefail

# the lab's values (URLs, the administrator's account): root's only, read without a shell parsing them
while IFS='=' read -r key value; do
    [[ -z "$key" || "$key" == \#* ]] && continue
    export "$key=$value"
done < "$1"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
changed=0
waiting=()

# the keys: every .lic or .txt file in the licences folder that holds something
keys=()
for f in "$LICENCES"/*.lic "$LICENCES"/*.txt; do
    [[ -s "$f" ]] && keys+=("$f")
done

# the form token of the page last fetched: Seraph's atl_token, in a hidden input
token() { tr '\n' ' ' < "$work/page.html" | grep -oE 'name="atl_token"[^>]*value="[^"]*"|value="[^"]*"[^>]*name="atl_token"' | head -1 | sed -E 's/.*value="([^"]*)".*/\1/'; }

# where a page ends up, its redirects followed: the path below the product (the page itself in $work/page.html). A
# wizard's step that needs nothing from the browser runs as its redirect is followed: Jira's database step makes its
# tables then (minutes), so the wait is long. $1 the product's address, $2 cookie jar, $3 the page
visit() {
    local url
    url=$(curl -sL --max-redirs 20 -m 900 -c "$work/$2.jar" -b "$work/$2.jar" -o "$work/page.html" -w '%{url_effective}' "$3") || true
    url=${url#"$1"}
    echo "${url#/}"
}

# The next page of a product's wizard: where the last answer's redirect leads, as a browser goes on, else where its first
# page leads. Jira's first page leads to its first step until its licence is in, whatever was answered since.
# $1 the product's address, $2 cookie jar; reads $answer
next_step() {
    local to=${answer#* }
    if [[ $answer == 3* && -n $to ]]; then visit "$1" "$2" "$to"; else visit "$1" "$2" "$1/"; fi
}

# POST a form (the remaining arguments are curl --data-urlencode pairs; name@file reads a value from a file); prints
# "<status> <redirect>"
post() {
    local url=$1 jar=$2; shift 2
    local args=()
    for pair in "$@"; do args+=(--data-urlencode "$pair"); done
    curl -s -m 900 -c "$work/$jar.jar" -b "$work/$jar.jar" -o "$work/page.html" -w '%{http_code} %{redirect_url}' -H 'X-Atlassian-Token: no-check' "${args[@]}" "$url" || true
}

# an error message the page shows, without anything that could be a key (long unbroken strings)
page_error() { tr '\n' ' ' < "$work/page.html" | grep -oE 'class="[^"]*(error|aui-message-error)[^"]*"[^>]*>[^<]{1,200}' | sed -E 's/.*>//; s/[A-Za-z0-9+\/=]{30,}/…/g' | head -2 | tr '\n' ' '; }

# Jira's own check of a key, which its licence page's script makes (a plain post shows no reason): the reason's words, in
# lower case. Its answer echoes the key, so only the reason is kept. $1 the key's file
jira_licence_reason() {
    curl -s -m 60 -c "$work/jira.jar" -b "$work/jira.jar" -H 'X-Atlassian-Token: no-check' --data-urlencode "licenseToValidate@$1" \
        --data-urlencode "atl_token=$(token)" "$JIRA_URL/secure/SetupLicense!validateLicense.jspa" |
        jq -r '[.errors[]?] | join(" ")' 2>/dev/null | tr '[:upper:]' '[:lower:]' || true
}

# what a refused key's error is about, in a few fixed words: the product's own words are not printed, in case they quote
# the key or name its holder. $1 the reason's words, when the product gave them otherwise than on its page
licence_error() {
    local text=${1:-} w about=()
    [[ -n $text ]] || text=$(perl -0777 -ne 'while (/class="[^"]*error[^"]*"[^>]*>(.*?)<\/div>/sg) { my $t = $1; $t =~ s/<[^>]+>/ /g; print lc($t), " " }' "$work/page.html")
    for w in valid version expire maintenance "support period" evaluation "data center" "server id" cluster user; do
        [[ $text == *"$w"* ]] && about+=("$w")
    done
    if ((${#about[@]})); then
        local IFS=,
        echo "its error mentions: ${about[*]}"
    elif [[ -n ${text//[[:space:]]/} ]]; then
        echo "an error about none of the usual things"
    else
        echo "no error shown"
    fi
}

# Offers each key to a product's licence form until one is taken: a key that is taken leaves the form behind (the next
# step, or a redirect to it); one that is not shows the form again with an error. The key goes from a file (curl's
# name@file), so it is never on a command line. $1 product, $2 the form's action, $3 cookie jar, $4 the key's field, then
# the form's other pairs. 0 when a key was taken; $answer is the last post's.
offer_keys() {
    local product=$1 form=$2 jar=$3 field=$4; shift 4
    local f status reason
    if ((${#keys[@]} == 0)); then
        echo "$product: waits for its licence: no key in provisioning/atlassian/licences"
        return 1
    fi
    for f in "${keys[@]}"; do
        # a byte-order mark (Notepad's) and the line breaks a key is shown with are not the key's
        LC_ALL=C sed '1s/^\xEF\xBB\xBF//' "$f" | tr -d ' \t\r\n' > "$work/licence"
        answer=$(post "$form" "$jar" "$field@$work/licence" "$@" "atl_token=$(token)")
        status=${answer%% *}
        if [[ $status == 5* || $status == 000 ]]; then
            rm -f "$work/licence"
            echo "$product: the licence step failed ($status) with the key in $(basename "$f")"
            return 1
        fi
        if ! grep -q "name=\"$field\"" "$work/page.html"; then
            rm -f "$work/licence"
            echo "$product: took the key in $(basename "$f") ($status)"
            changed=1
            return 0
        fi
        reason=""
        [[ $product == jira ]] && reason=$(jira_licence_reason "$work/licence")
        rm -f "$work/licence"
        echo "$product: refused the key in $(basename "$f") ($(licence_error "$reason"))"
    done
    echo "$product: waits for its licence: none of the ${#keys[@]} key(s) in provisioning/atlassian/licences fits it"
    return 1
}

# --- Jira -------------------------------------------------------------------------------------------------------------
# secure/SetupDatabase (the image's dbconfig.xml) → SetupApplicationProperties → SetupLicense → SetupAdminAccount →
# SetupMailNotifications → SetupComplete (Jira 10.3 and 11.3 alike).
setup_jira() {
    local j=$JIRA_URL step answer=""
    for _ in $(seq 1 12); do
        step=$(next_step "$j" jira)
        answer=""
        case "$step" in
            "" | secure/Dashboard.jspa* | secure/MyJiraHome.jspa* | secure/WelcomeToJIRA.jspa* | login.jsp*)
                echo "jira: set up"
                return 0 ;;
            secure/SetupDatabase*)
                # its page, rather than the redirect past it: the tables are still being made, or the settings are wrong
                echo "jira: at the database step $(page_error)"
                sleep 20 ;;
            secure/SetupApplicationProperties*)
                # asked again after a restart until the licence is in: the same answers
                answer=$(post "$j/secure/SetupApplicationProperties.jspa" jira "title=AnyFS Lab Jira" "mode=private" "baseURL=$j" "nextStep=true" "next=Next" "atl_token=$(token)")
                echo "jira: title, private mode and base address: ${answer%% *} $(page_error)" ;;
            secure/SetupLicense*)
                offer_keys jira "$j/secure/SetupLicense.jspa" jira setupLicenseKey "next=Next" || { waiting+=(Jira); return 0; } ;;
            secure/SetupAdminAccount*)
                answer=$(post "$j/secure/SetupAdminAccount.jspa" jira "fullname=AnyFS Lab Administrator" "email=$ADMIN_USER@anyfs.lab" "username=$ADMIN_USER" \
                    "password=$ADMIN_PASSWORD" "confirm=$ADMIN_PASSWORD" "next=Next" "atl_token=$(token)")
                echo "jira: the administrator $ADMIN_USER: ${answer%% *} $(page_error)"
                changed=1 ;;
            secure/SetupMailNotifications*)
                answer=$(post "$j/secure/SetupMailNotifications.jspa" jira "noemail=true" "finish=Finish" "atl_token=$(token)")
                echo "jira: no mail: ${answer%% *} $(page_error)"
                changed=1 ;;
            secure/SetupComplete* | secure/SetupFinishing*)
                echo "jira: finishing"
                sleep 20 ;;
            *)
                echo "jira: an unknown setup step: ${step%%\?*}"
                return 1 ;;
        esac
    done

    echo "jira: the setup did not finish"
    return 1
}

# --- Confluence -------------------------------------------------------------------------------------------------------
# setup/setupstart → setuplicense → (a Data Center licence) setupcluster-start → setupdbchoice-start → setupdbtype-start
# (both passed by themselves: the image's confluence.cfg.xml holds the database) → setuppaths → setupdata-start →
# setupusermanagementchoice-start → setupadministrator-start → finishsetup.
setup_confluence() {
    local c=$CONFLUENCE_URL step answer=""
    for _ in $(seq 1 16); do
        step=$(next_step "$c" confluence)
        answer=""
        case "$step" in
            "" | index.action* | dashboard.action* | login.action* | welcome.action* | homepage.action* | spaces/*)
                echo "confluence: set up"
                return 0 ;;
            setup/setupstart.action* | bootstrap/selectsetupstep.action*)
                # a custom setup, the one that takes a key (a trial would ask Atlassian for one)
                answer=$(post "$c/setup/dosetupstart.action" confluence "setupType=custom" "atl_token=$(token)")
                echo "confluence: a custom setup: ${answer%% *} $(page_error)" ;;
            setup/setuplicense.action* | setup/dosetuplicense.action*)
                offer_keys confluence "$c/setup/dosetuplicense.action" confluence confLicenseString "setupTypeCustom=Next" || { waiting+=(Confluence); return 0; } ;;
            setup/setupcluster-start.action* | setup/setupcluster.action*)
                # Data Center asks whether this node begins a cluster: one node (the page's own button sets newCluster so)
                answer=$(post "$c/setup/setupcluster.action" confluence "newCluster=skipCluster" "atl_token=$(token)")
                echo "confluence: one node, no cluster: ${answer%% *} $(page_error)"
                changed=1 ;;
            setup/setupdbchoice-start.action* | setup/setupdbchoice.action*)
                # passed by itself when the image has written the database's settings; shown, it is "my own database"
                answer=$(post "$c/setup/setupdbchoice.action" confluence "dbChoice=custom" "thisNodeClustered=false" "atl_token=$(token)")
                echo "confluence: its own database: ${answer%% *} $(page_error)" ;;
            setup/setupdbtype-start.action* | setup/setupdbtype.action*)
                # passed by itself as well; shown, it takes the container's own settings (the password from a file)
                docker exec confluence printenv ATL_JDBC_PASSWORD | tr -d '\n' > "$work/db"
                answer=$(post "$c/setup/setupdbtype.action" confluence "database=postgresql" "dbConfigInfo.databaseType=postgresql" "dbConfigInfo.simple=false" \
                    "dbConfigInfo.databaseUrl=$(docker exec confluence printenv ATL_JDBC_URL)" "dbConfigInfo.userName=$(docker exec confluence printenv ATL_JDBC_USER)" \
                    "dbConfigInfo.password@$work/db" "atl_token=$(token)")
                rm -f "$work/db"
                echo "confluence: the database's settings: ${answer%% *} $(page_error)" ;;
            setup/setuppaths.action*)
                answer=$(post "$c/setup/setuppaths.action" confluence "setupTypeCustom=Next" "atl_token=$(token)")
                echo "confluence: its folders as the image has them: ${answer%% *} $(page_error)" ;;
            setup/setupdata-start.action* | setup/setupdata.action*)
                answer=$(post "$c/setup/setupdata.action" confluence "contentChoice=blank" "dbchoiceSelect=Empty Site" "atl_token=$(token)")
                echo "confluence: an empty site: ${answer%% *} $(page_error)"
                changed=1 ;;
            setup/setupusermanagementchoice-start.action* | setup/setupusermanagementchoice.action*)
                answer=$(post "$c/setup/setupusermanagementchoice.action" confluence "userManagementChoice=internal" "internal=Next" "atl_token=$(token)")
                echo "confluence: users kept in Confluence: ${answer%% *} $(page_error)"
                changed=1 ;;
            setup/setupadministrator-start.action* | setup/setupadministrator.action*)
                answer=$(post "$c/setup/setupadministrator.action" confluence "username=$ADMIN_USER" "fullName=AnyFS Lab Administrator" "email=$ADMIN_USER@anyfs.lab" \
                    "password=$ADMIN_PASSWORD" "confirm=$ADMIN_PASSWORD" "setup-next-button=Next" "atl_token=$(token)")
                echo "confluence: the administrator $ADMIN_USER: ${answer%% *} $(page_error)"
                changed=1 ;;
            setup/finishsetup.action* | setup/finishclustersetup.action*)
                # reached is done: the step runs as its page is fetched
                echo "confluence: finished $(page_error)"
                sleep 10 ;;
            johnson/* | errors.jsp*)
                # Confluence's own "starting" or problem page
                echo "confluence: not ready ($step) $(page_error)"
                sleep 20 ;;
            *)
                echo "confluence: an unknown setup step: ${step%%\?*}"
                return 1 ;;
        esac
    done

    echo "confluence: the setup did not finish"
    return 1
}

failed=0
setup_jira || failed=1
setup_confluence || failed=1
[[ $changed == 1 ]] && echo "set up: something changed"
if ((${#waiting[@]})); then echo "waiting for a licence: ${waiting[*]}"; fi
exit $failed
