# LGS Impact Project — Timesheet & Delivery Log

**Developer:** Anand Singh  
**Generated:** 2026-05-04  
**Period:** 2026-04-07 to 2026-05-04 (updated 2026-05-04)  

---

## Delivered Work — Full Bullet List

### Frontend (React 19 + Vite + TypeScript)
> Already built prior to this engagement (Apr 7–9 commits). Delivered as-is and deployed.

- `Layout.tsx`, `AuthContext.tsx`, `api.ts`, `Dashboard.tsx`, `StudentsList.tsx`, `StudentProfile.tsx`, `DataIngestion.tsx`
- Recharts charts, Google Maps integration

**Frontend subtotal: pre-existing (not billed)**

---

### Backend (.NET 8 Web API)
- Initialized .NET 8 Web API project — `LgsImpact.Api.csproj`, `Program.cs`, `appsettings.json`
- `AuthController.cs` — POST `/api/auth/login`, BCrypt password verification, JWT token generation, audit logging on login/failure
- `TokenService.cs` — JWT generation with claims (adminId, email, name), configurable expiry via `Jwt:ExpiryHours`
- `StudentsController.cs` — GET list (paginated, search, filter), GET by ID, PATCH update, DELETE (soft delete), all with audit logging
- `AssessmentsController.cs` — GET assessments by student ID
- `AuditController.cs` — GET paginated audit log
- `ExportController.cs` — Excel export via EPPlus (102 lines)
- `AiController.cs` — POST AI summary via Ollama, PII redaction before prompt (76 lines)
- `UploadController.cs` — CSV/Excel ingestion pipeline for student demographics and assessments (245 lines)
- `CosmosDbService.cs` — full Cosmos DB service: students, assessments, admins, AI summaries, audit logs, upload/export logs, container provisioning, admin seeding (349 lines)
- `PiiRedactionService.cs` — tokenizes student identity, strips PII before AI prompts; `RedactRawFields()` redacts 30+ PII column names from raw ingestion data before Cosmos write
- `PiiTelemetryInitializer.cs` — App Insights telemetry scrubber, strips student IDs and emails from all telemetry (71 lines)
- `AuditService.cs` — structured audit logging to Cosmos DB `audit-logs` container
- `BlobStorageService.cs` — Azure Blob Storage upload/download via managed identity
- `OllamaService.cs` — HTTP client for Ollama LLM API
- `CosmosDocuments.cs` — full data model: StudentDocument, AssessmentDocument, AdminDocument, AuditLogDocument, AiSummaryDocument (225 lines)
- Swagger UI added with JWT Bearer auth support — accessible at `/swagger`
- Health check endpoint at `/health`
- Root endpoint at `/` returning service info
- `Middleware/PiiAuditMiddleware.cs` — auto-audit logs all 2xx requests to `/api/students/`, `/api/assessments/`, `/api/export`, `/api/ai/`
- HSTS (365 days, IncludeSubDomains) + security headers middleware: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`
- `ExportController.cs` — added 2-row export watermark: confidentiality banner + admin name/email/timestamp/record count
- `AuditController.cs` — restricted to `superAdmin` claim only; non-super-admins receive 403
- `AdminDocument` — added `IsSuperAdmin` field; `TokenService` includes `superAdmin` JWT claim

**Backend subtotal: ~23.0h**

---

### Azure Infrastructure (Bicep IaC)
- `infra/main.bicep` — provisions: App Service Plan (Linux B1), Backend App Service (.NET 8, managed identity, health check, CORS, Key Vault refs, App Insights), Staging deployment slot, Frontend Static Web App, Storage Account (private `uploads` container, TLS 1.2), Log Analytics Workspace, App Insights, Key Vault access policy for managed identity
- `infra/cosmos.bicep` — Cosmos DB account (serverless), `lgs-impact` database, 7 containers: admins, students, assessments, ai-summaries, upload-logs, export-logs, audit-logs
- `infra/params.json` — dev parameters
- `infra/params.prod.json` — production parameters with full Key Vault resource ID
- `infra/modules/` — 7 reusable Bicep modules: app-service, app-service-plan, blob-storage, app-insights, key-vault, log-analytics, azure-sql

**Infrastructure subtotal: ~6.0h**

---

### CI/CD Pipeline (GitHub Actions)
- `.github/workflows/deploy.yml` — dual-job pipeline:
  - Backend job: checkout → .NET 8 restore → build Release → publish linux-x64 → deploy to `api-lgsi-dev` via publish profile
  - Frontend job: checkout → Node 20 install → Vite build (VITE_API_URL injected) → deploy to Azure Static Web Apps
- Fixed publish profile credential error (missing `azure/login` for slot swap)
- Fixed package path (`backend/publish` vs `./publish` working-directory conflict)
- Fixed Static Web App deployment size error (`app_location: 'dist'` instead of `/`)
- Targeted dev resources: `api-lgsi-dev`, `web-lgsi-dev`, `rg-lgs-sna-mvp-dev`

**CI/CD subtotal: ~3.0h**

---

### Deployment & Configuration
- Deployed backend to Azure App Service `api-lgsi-dev` — verified live at `https://api-lgsi-dev.azurewebsites.net`
- Deployed frontend to Azure Static Web Apps `web-lgsi-dev` — live at `https://salmon-forest-0e3899510.7.azurestaticapps.net`
- Configured `AllowedOrigins` in App Service to allow frontend Static Web App URL
- Resolved CORS error blocking login
- Verified end-to-end login flow with seeded admin credentials
- Confirmed Key Vault secret references (green checkmarks) for `CosmosKey`, `JwtSecret`, `StorageConnectionString`

