# LGS Impact Project — PRD Progress Tracker

**Source:** Meeting Transcript (April 9, 2026)  
**Total Tasks:** 48 | **Total Estimated Hours:** 200h  
**Stack:** React 19 + Vite + TypeScript | .NET 8 Web API | Azure Cosmos DB | Azure App Service  
**Last Updated:** 2026-04-30

---

## Legend
- ✅ Done — fully implemented
- ⚠️ Partial — scaffolded or partially implemented
- ❌ Not Done — not started
- 🚫 N/A — superseded by architecture decision (SQL → Cosmos DB)

---

## 1. Azure Infrastructure (19h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 1 | Resource Group & App Service (React SPA) | React hosting, custom domain, HTTPS enforced, managed certificate | 3h | ⚠️ Partial | Static Web App `web-lgsi-dev` deployed. Custom domain and managed cert not configured. |
| 2 | App Service (.NET 8 API backend) | API hosting, managed identity enabled, health checks, deployment slots | 3h | ⚠️ Partial | `api-lgsi-dev` deployed, managed identity enabled. Health check path and staging slot not configured. |
| 3 | Azure SQL Database provisioning | TDE enabled, firewall rules (App Service IPs only), TLS 1.2 minimum | 3h | 🚫 N/A | Architecture changed to Cosmos DB (serverless). SQL module exists in `infra/modules/azure-sql.bicep` but not deployed. Confirm with stakeholders if SQL is still required. |
| 4 | Azure Blob Storage for file uploads | Private container, managed identity access, no public anonymous access | 2h | ✅ Done | `stlgsidev` deployed. Private `uploads` container, `allowBlobPublicAccess: false`, HTTPS enforced. Note: managed identity access not wired — using connection string via Key Vault. |
| 5 | Azure Key Vault integration | Store connection strings, Gemini API key, JWT key; managed identity access | 3h | ⚠️ Partial | `kv-lgs-sna-mvp-dev` created by admin. CosmosKey, JwtSecret, StorageConnectionString stored. Key Vault access policy for App Service managed identity still needs to be applied (`az keyvault set-policy`). |
| 6 | Application Insights & monitoring | Performance monitoring, PII telemetry scrubber, alerting, dashboards | 2h | ❌ Not Done | Module exists at `infra/modules/app-insights.bicep` and `log-analytics.bicep` but not wired into `main.bicep`. No alerts or dashboards configured. |
| 7 | CI/CD Pipeline (GitHub Actions) | Build React + .NET 8; deploy to App Service; environment gates | 3h | ⚠️ Partial | `.github/workflows/deploy.yml` deploys backend (linux-x64) and frontend on push to main. No staging slot, no environment gates (manual approval before prod). |

**Infra: ~8h done / ~11h remaining**

---

## 2. PII / Security (46h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 8 | PII field inventory & data classification | 12 PII fields classified into 3 sensitivity tiers; data flow mapping | 6h | ❌ Not Done | No classification document or data flow map exists. |
| 9 | Encryption at rest & in transit | Verify TDE on SQL, TLS 1.2+, HTTPS-only App Service, HSTS headers | 3h | ⚠️ Partial | HTTPS-only and TLS 1.2 enforced on App Service and Storage. No TDE (no SQL). HSTS headers not added to .NET API responses. |
| 10 | Dynamic Data Masking on Azure SQL | DDM rules on all Tier 1/2 PII columns; UNMASK for App Service identity | 10h | 🚫 N/A | SQL not used. Cosmos DB has no DDM equivalent. Needs stakeholder decision on Cosmos-level access strategy. |
| 11 | PII redaction before AI calls | .NET PiiRedactionService; tokenize student identity; strip all PII from prompts | 10h | ✅ Done | `backend/Services/PiiRedactionService.cs` implemented. |
| 12 | App Insights PII scrubber | Telemetry initializer strips PII from dependency logs | 2h | ❌ Not Done | App Insights not deployed. Telemetry initializer not written. |
| 13 | Audit logging middleware | .NET middleware logging all PII-access endpoints; user, action, timestamp, IP | 6h | ⚠️ Partial | `AuditService.cs` and `AuditController.cs` exist. Middleware not wired to all PII-access endpoints automatically. |
| 14 | Export watermarking | ClosedXML header/footer with user name, timestamp, confidentiality notice | 4h | ❌ Not Done | `ExportController.cs` exports data but no watermark, confidentiality notice, or user attribution in Excel output. |
| 15 | Basic auth (MVP — no SSO) | JWT-based login for admin users; hashed credentials in DB | 5h | ✅ Done | `AuthController.cs`, `TokenService.cs`, BCrypt hashing, JWT wired in `Program.cs`. |

