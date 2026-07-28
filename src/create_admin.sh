#!/usr/bin/env bash
# Create (or promote) an admin account directly in sanctuary.db.
#
# Usage:
#   ./create_admin.sh <username> <password>
#   ./create_admin.sh <username> <password> --mod
#   ./create_admin.sh <username> <password> --db /path/to/sanctuary.db
#   ./create_admin.sh <username> <password> --force        # update if the user already exists
#   ./create_admin.sh <username> <password> --no-admin      # plain member, no admin flag
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DB_PATH="$SCRIPT_DIR/bin/Release/sanctuary.db"
HASHER_DIR="$SCRIPT_DIR/PasswordHasher"
HASHER_EXE="$HASHER_DIR/bin/Release/net8.0/PasswordHasher.exe"
HASHER_DLL="$HASHER_DIR/bin/Release/net8.0/PasswordHasher.dll"

IS_ADMIN=1
IS_MOD=0
MAX_CHARACTERS=10
FORCE=0

usage() {
    grep '^#' "${BASH_SOURCE[0]}" | grep -v '#!/' | sed 's/^# \{0,1\}//'
    exit 1
}

if [ $# -lt 2 ]; then
    usage
fi

USERNAME="$1"
PASSWORD="$2"
shift 2

while [ $# -gt 0 ]; do
    case "$1" in
        --db) DB_PATH="$2"; shift 2 ;;
        --mod) IS_MOD=1; shift ;;
        --no-admin) IS_ADMIN=0; shift ;;
        --max-characters) MAX_CHARACTERS="$2"; shift 2 ;;
        --force) FORCE=1; shift ;;
        -h|--help) usage ;;
        *) echo "Unknown argument: $1" >&2; usage ;;
    esac
done

if [ ! -f "$DB_PATH" ]; then
    echo "Database not found at $DB_PATH" >&2
    exit 1
fi

PYTHON_BIN=""
for candidate in python3 python py; do
    if command -v "$candidate" >/dev/null 2>&1; then
        PYTHON_BIN="$candidate"
        break
    fi
done
if [ -z "$PYTHON_BIN" ]; then
    echo "No python3/python interpreter found on PATH (needed to write to sanctuary.db)." >&2
    exit 1
fi

# Hash the password with the same BCrypt.Net-Next scheme the server uses to verify logins.
# PasswordHasher exits non-zero because of an unhandled Console.ReadKey() once stdin/stdout
# are redirected -- that happens after it has already printed the hash, so ignore its exit code.
if [ -f "$HASHER_EXE" ]; then
    HASHER_OUTPUT="$("$HASHER_EXE" "$PASSWORD" || true)"
elif [ -f "$HASHER_DLL" ]; then
    HASHER_OUTPUT="$(dotnet "$HASHER_DLL" "$PASSWORD" || true)"
else
    HASHER_OUTPUT="$(dotnet run --project "$HASHER_DIR" -c Release -- "$PASSWORD" || true)"
fi

PASSWORD_HASH="$(printf '%s\n' "$HASHER_OUTPUT" | sed -n 's/^BCrypt Hash: //p' | head -n1)"
if [ -z "$PASSWORD_HASH" ]; then
    echo "Failed to generate bcrypt hash via PasswordHasher:" >&2
    printf '%s\n' "$HASHER_OUTPUT" >&2
    exit 1
fi

"$PYTHON_BIN" - "$DB_PATH" "$USERNAME" "$PASSWORD_HASH" "$IS_ADMIN" "$IS_MOD" "$MAX_CHARACTERS" "$FORCE" <<'PYEOF'
import sqlite3
import sys
from datetime import datetime, timezone

db_path, username, password_hash, is_admin, is_mod, max_characters, force = sys.argv[1:8]
is_admin = int(is_admin)
is_mod = int(is_mod)
max_characters = int(max_characters)
force = int(force)

con = sqlite3.connect(db_path)
try:
    cur = con.cursor()
    cur.execute("SELECT Id FROM Users WHERE Username = ?", (username,))
    existing = cur.fetchone()

    if existing:
        if not force:
            print(f"User '{username}' already exists (Id={existing[0]}). Pass --force to update it.", file=sys.stderr)
            sys.exit(1)
        cur.execute(
            "UPDATE Users SET Password = ?, IsAdmin = ?, IsMod = ? WHERE Username = ?",
            (password_hash, is_admin, is_mod, username),
        )
        con.commit()
        print(f"Updated user '{username}' (Id={existing[0]}): IsAdmin={is_admin}, IsMod={is_mod}")
    else:
        created = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        cur.execute(
            """INSERT INTO Users (Created, IsAdmin, IsMember, IsMod, MaxCharacters, Password, Username)
               VALUES (?, ?, 1, ?, ?, ?, ?)""",
            (created, is_admin, is_mod, max_characters, password_hash, username),
        )
        con.commit()
        new_id = cur.execute("SELECT Id FROM Users WHERE Username = ?", (username,)).fetchone()[0]
        print(f"Created user '{username}' (Id={new_id}): IsAdmin={is_admin}, IsMod={is_mod}")
        if new_id != 1:
            print("Note: only the Id=1 account can grant/revoke admin via the in-game /admin add|remove commands.")
finally:
    con.close()
PYEOF
