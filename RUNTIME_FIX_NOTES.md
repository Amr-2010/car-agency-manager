# v0.7 Runtime diagnostics and configuration fix

The screenshot confirms a successful Docker build and a process exit code 139. Code 139 usually indicates a native segmentation fault; its exact root cause cannot be established from that screenshot. This release improves runtime diagnostics and corrects a definite configuration defect: empty `appsettings.json` values previously masked the environment variables `DATABASE_CONNECTION_STRING` and `JWT_KEY`.

## Render settings
- Runtime: Docker; Dockerfile Path: `./Dockerfile`; Docker Context: `.`
- Environment: `DATABASE_CONNECTION_STRING` (PostgreSQL connection string), `JWT_KEY` (random >= 32-byte secret)
- Health Check Path: `/health`
- Service port: `8080`
- Keep secrets only in Render, never in GitHub.
- Do not change other Render projects or databases.

## Troubleshooting
If the next deploy still exits 139, collect Application Logs and the platform architecture. Check for native crash / OOM details. Do not interpret a successful image build as a successful app launch.

## Important
No .NET SDK or Docker runtime was available in the authoring environment, so this package has not been compiled or run. This is a source-code remediation and diagnostic update, not a verified fix for exit 139.
