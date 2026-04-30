@"
# CONFIG SECURITY PERSISTENCE TRUTH

- appsettings.json no longer contains C:\Users\ceide paths.
- Runtime options expand environment variables and normalize full paths.
- Public /api/devices returns DeviceSummaryDto without Password, PasswordCiphertext, or ssid_pwd.
- Internal SQLite payload remains a deferred migration-risk item; public/API/diagnostic surfaces are redacted.

Validation: Release build/test PASS.
