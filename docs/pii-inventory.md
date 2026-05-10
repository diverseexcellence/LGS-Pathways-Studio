# LGS Impact — PII Field Inventory & Data Classification

**Task:** PRD Task #8  
**Created:** 2026-05-04  
**Status:** Complete  

---

## Sensitivity Tiers

| Tier | Definition | Handling |
|------|-----------|---------|
| **Tier 1 — Highly Sensitive** | Directly identifies a student; combined with other fields could enable harm | Redact from AI prompts, redact from logs, watermark on export, restrict API access to authenticated admins only |
| **Tier 2 — Sensitive** | Sensitive demographic or program data; protected under FERPA | Redact from AI prompts, audit all access, watermark on export |
| **Tier 3 — Operational** | Internal system data; low risk on its own but should not be public | Audit access, no public exposure |

---

## Student PII Fields (`StudentDocument`)

| Field | Description | Tier | AI Prompt | Logs | Export |
|-------|-------------|------|-----------|------|--------|
| `studentId` | Student Tracking Number (STN) | **Tier 1** | Redact | Redact | Watermark |
| `fullName` | Student full name | **Tier 1** | Redact | Redact | Watermark |
| `dob` | Date of birth | **Tier 1** | Redact | Redact | Watermark |
| `gender` | Gender | **Tier 2** | Redact | Mask | Watermark |
| `ethnicity` | Ethnicity code | **Tier 2** | Redact | Mask | Watermark |
| `ellStatus` | English Learner status | **Tier 2** | Redact | Mask | Watermark |
| `spedStatus` | Special Education status | **Tier 2** | Redact | Mask | Watermark |
| `section504` | 504 plan status | **Tier 2** | Redact | Mask | Watermark |
| `homeRoom` | Homeroom teacher assignment | **Tier 3** | OK | OK | OK |
| `classGroup` | Class group / cohort | **Tier 3** | OK | OK | OK |
| `grade` | Grade level | **Tier 3** | OK | OK | OK |
| `tier` | Intervention tier (1/2/3) | **Tier 3** | OK | OK | OK |
| `tierStatus` | Tier workflow status | **Tier 3** | OK | OK | OK |
| `enrolDate` | Enrollment date | **Tier 3** | OK | OK | OK |

---

## Assessment PII Fields (`AssessmentDocument`)

| Field | Description | Tier | AI Prompt | Logs | Export |
|-------|-------------|------|-----------|------|--------|
| `studentId` | Links assessment to student (STN) | **Tier 1** | Redact | Redact | Watermark |
| `score` | Individual assessment score | **Tier 2** | Redact | Mask | Watermark |
| `proficiency` | Proficiency level | **Tier 2** | Redact | Mask | Watermark |
| `rawFields` | Raw CSV fields from upload (may contain PII) | **Tier 2** | Redact | Redact | Watermark |
| `uploadType` | Assessment type (IXL, i-Ready, etc.) | **Tier 3** | OK | OK | OK |
| `subject` | Subject area | **Tier 3** | OK | OK | OK |
| `period` | Assessment period | **Tier 3** | OK | OK | OK |
| `date` | Assessment date | **Tier 3** | OK | OK | OK |

---

## AI Summary PII Fields (`AiSummaryDocument`)

| Field | Description | Tier | Notes |
|-------|-------------|------|-------|
| `studentId` | Links summary to student | **Tier 1** | Never expose in logs |
| `summaryText` | AI-generated text about student | **Tier 2** | PII must be redacted before generation; output may still contain inferred sensitive content |

---

## Admin PII Fields (`AdminDocument`)

| Field | Description | Tier | Notes |
|-------|-------------|------|-------|
| `email` | Admin email address | **Tier 2** | Mask in telemetry (already handled by `PiiTelemetryInitializer`) |
| `passwordHash` | BCrypt hashed password | **Tier 1** | Never expose in API responses or logs |
| `lastLogin` | Last login timestamp | **Tier 3** | OK |

---

## Audit Log PII Fields (`AuditLogDocument`)

| Field | Description | Tier | Notes |
|-------|-------------|------|-------|
| `adminEmail` | Email of admin who took action | **Tier 2** | Needed for audit trail; restrict read access to super-admins |
| `entityId` | Often a studentId | **Tier 1** | Needed for audit trail; restrict read access |
| `ipAddress` | Admin IP address | **Tier 2** | Needed for security audit; restrict read access |
| `details` | Free-text action description (may contain student name) | **Tier 2** | Restrict read access |

---

## Data Flow Map

```
CSV/Excel Upload
    │
    ▼
UploadController → CosmosDbService → [students] container (Tier 1+2 fields)
                                   → [assessments] container (Tier 1+2 fields)
                                   → [upload-logs] container (Tier 3 only)
                                   → Azure Blob Storage (raw file — Tier 1+2)
    │
    ▼
StudentsController → API response → React frontend (authenticated admins only)
    │
    ▼
AiController → PiiRedactionService (strips Tier 1+2) → Ollama LLM
                                                      → [ai-summaries] container
    │
    ▼
ExportController → Excel file download (Tier 1+2 — requires watermark) ← PENDING
    │
    ▼
AuditController → [audit-logs] container (Tier 1+2 in entityId/details) ← restrict access
    │
    ▼
Application Insights → PiiTelemetryInitializer scrubs Tier 1 from telemetry ✅
```

---

## Current Compliance Status

| Control | Status | Notes |
|---------|--------|-------|
| Tier 1 redaction from AI prompts | ✅ Done | `PiiRedactionService.cs` |
| Tier 1+2 scrubbed from App Insights | ✅ Done | `PiiTelemetryInitializer.cs` |
| HTTPS-only transport | ✅ Done | Enforced on App Service + Storage |
| JWT auth on all student endpoints | ✅ Done | `[Authorize]` on `StudentsController` |
| BCrypt password hashing | ✅ Done | Admin passwords never stored in plain text |
| HSTS headers on API responses | ❌ Pending | Task #9 |
| Export watermarking (Tier 1+2) | ❌ Pending | Task #14 |
| Audit middleware on all PII endpoints | ⚠️ Partial | Task #13 — manual only, not auto-wired |
| `rawFields` PII scan on ingestion | ❌ Pending | Raw CSV fields not scanned for PII |
| Audit log read access restriction | ❌ Pending | Any admin can read all audit logs |

---

## Recommended Next Actions (Priority Order)

1. **Task #13** — Wire audit middleware to auto-log all requests hitting `studentId` in path/body
2. **Task #9** — Add HSTS header middleware to `Program.cs`
3. **Task #14** — Add watermark (admin name + timestamp + "CONFIDENTIAL") to Excel exports
4. **Restrict audit log reads** — Only super-admin role should read full audit trail
5. **Scan `rawFields`** — Run PII detection on raw CSV fields at ingestion time
