# SERVICE API TRUTH

- LocalApiBaseUrl default remains http://127.0.0.1:5317.
- Desktop can override via BOSSCAM_LOCAL_API_BASE_URL.
- Desktop verifies /api/health before API workflows.
- Swagger is Development or BossCam:EnableSwagger=true only.
- CORS is loopback-only by default.
- Non-loopback service bind requires BossCam:AllowLanApi=true.
- Dangerous maintenance operations require loopback and explicit confirmation.

Validation: Release build PASS.