**PII/Security: ~15h done / ~27h remaining** *(excl. 10h DDM — architecture decision pending)*

---

## 3. Student Profile (23h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 16 | Display name as Last Name, First Name | Replace current name format on student profile card | 1h | ❌ Not Done | Full name displayed as-is from `student.fullName`. |
| 17 | Add STN to student profile card | Show STN alongside student name in top-level info boxes | 1h | ❌ Not Done | STN not shown in profile header. |
| 18 | Add Date of Birth alongside Age | Display both DOB and runtime-calculated age | 1.5h | ⚠️ Partial | DOB displayed. Calculated age not shown. |
| 19 | Translate ethnicity codes to readable labels | Map numeric codes to ethnicity names; maintain mapping table | 4h | ❌ Not Done | Raw `student.ethnicity` code shown without translation. |
| 20 | Change EL Status from date to Yes/No | Replace date field with simple Yes/No indicator | 1h | ❌ Not Done | Raw `ellStatus` value displayed. |
| 21 | Change Special Education T/F to Yes/No | Convert boolean to human-readable Yes/No | 1h | ❌ Not Done | Not displayed on profile. |
| 22 | Change 504 Status to Yes/No (default No) | If field is empty, default to No | 1h | ❌ Not Done | Not displayed on profile. |
| 23 | Add Entry/Exit Date, Lunch Status, Homeroom | Include in visible demographic fields on profile | 2.5h | ❌ Not Done | Fields not shown on profile card. |
| 24 | Redesign profile to clean card layout | Replace raw data table with visually appealing React component; responsive | 10h | ⚠️ Partial | Has card-style UI but not the full clean redesign specified. Raw data still shown in several areas. |

**Student Profile: ~1h done / ~22h remaining**

---

## 4. Tiering Workflow (30h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 25 | Auto-process tier recommendations on ingestion | Background job triggers AI recommendation when sufficient data exists | 14h | ❌ Not Done | AI called manually only via `AiController`. No background trigger on upload. |
| 26 | Change default status to System Recommended | Update state machine; auto-transition on data ingestion | 4h | ❌ Not Done | Default tier status is `"Pending"` hardcoded in `CosmosDocuments.cs`. |
| 27 | Redefine Pending = insufficient data only | Validation logic to detect missing demographics/assessments; flag reason | 4h | ❌ Not Done | No insufficient-data detection logic. |
| 28 | Add Finalized status with audit trail | Admin confirmation; audit log with user ID, timestamp, prev/new tier | 6h | ❌ Not Done | No Finalized status. Audit trail not linked to tier changes. |
| 29 | Exclude Pending students from dashboard | Filter logic on dashboard aggregation queries | 2h | ❌ Not Done | No filter applied to dashboard queries. |

**Tiering Workflow: 0h done / ~30h remaining**

---

## 5. Assessment Data (16h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 30 | Load Checkpoint 1 data | Ingestion pipeline for CP1 alongside CP2/CP3, grades 3–6 | 3h | ❌ Not Done | Only CP2/CP3 ingestion pipeline exists. |
| 31 | Load Acadience reading data (K–2) | New data type; reading only; configure ingestion mapping | 4h | ❌ Not Done | No Acadience ingestion mapping. |
| 32 | Label Acadience as Reading subject | Subject metadata tagging on ingestion | 1h | ❌ Not Done | Depends on #31. |
| 33 | Use current I-Read file (3/11/26) | Swap data source; validate STN matching with current roster | 2.5h | ❌ Not Done | Data source not swapped. |
| 34 | Map I-Read statuses to proficiency labels | Did Not Pass → Below Proficiency; Pass → Proficient | 2h | ❌ Not Done | No status mapping logic. |
| 35 | Fix I-Read display bug showing Yes | Debug incorrect status translation in React component | 3h | ❌ Not Done | Bug not fixed. |
| 36 | Exclude WIDA data | Confirm exclusion in data type config; no dev work needed | 0.5h | ✅ Done | Confirmed no dev work needed. |

