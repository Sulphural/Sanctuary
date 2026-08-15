#!/usr/bin/env bash
#
# manage-accounts.sh - view / add / update / delete Sanctuary accounts, live.
#
# Reads the server's own database.json so it always talks to the same database the
# running server does, and supports both providers (Sqlite and MySql).
#
# Run with no arguments for an interactive menu, or use the subcommands directly:
#
#   ./manage-accounts.sh list
#   ./manage-accounts.sh show <username>
#   ./manage-accounts.sh add <username> [password]
#   ./manage-accounts.sh passwd <username> [password]
#   ./manage-accounts.sh rename <username> <newname>
#   ./manage-accounts.sh member <username> on|off
#   ./manage-accounts.sh admin  <username> on|off
#   ./manage-accounts.sh mod    <username> on|off
#   ./manage-accounts.sh maxchars <username> <n>
#   ./manage-accounts.sh lock <username> <30m|2h|7d|off>
#   ./manage-accounts.sh mute <username> <30m|2h|7d|off>
#   ./manage-accounts.sh logout <username>
#   ./manage-accounts.sh delete <username>
#   ./manage-accounts.sh sync-members
#
# The database is found by looking for database.json next to this script, then in
# ../bin/Release, then ../../bin/Release. Override with --config <path> or
# SANCTUARY_DB_CONFIG=<path>.
#
# Safe to run against a live server. Note that the SQLite database runs in WAL mode, so a
# backup must copy sanctuary.db-wal and sanctuary.db-shm alongside sanctuary.db - copying
# the .db on its own silently loses recent writes.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

CONFIG=""
PROVIDER=""
SQLITE_PATH=""
MYSQL_DEFAULTS_FILE=""
MYSQL_DATABASE=""
PYTHON=""

# BCrypt.Net-Next's default work factor - matches the hashes the server writes.
BCRYPT_COST=11

# --------------------------------------------------------------------------------------
# output helpers
# --------------------------------------------------------------------------------------

if [[ -t 1 ]]; then
    C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
    C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_CYAN=$'\033[36m'
else
    C_RESET=""; C_BOLD=""; C_DIM=""; C_RED=""; C_GREEN=""; C_YELLOW=""; C_CYAN=""
fi

info()  { printf '%s\n' "$*"; }
ok()    { printf '%s%s%s\n' "$C_GREEN" "$*" "$C_RESET"; }
warn()  { printf '%s%s%s\n' "$C_YELLOW" "$*" "$C_RESET" >&2; }
die()   { printf '%s%s%s\n' "$C_RED" "$*" "$C_RESET" >&2; exit 1; }

# --------------------------------------------------------------------------------------
# config discovery + parsing
# --------------------------------------------------------------------------------------

find_config() {
    if [[ -n "${SANCTUARY_DB_CONFIG:-}" ]]; then
        [[ -f "$SANCTUARY_DB_CONFIG" ]] || die "SANCTUARY_DB_CONFIG points at a missing file: $SANCTUARY_DB_CONFIG"
        CONFIG="$SANCTUARY_DB_CONFIG"
        return
    fi

    local candidate
    for candidate in \
        "$SCRIPT_DIR/database.json" \
        "$SCRIPT_DIR/../bin/Release/database.json" \
        "$SCRIPT_DIR/../../bin/Release/database.json" \
        "$SCRIPT_DIR/../bin/Debug/database.json"
    do
        if [[ -f "$candidate" ]]; then
            CONFIG="$(cd -- "$(dirname -- "$candidate")" && pwd)/$(basename -- "$candidate")"
            return
        fi
    done

    die "Could not find database.json. Pass one with --config <path>."
}

# Pull "Key": "value" out of the JSON. Small and dependency-free on purpose - database.json
# is a flat two-key file written by the server, not arbitrary JSON.
json_value() {
    local key="$1"
    sed -n "s/.*\"$key\"[[:space:]]*:[[:space:]]*\"\(.*\)\".*/\1/p" "$CONFIG" | head -1
}

# Read Key=Value out of an ADO.NET connection string, case-insensitively.
conn_value() {
    local wanted="$1" conn="$2" pair key value
    local IFS=';'
    for pair in $conn; do
        key="${pair%%=*}"
        value="${pair#*=}"
        [[ "$pair" == *"="* ]] || continue
        key="$(printf '%s' "$key" | tr -d ' ' | tr '[:upper:]' '[:lower:]')"
        if [[ "$key" == "$wanted" ]]; then
            printf '%s' "$value"
            return
        fi
    done
}

