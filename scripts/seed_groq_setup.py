"""Seed ApplicationSettings + Groq SearchQuery for local Groq-only flow."""
from __future__ import annotations

import os
import sys

try:
    import psycopg
except ImportError:
    print("Installing psycopg...")
    import subprocess

    subprocess.check_call([sys.executable, "-m", "pip", "install", "psycopg[binary]", "-q"])
    import psycopg


def main() -> int:
    conn_str = os.environ.get("DATABASE_URL") or os.environ.get("ConnectionStrings__DefaultConnection")
    if not conn_str:
        print("Set DATABASE_URL or ConnectionStrings__DefaultConnection first.")
        return 1

    # Convert ADO.NET-style to libpq if needed
    if "Host=" in conn_str:
        parts = {}
        for chunk in conn_str.split(";"):
            if "=" not in chunk:
                continue
            k, v = chunk.split("=", 1)
            parts[k.strip().lower()] = v.strip()
        conn_str = (
            f"host={parts.get('host')} dbname={parts.get('database')} "
            f"user={parts.get('username')} password={parts.get('password')} "
            f"sslmode=require"
        )

    settings = [
        ("AllowScraper", "true", "Enable scraper bot"),
        ("AllowVerifier", "true", "Enable verifier bot"),
        ("ScraperContinuousMode", "false", "Exit after one scraper cycle"),
        ("VerifierContinuousMode", "false", "Exit after one verifier cycle"),
        ("UseProxy", "false", "Disable proxy usage"),
    ]

    with psycopg.connect(conn_str) as conn:
        with conn.cursor() as cur:
            for key, value, description in settings:
                cur.execute(
                    """
                    INSERT INTO "ApplicationSettings" ("Key", "Value", "Description")
                    VALUES (%s, %s, %s)
                    ON CONFLICT ("Key") DO UPDATE
                    SET "Value" = EXCLUDED."Value",
                        "Description" = EXCLUDED."Description"
                    """,
                    (key, value, description),
                )

            cur.execute('UPDATE "SearchQueries" SET "IsEnabled" = false')
            cur.execute(
                """
                INSERT INTO "SearchQueries" ("Query", "IsEnabled", "SearchResultsCount", "LastSearchUTC")
                SELECT 'gsk_', true, 0, TIMESTAMPTZ '1970-01-01 00:00:00+00'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "SearchQueries" WHERE "Query" = 'gsk_'
                )
                """
            )
            cur.execute(
                """
                UPDATE "SearchQueries"
                SET "IsEnabled" = true, "LastSearchUTC" = TIMESTAMPTZ '1970-01-01 00:00:00+00'
                WHERE "Query" = 'gsk_'
                """
            )

            cur.execute('SELECT COUNT(*) FROM "SearchProviderTokens" WHERE "IsEnabled" = true')
            enabled_tokens = cur.fetchone()[0]

            cur.execute('SELECT "Key", "Value" FROM "ApplicationSettings" ORDER BY "Key"')
            print("ApplicationSettings:")
            for row in cur.fetchall():
                print(f"  {row[0]} = {row[1]}")

            cur.execute(
                'SELECT "Id", "Query", "IsEnabled" FROM "SearchQueries" ORDER BY "Id"'
            )
            print("SearchQueries:")
            for row in cur.fetchall():
                print(f"  id={row[0]} query={row[1]!r} enabled={row[2]}")

            print(f"Enabled SearchProviderTokens: {enabled_tokens}")
            if enabled_tokens == 0:
                print(
                    "\nMISSING: add a GitHub PAT to SearchProviderTokens before running the scraper."
                )

        conn.commit()

    print("\nGroq seed complete.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