**Deployment subtotal: ~2.0h**

---

### Documentation & Planning
- PRD progress tracker — 48 tasks, 200h estimated, status/notes for all tasks (`docs/PRD-progress.md`)
- Resolved 4 open architecture decisions: Cosmos DB confirmed, custom domain dropped, Ollama confirmed, Key Vault priority set
- `docs/pii-inventory.md` — full PII field classification (3 tiers), data flow map, compliance status per field
- Removed `.claude/` folder from repo; updated `.gitignore` to exclude entire folder

**Documentation subtotal: ~3.0h**

---

## Hours Summary

| Area | Hours |
|------|-------|
| Frontend (React) | pre-existing |
| Backend (.NET 8 API) | 23.0h |
| Azure Infrastructure (Bicep) | 6.0h |
| CI/CD Pipeline | 3.0h |
| Deployment & Configuration | 2.0h |
| Documentation & Planning | 3.0h |
| **Total** | **37.0h** |

---

## PRD Tasks Completed

| Task # | Description | Est. | Status |
|--------|-------------|------|--------|
| 1 | Static Web App deployed (`web-lgsi-dev`) | 3h | ⚠️ Partial — no custom domain |
| 2 | App Service backend deployed (`api-lgsi-dev`), managed identity, health check, staging slot | 3h | ✅ Done |
| 4 | Blob Storage — private `uploads` container, TLS 1.2, HTTPS | 2h | ✅ Done |
| 5 | Key Vault — secrets stored, managed identity access policy | 3h | ✅ Done |
| 6 | App Insights + Log Analytics — PII telemetry scrubber | 2h | ✅ Done |
| 7 | CI/CD Pipeline — GitHub Actions backend + frontend | 3h | ⚠️ Partial — no manual approval gate |
| 8 | PII field inventory — 3-tier classification, data flow map | 2h | ✅ Done |
| 9 | HSTS + security headers (X-Content-Type-Options, X-Frame-Options, Referrer-Policy) | 1h | ✅ Done |
| 10 | Export watermark — confidentiality banner + admin identity row | 1h | ✅ Done |
| 11 | PII redaction before AI calls; rawFields redaction at ingestion; PiiAuditMiddleware | 10h | ✅ Done |
| 12 | App Insights PII scrubber | 2h | ✅ Done |
| 13 | Audit log restricted to superAdmin; IsSuperAdmin field + JWT claim | 1h | ✅ Done |
| 15 | JWT auth — login, BCrypt, token generation | 5h | ✅ Done |
| 36 | Exclude WIDA data | 0.5h | ✅ Done |

**PRD estimated hours delivered: ~37.0h**
