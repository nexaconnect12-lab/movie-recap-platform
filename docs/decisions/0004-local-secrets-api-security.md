# ADR 0004 — Local secrets and provisional API access
Status: accepted, 2026-10-12.
Fail startup without injected database connection and API key. API management endpoints compare a shared key with constant-time digest comparison, appropriate for loopback development. Windows local helper persists generated SecureStrings with current-user DPAPI, never hardcoded credentials. Service uses a restricted administrative registry key for environment injection; production needs a protected secret provider and stronger user authorization. No OAuth token storage is implemented.

