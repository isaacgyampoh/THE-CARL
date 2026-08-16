from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


class OfflineStore:
    def __init__(self, database_path: str = ":memory:") -> None:
        self.database_path = database_path
        self.connection = sqlite3.connect(self.database_path)
        self.connection.row_factory = sqlite3.Row
        self._initialize_schema()

    def _initialize_schema(self) -> None:
        self.connection.execute(
            """
            CREATE TABLE IF NOT EXISTS devices (
                device_id TEXT PRIMARY KEY,
                business_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                role TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'ACTIVE',
                revoked INTEGER NOT NULL DEFAULT 0,
                registered_at TEXT NOT NULL
            )
            """
        )
        self.connection.execute(
            """
            CREATE TABLE IF NOT EXISTS transactions (
                event_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                business_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                source TEXT NOT NULL,
                network TEXT NOT NULL,
                event_type TEXT NOT NULL,
                amount TEXT NOT NULL,
                currency TEXT NOT NULL,
                provider_reference TEXT NOT NULL,
                parser_version TEXT NOT NULL DEFAULT 'v1',
                fingerprint TEXT NOT NULL,
                confidence REAL NOT NULL DEFAULT 1.0,
                customer_identifier_if_present TEXT,
                created_at TEXT NOT NULL
            )
            """
        )
        self.connection.execute(
            """
            CREATE TABLE IF NOT EXISTS outbox (
                outbox_id TEXT PRIMARY KEY,
                event_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                business_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                payload_version TEXT NOT NULL DEFAULT 'v1',
                created_at TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'PENDING',
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT
            )
            """
        )
        self.connection.execute(
            """
            CREATE TABLE IF NOT EXISTS sync_attempts (
                sync_id TEXT PRIMARY KEY,
                event_id TEXT NOT NULL,
                status TEXT NOT NULL,
                attempted_at TEXT NOT NULL,
                message TEXT
            )
            """
        )
        self.connection.commit()

    def upsert_device(
        self,
        device_id: str,
        business_id: str,
        branch_id: str,
        role: str,
        status: str = "ACTIVE",
        revoked: bool = False,
    ) -> None:
        self.connection.execute(
            """
            INSERT INTO devices (device_id, business_id, branch_id, role, status, revoked, registered_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(device_id)
            DO UPDATE SET
                business_id = excluded.business_id,
                branch_id = excluded.branch_id,
                role = excluded.role,
                status = excluded.status,
                revoked = excluded.revoked,
                registered_at = excluded.registered_at
            """,
            (
                device_id,
                business_id,
                branch_id,
                role,
                status,
                int(bool(revoked)),
                utc_now_iso(),
            ),
        )
        self.connection.commit()

    def save_event(self, event: Dict[str, Any]) -> None:
        self.connection.execute(
            """
            INSERT OR REPLACE INTO transactions (
                event_id, device_id, business_id, branch_id, sequence, occurred_at, source, network,
                event_type, amount, currency, provider_reference, parser_version, fingerprint, confidence,
                customer_identifier_if_present, created_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                event["event_id"],
                event["device_id"],
                event["business_id"],
                event["branch_id"],
                int(event["sequence"]),
                event["occurred_at"],
                event["source"],
                event["network"],
                event["event_type"],
                event["amount"],
                event["currency"],
                event["provider_reference"],
                event.get("parser_version", "v1"),
                event["fingerprint"],
                float(event.get("confidence", 1.0)),
                event.get("customer_identifier_if_present"),
                utc_now_iso(),
            ),
        )
        self.connection.commit()

    def enqueue_outbox(self, event: Dict[str, Any], *, outbox_id: Optional[str] = None) -> str:
        outbox_key = outbox_id or f"outbox-{event['event_id']}"
        self.connection.execute(
            """
            INSERT OR REPLACE INTO outbox (
                outbox_id, event_id, device_id, business_id, branch_id, sequence, payload_version,
                created_at, status, attempt_count, last_error
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                outbox_key,
                event["event_id"],
                event["device_id"],
                event["business_id"],
                event["branch_id"],
                int(event["sequence"]),
                "v1",
                utc_now_iso(),
                "PENDING",
                0,
                None,
            ),
        )
        self.connection.commit()
        return outbox_key

    def list_pending_outbox(self) -> List[Dict[str, Any]]:
        rows = self.connection.execute(
            "SELECT * FROM outbox WHERE status IN ('PENDING','RETRY') ORDER BY created_at ASC"
        ).fetchall()
        return [dict(row) for row in rows]

    def mark_outbox_acked(self, event_id: str, *, status: str = "ACKED") -> None:
        self.connection.execute(
            "UPDATE outbox SET status = ?, attempt_count = attempt_count + 1 WHERE event_id = ?",
            (status, event_id),
        )
        self.connection.commit()

    def log_sync_attempt(self, event_id: str, status: str, message: Optional[str] = None) -> None:
        sync_id = f"sync-{event_id}-{utc_now_iso()}"
        self.connection.execute(
            "INSERT INTO sync_attempts (sync_id, event_id, status, attempted_at, message) VALUES (?, ?, ?, ?, ?)",
            (sync_id, event_id, status, utc_now_iso(), message),
        )
        self.connection.commit()

    def recover_pending(self) -> List[Dict[str, Any]]:
        return self.list_pending_outbox()

    def close(self) -> None:
        self.connection.close()
