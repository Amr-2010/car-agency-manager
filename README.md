# Car Agency Manager — .NET v0.4 (source)

## Separate applications
- `src/CarAgency.PublicWeb` — anonymous Blazor public car catalog; only approved + published + available vehicles returned by `/api/public/vehicles`.
- `src/CarAgency.AdminWeb` — distinct Blazor administration application with server-side cookie sign-in, manager-only access.
- `src/CarAgency.Api` — JWT-protected tenant-scoped private endpoints, publication and approval endpoints requiring AgencyAdmin or SuperAdmin, and anonymous sanitized catalog.
- `src/CarAgency.Maui` — existing Android/iOS/Windows source scaffold; API integration is not complete.

## Run (requires .NET 10 SDK + PostgreSQL)
Set `DATABASE_CONNECTION_STRING` and strong `JWT_KEY` (32+ bytes) for API. Create database with EF migrations (`dotnet ef migrations add Initial --project src/CarAgency.Api`, then `dotnet ef database update --project src/CarAgency.Api`). If upgrading an existing v0.3 database instead, add columns using `database/002_publication.sql` and reconcile EF migrations. Configure `ApiBaseUrl` for both web apps.

Run API, public website, admin website separately with `dotnet run --project <path> --urls http://localhost:<port>`; use HTTPS for admin cookies in real deployment. For local development configure HTTPS URLs/certificates and reverse proxy; never disable secure cookies in production. Users must be securely provisioned with hashed passwords; no default credentials.

## Limitations
Source has not been compiled or integration-tested in this environment (.NET SDK unavailable). Admin currently provides a protected shell and login, not full connected CRUD. MAUI is still a scaffold and does not yet use API authorization. No APK, EXE, or IPA binary is included. Do not deploy as production-ready: add CSRF protections for login/logout, rate limiting, admin session revocation, audit logs, robust tenant authorization across every future endpoint, migrations, tests, and HTTPS configuration. Do not expose database credentials to clients.


## Render deployment (v0.5)
The repository root now contains `Dockerfile`, resolving Render's `open Dockerfile: no such file or directory` error. This default image deploys ONLY `CarAgency.Api`.

1. Upload ALL contents of this ZIP to the ROOT of the GitHub repository (`main`), not inside an extra enclosing folder.
2. Render API service: Dockerfile Path `./Dockerfile`, Docker Context `.`, Root Directory blank, port `8080`.
3. Configure secret environment variables `DATABASE_CONNECTION_STRING` (PostgreSQL connection string) and `JWT_KEY` (random 32+ byte secret). Do not commit these secrets.
4. Provision the database schema and accounts before expecting catalog data or login. The API has no automatic seed or initial super admin.
5. To deploy the public catalog as a separate Render web service, use `./Dockerfile.PublicWeb` and set `ApiBaseUrl` to the public API URL.
6. To deploy the admin as a separate Render web service, use `./Dockerfile.AdminWeb` and set `ApiBaseUrl` to the public API URL. HTTPS is required for secure cookies.
7. MAUI is a SOURCE scaffold, not compiled APK/EXE/IPA. Mobile builds require appropriate workloads and signing; iOS requires Apple tooling.

IMPORTANT: Existing source is still a prototype and has NOT been compiled or tested here. In particular, the admin login/logout POST handlers disable antiforgery; address this and other security limitations before production. A successful Docker build alone does not mean the application is production-ready.
