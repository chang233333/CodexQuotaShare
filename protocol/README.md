# Relay protocol v1

Production requires HTTPS and rejects browser Origin headers. Local development additionally requires LOCAL_DEV=true, loopback and X-CQS-Local-Test: 1. Production /internal and test-inspection routes are not exposed.

## HTTP

| Route | Behavior |
| --- | --- |
| POST /v1/groups | protocolVersion, displayName; returns group/device identity, secret, short-lived invite and snapshot |
| POST /v1/groups/join | protocolVersion, displayName, joinCode; maximum four devices |
| GET /v1/groups/:id | Signed complete snapshot |
| GET /v1/groups/:id/ws?deviceId=:deviceId | Signed WebSocket upgrade; complete snapshot immediately |
| POST /v1/groups/:id/invite/rotate | Owner only, protocolVersion |
| POST /v1/groups/:id/owner | Owner only, protocolVersion + target deviceId |
| PATCH /v1/groups/:id/devices/:deviceId | Owner only, protocolVersion + displayName or limitPercent (null or integer 1–100) |
| DELETE /v1/groups/:id/devices/:deviceId | Owner removes member, or member leaves itself; Owner must transfer first |

Protected HTTP requests carry X-CQS-Device-Id, X-CQS-Timestamp, X-CQS-Nonce, X-CQS-Sequence and X-CQS-Signature. HMAC-SHA256 covers method, exact path+query, timestamp, nonce, monotonic auth sequence and canonical JSON body. C# and TypeScript share golden vectors. JSON bodies/frames are limited to 16 KiB; strict field allowlists reject contents and client-written ledger fields. Errors contain fixed codes only.

## WebSocket

All JSON messages include protocolVersion=1 and type. Requests carry an auth envelope containing deviceId, timestamp, nonce, sequence, signature.

- REQUEST_SNAPSHOT: complete current state.
- HEARTBEAT: lastSeen update, unchanged state returns HEARTBEAT_ACK. The shipped client sends one per minute.
- ACTIVITY_UPDATE: separate activity sequence plus windowStart, windowEnd, tokenDelta, activeSessionCount. Maximum one accepted report/device/5 seconds; duplicate or overlapping windows rejected.
- QUOTA_OBSERVATION: separate quota sequence plus weeklyUsedPercent, weeklyResetAt, planType and optional observedAt for older v1 compatibility. New clients always send observedAt; observations older than five minutes or far in the future are rejected. Receipt time is used only for legacy reports.

The server sends GROUP_SNAPSHOT after committed public changes. Full state contains devices and limits, officialQuota, weeklyEpoch, usageLedger, bounded notifications and the legacy lastNotification field. REPORT_ACK includes kind and sequence after a report's transaction completes. Pending aggregate reports are removed only after acknowledgement (or matching activity watermark on reconnect). HMAC auth sequence and business sequence protect different replay boundaries.

During an established connection only a strictly greater version replaces state. On reconnect the first authoritative full snapshot replaces an untrusted local cache even if the cache version was inflated. Server-side ledger, limits and epoch survive runtime restarts. Owner availability is irrelevant to monitoring, attribution and reset.

Reset requires corroboration and server-time checks, described in relay/README.md. Event notifications are bounded and deduplicated by eventId. Legacy v1 development clients lacking the new acknowledgement/outbox behavior should be upgraded together with the Relay; this is an unpublished candidate, not a production compatibility guarantee.
