"""Insert or update a GitHub search token in SearchProviderTokens."""
from __future__ import annotations

import os
import sys

try:
    import psycopg
except ImportError:
    import subprocess

    subprocess.check_call([sys.executable, "-m", "pip", "install", "psycopg[binary]", "-q"])
    import psycopg


def to_libpq(conn_str: str) -> str:
    if "Host=" not in conn_str:
        return conn_str
    parts = {}
    for chunk in conn_str.split(";"):
        if "=" not in chunk:
            continue
        k, v = chunk.split("=", 1)
        parts[k.strip().lower()] = v.strip()
    return (
        f"host={parts.get('host')} dbname={parts.get('database')} "
        f"user={parts.get('username')} password={parts.get('password')} "
        f"sslmode=require"
    )


def main() -> int:
    token = (sys.argv[1] if len(sys.argv) > 1 else os.environ.get("GITHUB_TOKEN", "")).strip()
    if not token:
        print("Usage: python scripts/set_github_token.py <github_pat>")
        print("   or: $env:GITHUB_TOKEN='ghp_...'; python scripts/set_github_token.py")
        return 1

    conn_str = os.environ.get("DATABASE_URL") or os.environ.get("ConnectionStrings__DefaultConnection")
    if not conn_str:
        print("Set DATABASE_URL or ConnectionStrings__DefaultConnection first.")
        return 1

    with psycopg.connect(to_libpq(conn_str)) as conn:
        with conn.cursor() as cur:
            # SearchProviderEnum.GitHub = 1
            cur.execute(
                """
                UPDATE "SearchProviderTokens"
                SET "IsEnabled" = false
                WHERE "SearchProvider" = 1
                """
            )
            cur.execute(
                """
                INSERT INTO "SearchProviderTokens" ("Token", "SearchProvider", "IsEnabled", "LastUsedUTC")
                VALUES (%s, 1, true, NULL)
                """,
                (token,),
            )
        conn.commit()

    print("GitHub search token saved (SearchProvider=GitHub, IsEnabled=true).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