**Assessment Data: ~0.5h done / ~15.5h remaining**

---

## 6. Dashboard (26h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 37 | Replace top metrics with 4 KPI boxes | ELA Growth, Math Growth, ELA Proficiency, Math Proficiency | 6h | ⚠️ Partial | KPI section exists in `Dashboard.tsx` but not the 4 specified metrics. |
| 38 | Configurable target goal percentage | Admin setting to replace hardcoded 85% placeholder | 2h | ❌ Not Done | 85% hardcoded. No admin setting. |
| 39 | Grade > Teacher > Student drill-down | Three-level React components with breadcrumb; API endpoints per level | 18h | ❌ Not Done | No drill-down navigation implemented. |
| 40 | Retain homeroom caseload chart | No changes — approved as-is | 0h | ✅ Done | No changes needed. |
| 41 | Retain zip code census distribution | No changes — approved as-is | 0h | ✅ Done | No changes needed. |

**Dashboard: ~0h done / ~26h remaining**

---

## 7. Learning Plans (31h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 42 | Expand scope beyond RTI/MTSS only | Redesign data model; every student gets a plan; RTI added for tiered | 14h | ❌ Not Done | No learning plans feature exists. |
| 43 | Add plan name field | DB schema + React form; multiple named plans per student | 2h | ❌ Not Done | |
| 44 | Add non-academic support programs | Attendance, student support services; new program category | 5h | ❌ Not Done | |
| 45 | Add program drop-down selector | Dynamic drop-down from admin-managed list; multi-select | 4h | ❌ Not Done | |
| 46 | Complete Create Plan save functionality | Wire React form to .NET API; validation; save; confirmation UX | 6h | ❌ Not Done | |

**Learning Plans: 0h done / ~31h remaining**

---

## 8. AI Summary (6h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 47 | Refine AI prompt for collaboration notes | Update Gemini prompt; translate codes before sending; validate output | 6h | ❌ Not Done | Currently uses Ollama with a basic prompt. Gemini not integrated. No code translation before prompting. |

**AI Summary: 0h done / ~6h remaining**

---

## 9. Data Quality (3h)

| # | Task | Details | Est. | Status | Notes |
|---|------|---------|------|--------|-------|
| 48 | Compile unmatched STN list | Query to diff assessment vs demographic STNs; export for client | 3h | ❌ Not Done | No STN diff query or export built. |

**Data Quality: 0h done / ~3h remaining**

---

## Overall Progress Summary

| Area | Est. Hours | Done | Remaining |
|------|-----------|------|-----------|
| Azure Infrastructure | 19h | ~8h | ~11h |
| PII / Security | 46h | ~15h | ~27h* |
| Student Profile | 23h | ~1h | ~22h |
| Tiering Workflow | 30h | 0h | 30h |
| Assessment Data | 16h | ~0.5h | ~15.5h |
| Dashboard | 26h | ~0h | ~26h |
| Learning Plans | 31h | 0h | 31h |
| AI Summary | 6h | 0h | 6h |
| Data Quality | 3h | 0h | 3h |
| **Total** | **200h** | **~24.5h** | **~171.5h** |

*10h DDM (task #10) excluded from remaining — pending stakeholder decision on SQL vs Cosmos DB strategy.

---

## Open Decisions Required

1. **SQL vs Cosmos DB** — Plan specified Azure SQL (TDE, DDM). Project uses Cosmos DB. Confirm which is canonical for MVP. If Cosmos stays, DDM (10h) needs a replacement access-control strategy.
2. **Custom domain** — A domain name is needed before task #1 can be completed.
3. **Gemini vs Ollama** — Plan says Gemini for AI. Current backend uses Ollama (local). Confirm which AI provider for production.
4. **Key Vault access policy** — One `az keyvault set-policy` command pending to grant App Service managed identity access to Key Vault secrets.
