# CodexQuotaShare Privacy

## Never uploaded

OpenAI credentials, ChatGPT cookies, access/refresh tokens, auth.json, session JSONL content, prompts, responses, source code and user file contents are never sent to the Relay or retained in diagnostic logs.

## Synchronization data

After explicit Create Group / Join Group, the configured service receives server-generated device/group identifiers, device display names, limits, official weekly quota percentage, plan label, reset and observation timestamps, aggregated activity counters, active-session counts, protocol versions, sequences and HMAC authentication metadata. Only data required for sharing and allocating quota is included.

The current candidate has no public endpoint. A future release may include the maintainer's public HTTPS Relay; advanced settings allow a custom HTTPS service. Pairing is required before synchronization starts. No OpenAI credentials are requested by this app.

The Relay stores the current epoch and ledger, devices, secrets, bounded replay state, at most five minutes of pending attribution counters and at most 16 necessary notifications. It does not store conversation contents or a minute-by-minute usage history. Authentication establishes device identity, not independent proof of the reported OpenAI quota.

## Local storage and Windows features

The application data/ directory contains settings, DPAPI-protected device identity, official quota cache, Relay cache, bounded pending aggregate reports, activity cursor/counter state and optional fixed-code diagnostics. Cursor state includes local session paths and byte positions; those paths are not uploaded. Partial conversation lines remain in the original session file, not a separate content cache.

When enabled through Settings, launch at startup writes the current user's Windows Run entry. Production startup registers Windows notifications; the operating system controls notification visibility. Demo and smoke modes do not register notifications, read actual account/session data, change startup settings or enforce process restrictions.

## Enforcement boundary

Soft enforcement is enabled by default and configurable. When a paired device reaches its limit, the app blocks its own launch button and attempts to close known Codex Desktop/CLI executables in the same Windows session. It matches exact installation paths and exempts its quota-observation process. Active tasks may be interrupted. It reads process identifiers, executable paths and window handles, not conversation contents or process command-line secrets. Unknown installation layouts may require explicit CLI selection.

An administrator or a user who stops/opts out of the tool can bypass these controls. Enhanced firewall enforcement is not enabled; no firewall rules are installed.

## Updates and licenses

A user-triggered update check accesses the configured repository's GitHub Releases API. Updates are not installed silently. Third-party notices and license material accompany the portable package.