load_config() {
    local provider conn
    provider="$(json_value Provider)"
    conn="$(json_value ConnectionString)"

    # The connection string is JSON-escaped, so Windows paths arrive as C:\\path\\file.
    conn="${conn//\\\\/\\}"

    [[ -n "$provider" ]] || die "No \"Provider\" found in $CONFIG"
    [[ -n "$conn" ]] || die "No \"ConnectionString\" found in $CONFIG"

    case "$(printf '%s' "$provider" | tr '[:upper:]' '[:lower:]')" in
        sqlite) PROVIDER="sqlite"; load_sqlite "$conn" ;;
        mysql)  PROVIDER="mysql";  load_mysql "$conn" ;;
        *)      die "Unsupported database provider: $provider" ;;
    esac
}

load_sqlite() {
    local conn="$1" path
    path="$(conn_value "datasource" "$conn")"
    [[ -n "$path" ]] || path="$(conn_value "filename" "$conn")"
    [[ -n "$path" ]] || die "Could not read the SQLite path out of the connection string."

    # Accept Windows-style paths so this also runs under Git Bash / WSL.
    path="${path//\\//}"
    if [[ "$path" =~ ^([A-Za-z]):/(.*)$ ]]; then
        path="/${BASH_REMATCH[1],}/${BASH_REMATCH[2]}"
    fi

    # A relative Data Source is relative to the server's working directory, i.e. where
    # database.json lives.
    [[ "$path" = /* ]] || path="$(dirname -- "$CONFIG")/$path"

    [[ -f "$path" ]] || die "SQLite database not found: $path"
    SQLITE_PATH="$path"

    if ! command -v sqlite3 >/dev/null 2>&1; then
        find_python || die "Need either the 'sqlite3' command or python3. Install with: sudo apt install sqlite3"
    fi
}

load_mysql() {
    local conn="$1" host port user pass db
    host="$(conn_value "server" "$conn")"
    [[ -n "$host" ]] || host="$(conn_value "host" "$conn")"
    [[ -n "$host" ]] || host="$(conn_value "datasource" "$conn")"
    [[ -n "$host" ]] || host="127.0.0.1"

    port="$(conn_value "port" "$conn")"
    [[ -n "$port" ]] || port=3306

    user="$(conn_value "userid" "$conn")"
    [[ -n "$user" ]] || user="$(conn_value "user" "$conn")"
    [[ -n "$user" ]] || user="$(conn_value "username" "$conn")"
    [[ -n "$user" ]] || user="$(conn_value "uid" "$conn")"

    pass="$(conn_value "password" "$conn")"
    [[ -n "$pass" ]] || pass="$(conn_value "pwd" "$conn")"

    db="$(conn_value "database" "$conn")"
    [[ -n "$db" ]] || db="$(conn_value "initialcatalog" "$conn")"
    [[ -n "$db" ]] || die "Could not read the database name out of the connection string."

    command -v mysql >/dev/null 2>&1 || die "The 'mysql' client is required. Install with: sudo apt install mysql-client"

    MYSQL_DATABASE="$db"

    # Credentials go in a 0600 temp file rather than argv, so they never show up in `ps`.
    MYSQL_DEFAULTS_FILE="$(mktemp)"
    chmod 600 "$MYSQL_DEFAULTS_FILE"
    {
        printf '[client]\n'
        printf 'host=%s\n' "$host"
        printf 'port=%s\n' "$port"
        [[ -n "$user" ]] && printf 'user=%s\n' "$user"
        [[ -n "$pass" ]] && printf 'password=%s\n' "$pass"
    } > "$MYSQL_DEFAULTS_FILE"

    trap 'rm -f "$MYSQL_DEFAULTS_FILE"' EXIT
}

find_python() {
    local candidate
    for candidate in python3 python; do
        if command -v "$candidate" >/dev/null 2>&1; then
            PYTHON="$candidate"
            return 0
        fi
    done
    return 1
}

# --------------------------------------------------------------------------------------
# SQL plumbing
# --------------------------------------------------------------------------------------

# Escape a value for single-quoted SQL. MySQL treats backslash as an escape character,
# SQLite does not, so double both the quote and the backslash - safe for either.
sql_quote() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\'/\'\'}"
    printf "'%s'" "$value"
}

sqlite_run() {
    local sql="$1" mode="$2"
    if command -v sqlite3 >/dev/null 2>&1; then
        if [[ "$mode" == "query" ]]; then
            sqlite3 -separator $'\t' "$SQLITE_PATH" "PRAGMA busy_timeout=10000; $sql"
        else
            sqlite3 "$SQLITE_PATH" "PRAGMA busy_timeout=10000; $sql SELECT changes();"
        fi
    else
        "$PYTHON" - "$SQLITE_PATH" "$sql" "$mode" <<'PYEOF'
import sqlite3, sys
db, sql, mode = sys.argv[1], sys.argv[2], sys.argv[3]
con = sqlite3.connect(db, timeout=10)
con.execute("PRAGMA busy_timeout=10000")
cur = con.execute(sql)
if mode == "query":
    for row in cur.fetchall():
        print("\t".join("" if v is None else str(v) for v in row))
else:
    con.commit()
    print(cur.rowcount)
con.close()
PYEOF
    fi
}

mysql_run() {
    local sql="$1" mode="$2"
    if [[ "$mode" == "query" ]]; then
        mysql --defaults-extra-file="$MYSQL_DEFAULTS_FILE" -N -B -e "$sql" "$MYSQL_DATABASE"
    else
        mysql --defaults-extra-file="$MYSQL_DEFAULTS_FILE" -N -B -e "$sql SELECT ROW_COUNT();" "$MYSQL_DATABASE"
    fi
}

# db_query <sql>  -> tab-separated rows on stdout
db_query() {
    case "$PROVIDER" in
        sqlite) sqlite_run "$1" query ;;
        mysql)  mysql_run  "$1" query ;;
    esac
}

# db_exec <sql>   -> number of affected rows on stdout
db_exec() {
    case "$PROVIDER" in
        sqlite) sqlite_run "$1" exec | tail -1 ;;
        mysql)  mysql_run  "$1" exec | tail -1 ;;
    esac
}

# --------------------------------------------------------------------------------------
# values the schema cares about
# --------------------------------------------------------------------------------------

# EF stores DateTimeOffset as text on SQLite and datetime(6) on MySQL. Emit whichever
# the running server will be able to parse back.
timestamp_from_now() {
    local seconds="$1"
    if [[ "$PROVIDER" == "sqlite" ]]; then
        date -u -d "+${seconds} seconds" +"%Y-%m-%d %H:%M:%S.0000000+00:00"
    else
        date -u -d "+${seconds} seconds" +"%Y-%m-%d %H:%M:%S.000000"
    fi
}

# 30m / 2h / 7d / 90s -> seconds
duration_to_seconds() {
    local input="$1" number unit
    [[ "$input" =~ ^([0-9]+)([smhd])?$ ]] || return 1
    number="${BASH_REMATCH[1]}"
    unit="${BASH_REMATCH[2]:-m}"
    case "$unit" in
        s) printf '%s' "$number" ;;
        m) printf '%s' $(( number * 60 )) ;;
        h) printf '%s' $(( number * 3600 )) ;;
        d) printf '%s' $(( number * 86400 )) ;;
    esac
}

# Same rule the server's RegisterRequestModel enforces, so accounts made here behave
# exactly like accounts made through the website.
validate_username() {
    local name="$1"
    [[ ${#name} -ge 3 && ${#name} -le 50 ]] || { warn "Username must be 3-50 characters."; return 1; }
    [[ "$name" =~ ^[a-zA-Z0-9_.]+$ ]] || { warn "Username may only contain letters, numbers, underscores and dots."; return 1; }
    return 0
}

validate_password() {
    local pass="$1"
    [[ ${#pass} -ge 6 && ${#pass} -le 100 ]] || { warn "Password must be 6-100 characters."; return 1; }
    [[ "$pass" =~ ^[[:print:]]+$ ]] || { warn "Password must be printable ASCII."; return 1; }
    return 0
}

# --------------------------------------------------------------------------------------
# bcrypt
# --------------------------------------------------------------------------------------

bcrypt_hash() {
    local password="$1" hash=""

    if find_python && "$PYTHON" -c "import bcrypt" >/dev/null 2>&1; then
        hash="$("$PYTHON" - "$password" "$BCRYPT_COST" <<'PYEOF'
import bcrypt, sys
password, cost = sys.argv[1], int(sys.argv[2])
print(bcrypt.hashpw(password.encode(), bcrypt.gensalt(cost, prefix=b"2a")).decode())
PYEOF
)"
    elif command -v htpasswd >/dev/null 2>&1; then
        hash="$(htpasswd -bnBC "$BCRYPT_COST" "" "$password" | tr -d '\n' | sed 's/^://')"
        # htpasswd emits the $2y$ revision. BCrypt.Net reads $2a$, and the two are identical
        # for the ASCII passwords the server accepts, so normalise the prefix.
        hash="${hash/#\$2y\$/\$2a\$}"
    else
        die "No bcrypt hasher available. Install one with:
    sudo apt install python3-bcrypt
  or
    sudo apt install apache2-utils"
    fi

    [[ "$hash" == \$2a\$* ]] || die "Generated an unexpected hash format: $hash"
    [[ ${#hash} -eq 60 ]] || die "Generated a hash of the wrong length (${#hash}, expected 60)."

    printf '%s' "$hash"
}

read_password_twice() {
    local first second
    read -rsp "New password: " first; echo >&2
    read -rsp "Repeat password: " second; echo >&2
    [[ "$first" == "$second" ]] || { warn "Passwords did not match."; return 1; }
    validate_password "$first" || return 1
    printf '%s' "$first"
}

# --------------------------------------------------------------------------------------
# account lookups
# --------------------------------------------------------------------------------------

user_id_for() {
    local name="$1"
    db_query "SELECT Id FROM Users WHERE Username = $(sql_quote "$name");" | head -1
}

require_user_id() {
    local name="$1" id
    id="$(user_id_for "$name")"
    [[ -n "$id" ]] || die "No such account: $name"
    printf '%s' "$id"
}

confirm() {
    local prompt="$1" answer
    read -rp "$prompt [y/N] " answer
    [[ "$answer" == [yY] || "$answer" == [yY][eE][sS] ]]
}

# --------------------------------------------------------------------------------------
# commands
# --------------------------------------------------------------------------------------

cmd_list() {
    local rows
    rows="$(db_query "
        SELECT u.Id, u.Username, u.IsMember, u.IsAdmin, u.IsMod, u.MaxCharacters,
               (SELECT COUNT(*) FROM Characters c WHERE c.UserId = u.Id),
               CASE WHEN u.LockedUntil IS NULL THEN '-' ELSE 'LOCKED' END,
               CASE WHEN u.MutedUntil  IS NULL THEN '-' ELSE 'MUTED'  END,
               CASE WHEN u.Session IS NULL THEN '-' ELSE 'yes' END
        FROM Users u
        ORDER BY u.Id;")"

    if [[ -z "$rows" ]]; then
        info "No accounts."
        return
    fi

    {
        printf 'ID\tUSERNAME\tMEMBER\tADMIN\tMOD\tMAXCH\tCHARS\tLOCK\tMUTE\tSESSION\n'
        printf '%s\n' "$rows"
    } | awk -F'\t' -v bold="$C_BOLD" -v reset="$C_RESET" '
        NR == 1 { printf "%s", bold }
        {
            for (i = 3; i <= 5; i++)
                if (NR > 1) $i = ($i == 1 ? "yes" : "-")
            printf "%-5s %-20s %-7s %-6s %-5s %-6s %-6s %-7s %-6s %-7s\n",
                   $1, $2, $3, $4, $5, $6, $7, $8, $9, $10
        }
        NR == 1 { printf "%s", reset }'
}

cmd_show() {
    local name="${1:-}"
    [[ -n "$name" ]] || die "Usage: show <username>"

    # Every column is COALESCEd to '-' rather than '': tab is IFS whitespace, so bash collapses
    # runs of it and an empty column would silently shift every field after it.
    local row
    row="$(db_query "
        SELECT Id, Username, IsMember, IsAdmin, IsMod, MaxCharacters,
               COALESCE(Created, '-'), COALESCE(LastLogin, '-'),
               COALESCE(LockedUntil, '-'), COALESCE(MutedUntil, '-'),
               CASE WHEN Session IS NULL THEN 'no' ELSE 'yes' END,
               Password
        FROM Users WHERE Username = $(sql_quote "$name");")"

    [[ -n "$row" ]] || die "No such account: $name"

    IFS=$'\t' read -r id username member admin mod maxch created lastlogin locked muted session hash <<< "$row"

    printf '%s%s%s (account %s)\n' "$C_BOLD" "$username" "$C_RESET" "$id"
    printf '  Member       : %s\n' "$([[ "$member" == 1 ]] && echo yes || echo no)"
    printf '  Admin / Mod  : %s / %s\n' \
        "$([[ "$admin" == 1 ]] && echo yes || echo no)" \
        "$([[ "$mod" == 1 ]] && echo yes || echo no)"
    printf '  Max chars    : %s\n' "$maxch"
    printf '  Created      : %s\n' "$created"
    printf '  Last login   : %s\n' "$lastlogin"
    printf '  Locked until : %s\n' "$locked"
    printf '  Muted until  : %s\n' "$muted"
    printf '  Has session  : %s\n' "$session"
    printf '  Password     : %s%s%s\n' "$C_DIM" "${hash:0:7}...(bcrypt)" "$C_RESET"

    local chars
    chars="$(db_query "
        SELECT Id, FirstName, COALESCE(NULLIF(LastName, ''), '-'), MembershipStatus
        FROM Characters WHERE UserId = $id ORDER BY Id;")"

    if [[ -z "$chars" ]]; then
        printf '  Characters   : none\n'
        return
    fi

    printf '  Characters   :\n'
    while IFS=$'\t' read -r cid first last mstatus; do
        [[ -n "$cid" ]] || continue
        local flag=""
        # MembershipStatus is stamped at character creation and never refreshed, so it can
        # disagree with the account. Call that out rather than hide it.
        if [[ "$member" == 1 && "$mstatus" == 0 ]]; then
            flag=" ${C_YELLOW}<- non-member character on a member account (run sync-members)${C_RESET}"
        fi
        [[ "$last" == "-" ]] && last=""
        printf '     [%s] %s %s MembershipStatus=%s%s\n' "$cid" "$first" "$last" "$mstatus" "$flag"
    done <<< "$chars"
}

cmd_add() {
    local name="${1:-}" password="${2:-}"

    if [[ -z "$name" ]]; then
        read -rp "New username: " name
    fi
    validate_username "$name" || return 1

    [[ -z "$(user_id_for "$name")" ]] || die "That username is already taken: $name"

    if [[ -z "$password" ]]; then
        password="$(read_password_twice)" || return 1
    else
        validate_password "$password" || return 1
    fi

    local hash created
    hash="$(bcrypt_hash "$password")"
    created="$(timestamp_from_now 0)"

    # Every column is written explicitly. Leaving IsMember to the column default is exactly
    # the bug that made new accounts non-member, so never rely on defaults here.
    db_exec "INSERT INTO Users
                 (Username, Password, MaxCharacters, IsMember, IsAdmin, IsMod, Created)
             VALUES
                 ($(sql_quote "$name"), $(sql_quote "$hash"), 5, 1, 0, 0, $(sql_quote "$created"));" >/dev/null

    ok "Created account '$name' (member, 5 character slots)."
}

cmd_passwd() {
    local name="${1:-}" password="${2:-}"
    [[ -n "$name" ]] || { read -rp "Account: " name; }
    require_user_id "$name" >/dev/null

    if [[ -z "$password" ]]; then
        password="$(read_password_twice)" || return 1
    else
        validate_password "$password" || return 1
    fi

    local hash
    hash="$(bcrypt_hash "$password")"

    db_exec "UPDATE Users SET Password = $(sql_quote "$hash") WHERE Username = $(sql_quote "$name");" >/dev/null
    ok "Password updated for '$name'."
    info "${C_DIM}Any existing session stays valid until they log out.${C_RESET}"
}

cmd_rename() {
    local name="${1:-}" newname="${2:-}"
    [[ -n "$name" && -n "$newname" ]] || die "Usage: rename <username> <newname>"
    require_user_id "$name" >/dev/null
    validate_username "$newname" || return 1
    [[ -z "$(user_id_for "$newname")" ]] || die "That username is already taken: $newname"

    db_exec "UPDATE Users SET Username = $(sql_quote "$newname") WHERE Username = $(sql_quote "$name");" >/dev/null
    ok "Renamed '$name' to '$newname'."
}

# Shared by member/admin/mod.
set_flag() {
    local column="$1" name="$2" state="$3" value

    case "$(printf '%s' "$state" | tr '[:upper:]' '[:lower:]')" in
        on|yes|true|1)   value=1 ;;
        off|no|false|0)  value=0 ;;
        *) die "Usage: ${column} <username> on|off" ;;
    esac

    local id
    id="$(require_user_id "$name")"

    db_exec "UPDATE Users SET $column = $value WHERE Id = $id;" >/dev/null
    ok "$column for '$name' set to $([[ $value == 1 ]] && echo on || echo off)."

    # Membership is read from the CHARACTER at login, not the account, so the account flag
    # alone changes nothing for characters that already exist.
    if [[ "$column" == "IsMember" ]]; then
        local status=$(( value == 1 ? 2 : 0 ))
        local changed
        changed="$(db_exec "UPDATE Characters SET MembershipStatus = $status WHERE UserId = $id;")"
        info "Updated ${changed:-0} character(s) to MembershipStatus=$status."
    fi
}

cmd_maxchars() {
    local name="${1:-}" count="${2:-}"
    [[ -n "$name" && -n "$count" ]] || die "Usage: maxchars <username> <n>"
    [[ "$count" =~ ^[0-9]+$ ]] || die "Character count must be a number."
    require_user_id "$name" >/dev/null

    db_exec "UPDATE Users SET MaxCharacters = $count WHERE Username = $(sql_quote "$name");" >/dev/null
    ok "'$name' can now have $count characters."
}

# Shared by lock/mute.
set_until() {
    local column="$1" label="$2" name="$3" duration="$4"
    require_user_id "$name" >/dev/null

    if [[ "$(printf '%s' "$duration" | tr '[:upper:]' '[:lower:]')" =~ ^(off|none|clear|0)$ ]]; then
        db_exec "UPDATE Users SET $column = NULL WHERE Username = $(sql_quote "$name");" >/dev/null
        ok "'$name' is no longer ${label}."
        return
    fi

    local seconds until
    seconds="$(duration_to_seconds "$duration")" \
        || die "Could not read the duration '$duration'. Use forms like 30m, 2h, 7d, or off."
    until="$(timestamp_from_now "$seconds")"

    db_exec "UPDATE Users SET $column = $(sql_quote "$until") WHERE Username = $(sql_quote "$name");" >/dev/null
    ok "'$name' is ${label} until $until UTC."
}

cmd_logout() {
    local name="${1:-}"
    [[ -n "$name" ]] || die "Usage: logout <username>"
    require_user_id "$name" >/dev/null

    db_exec "UPDATE Users SET Session = NULL, SessionCreated = NULL WHERE Username = $(sql_quote "$name");" >/dev/null
    ok "Cleared the saved session for '$name'."
    info "${C_DIM}This invalidates the login ticket; it does not disconnect someone already in-world.${C_RESET}"
}

cmd_delete() {
    local name="${1:-}"
    [[ -n "$name" ]] || die "Usage: delete <username>"

    local id charcount
    id="$(require_user_id "$name")"
    charcount="$(db_query "SELECT COUNT(*) FROM Characters WHERE UserId = $id;")"

    warn "About to delete account '$name' (id $id) and its ${charcount} character(s)."
    warn "Characters cascade-delete, taking their items, pets, mounts, quests and houses with them."
    confirm "Delete permanently?" || { info "Cancelled."; return; }

    db_exec "DELETE FROM Users WHERE Id = $id;" >/dev/null
    ok "Deleted '$name'."
}

cmd_sync_members() {
    local changed
    changed="$(db_exec "
        UPDATE Characters SET MembershipStatus =
            CASE WHEN (SELECT u.IsMember FROM Users u WHERE u.Id = Characters.UserId) = 1
                 THEN 2 ELSE 0 END
        WHERE MembershipStatus <>
            CASE WHEN (SELECT u.IsMember FROM Users u WHERE u.Id = Characters.UserId) = 1
                 THEN 2 ELSE 0 END;")"
    ok "Re-derived membership for ${changed:-0} character(s) from their account's IsMember."
}

# --------------------------------------------------------------------------------------
# interactive menu
# --------------------------------------------------------------------------------------

menu() {
    local choice name state count duration

    while true; do
        printf '\n%s== Sanctuary accounts ==%s  %s(%s)%s\n' \
            "$C_BOLD" "$C_RESET" "$C_DIM" "$(source_label)" "$C_RESET"
        cat <<'MENU'
   1) List accounts            7) Set admin on/off
   2) Show one account         8) Set moderator on/off
   3) Add account              9) Set max characters
   4) Change password         10) Lock / unlock account
   5) Delete account          11) Mute / unmute account
   6) Set member on/off       12) Clear saved session
                              13) Re-sync character membership
   q) Quit
MENU
        read -rp "> " choice || return 0

        case "$choice" in
            1) cmd_list ;;
            2) read -rp "Account: " name; cmd_show "$name" ;;
            3) cmd_add || true ;;
            4) read -rp "Account: " name; cmd_passwd "$name" || true ;;
            5) read -rp "Account: " name; cmd_delete "$name" ;;
            6) read -rp "Account: " name; read -rp "Member on/off: " state; set_flag IsMember "$name" "$state" ;;
            7) read -rp "Account: " name; read -rp "Admin on/off: " state; set_flag IsAdmin "$name" "$state" ;;
            8) read -rp "Account: " name; read -rp "Moderator on/off: " state; set_flag IsMod "$name" "$state" ;;
            9) read -rp "Account: " name; read -rp "Max characters: " count; cmd_maxchars "$name" "$count" ;;
           10) read -rp "Account: " name; read -rp "Duration (30m/2h/7d) or off: " duration; set_until LockedUntil "locked" "$name" "$duration" ;;
           11) read -rp "Account: " name; read -rp "Duration (30m/2h/7d) or off: " duration; set_until MutedUntil "muted" "$name" "$duration" ;;
           12) read -rp "Account: " name; cmd_logout "$name" ;;
           13) cmd_sync_members ;;
            q|Q|quit|exit) return 0 ;;
            *) warn "Unknown choice: $choice" ;;
        esac
    done
}

source_label() {
    if [[ "$PROVIDER" == "sqlite" ]]; then
        printf 'sqlite: %s' "$SQLITE_PATH"
    else
        printf 'mysql: %s' "$MYSQL_DATABASE"
    fi
}

usage() {
    sed -n '3,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

# --------------------------------------------------------------------------------------
# entry point
# --------------------------------------------------------------------------------------

main() {
    local args=()
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --config) CONFIG="${2:-}"; shift 2
                      [[ -f "$CONFIG" ]] || die "No such config file: $CONFIG" ;;
            -h|--help) usage; exit 0 ;;
            *) args+=("$1"); shift ;;
        esac
    done

    [[ -n "$CONFIG" ]] || find_config
    load_config

    if [[ ${#args[@]} -eq 0 ]]; then
        menu
        return
    fi

    local command="${args[0]}"
    local rest=("${args[@]:1}")

    case "$command" in
        list)          cmd_list ;;
        show)          cmd_show "${rest[@]:-}" ;;
        add)           cmd_add "${rest[@]:-}" ;;
        passwd|password) cmd_passwd "${rest[@]:-}" ;;
        rename)        cmd_rename "${rest[@]:-}" ;;
        member)        set_flag IsMember "${rest[0]:-}" "${rest[1]:-}" ;;
        admin)         set_flag IsAdmin  "${rest[0]:-}" "${rest[1]:-}" ;;
        mod)           set_flag IsMod    "${rest[0]:-}" "${rest[1]:-}" ;;
        maxchars)      cmd_maxchars "${rest[0]:-}" "${rest[1]:-}" ;;
        lock)          set_until LockedUntil "locked" "${rest[0]:-}" "${rest[1]:-}" ;;
        mute)          set_until MutedUntil  "muted"  "${rest[0]:-}" "${rest[1]:-}" ;;
        logout)        cmd_logout "${rest[@]:-}" ;;
        delete|remove) cmd_delete "${rest[@]:-}" ;;
        sync-members)  cmd_sync_members ;;
        menu)          menu ;;
        *)             usage; die "Unknown command: $command" ;;
    esac
}

main "$@"
