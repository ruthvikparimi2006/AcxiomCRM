# AcxiomCRM — Acceptance Record (Step 13)

Automated suite: **310 tests, all passing** (`dotnet test`, run twice). Tests run against a real SQL Server test
database and the real ASP.NET Core pipeline (WebApplicationFactory), including anti-forgery, authentication and
authorization. Test classes are in `tests/AcxiomCRM.Tests`.

Browser-only behaviour (client-side validation actually blocking a submit, Chart.js drawing) is verified by its rendered
markup here and by the manual Chrome acceptance run.

## §17.19 Final acceptance scenario

| # | Scenario | Result | Evidence |
|---|---|---|---|
| 1 | Open AcxiomCRM without authentication | Pass | `AcceptanceTests.S01…`, `ShellTests.Protected_pages_redirect_anonymous_users_to_login` |
| 2 | Register / login reaches the Dashboard | Pass | `AcceptanceTests.S02…`, `ShellTests.Login_lands_on_the_dashboard` |
| 3 | Customer with invalid email/phone: client validation | Pass | `AcceptanceTests.S03…`, `CustomerTests.Create_form_carries_client_side_validation_rules` |
| 4 | Crafted request bypassing the browser is rejected | Pass | `AcceptanceTests.S04…`, `CustomerTests.Crafted_invalid_requests_are_rejected_by_the_server`, `ValidationSweepTests` |
| 5 | Opportunity Amount <= 0 rejected | Pass | `AcceptanceTests.S05_to_S07…`, `OpportunityTests.Crafted_invalid_opportunities…`, `ApiTests.Opportunity_business_rules…` |
| 6 | Probability > 100 rejected | Pass | same |
| 7 | Expected Close Date in the past rejected (active) | Pass | same; closed deals may keep past dates (`OpportunityTests.Moving_through_stages…`) |
| 8 | Follow-up before today rejected | Pass | `AcceptanceTests.S08…`, `FollowUpTests.A_follow_up_dated_before_today_is_rejected_for_every_role` |
| 9 | SalesExecutive: only assigned scope | Pass | `AcceptanceTests.S09_to_S11…`, scope tests in every module |
| 10 | Manager: team pipeline/report access | Pass | same, `DashboardReportTests.Report_access_and_data_follow_the_role`, `ApiTests.Pipeline_report…` |
| 11 | Admin: user/role/audit administration | Pass | same, `AuthorizationTests`, `AuditLogTests` |
| 12 | Create/update/delete produces audit entries | Pass | `AcceptanceTests.S12…`, `AuditLogTests.Every_required_security_business_and_workflow_event_is_audited…` |
| 13 | `/api/customers` returns authorized JSON | Pass | `AcceptanceTests.S13…`, `ApiTests` |
| 14 | Dashboard KPI cards and Chart.js charts with authorized data | Pass | `AcceptanceTests.S14…`, `DashboardReportTests` |

## §13 Testing and acceptance checklist

| Area | Result | Evidence |
|---|---|---|
| Authentication: valid login, invalid rejected, logout | Pass | `AuthTests` (valid/invalid/unknown/inactive, logout ends session) |
| Password security: no plain text; policy enforced | Pass | `AuthTests.Register_creates_…_hashed_password`, `Register_enforces_the_password_policy`; audit scan for secrets |
| Lockout after repeated failures | Pass | `AuthTests.Five_failed_logins_lock_the_account_for_15_minutes`, `ApiTests.Api_logins_count_towards_account_lockout` |
| Authorization: no access by changing URLs/requests | Pass | `AuthorizationTests` (crafted posts), out-of-scope 404 tests in every module and the API |
| Client validation (required, email, phone, length, date, numeric) | Pass | `ValidationSweepTests.Every_form_has_client_validation…`, module `*_client_side_*` tests |
| Server validation: invalid/tampered requests rejected | Pass | `ValidationSweepTests` (length, whitespace, precision, over-posting, anti-forgery), module crafted-request tests |
| Opportunity: Amount > 0, probability 0–100, close-date rule | Pass | `OpportunityTests`, `ApiTests` |
| Follow-up: date not before today for new/planned | Pass | `FollowUpTests` (create, edit, reschedule; no role exempt) |
| Audit: security and business actions logged | Pass | `AuditLogTests` (36 required module/action pairs, fields, no secrets, DB append-only) |
| API: correct status codes and authorization | Pass | `ApiTests` (200/201/400/401/403/404/409/429) |
| Reports: filters and role-based visibility | Pass | `DashboardReportTests` |
| FUP-05 reminders: upcoming and overdue in scope | Pass | `FollowUpTests.Reminders_show_overdue_and_next_7_days…`, `Managers_get_reminders_for_their_team` |

## §8 Workflows (each run once with its role)

| Workflow | Role | Evidence |
|---|---|---|
| Lead-to-Customer: create, assign, contact, qualify, convert, audit | Manager assigns, SalesExecutive works the lead | `ConversionTests.Lead_to_customer_workflow_end_to_end` |
| Opportunity: amount, probability, close date, stages, Won, outcome | SalesExecutive | `AcceptanceTests.Workflow_opportunity_by_sales_executive` |
| Follow-Up: create, date, assign, complete/missed/reschedule, related record updated, audit | Manager for a team member | `AcceptanceTests.Workflow_follow_up_by_manager_for_a_team_member` |
| User Administration: create, role, active/inactive, password and lockout policy, audit | Admin | `AcceptanceTests.Workflow_user_administration_by_admin` |

## Scope

| Role | Result | Evidence |
|---|---|---|
| SalesExecutive: own/assigned only | Pass | out-of-scope 404 and list tests in Customer, Lead, Opportunity, FollowUp, Activity, API, Report tests |
| Manager: own + team (users whose manager they are) | Pass | `*Manager_sees_team*` tests per module, pipeline/report tests |
| Admin: everything | Pass | Admin visibility checks in Customer, Authorization, Report and API tests |

## §17.17 Minimum evaluation criteria

| Area | Result |
|---|---|
| CRUD (or deactivation) in all modules | Pass — Users (deactivate), Customers (deactivate), Leads/Opportunities/Follow-ups/Activities (soft delete) |
| Client-side and server-side validation | Pass |
| Opportunity, lead and follow-up business rules | Pass |
| Identity login/logout/register | Pass |
| Admin/Manager/SalesExecutive access differs | Pass |
| Dashboard cards and charts | Pass |
| Search/filter on required pages | Pass |
| Customer/Lead/Opportunity APIs | Pass (all §10 endpoints) |
| Important operations logged | Pass |
| Controllers, Models, Data, Services, ViewModels, Views organized | Pass (plus `Dtos`, `ViewComponents`) |
| No plain-text passwords; anti-forgery; protected endpoints | Pass |

## §14 Security checklist

| Item | Result | Evidence |
|---|---|---|
| Password hashing enabled | Pass | ASP.NET Core Identity; `AuthTests` |
| Password policy enabled | Pass | min 8, upper/lower/digit/symbol; `AuthTests`, reset-link policy in `AcceptanceTests` |
| Account lockout enabled | Pass | 5 failures → 15 minutes; web and API |
| Role-based authorization enabled | Pass | fixed role policies + `ScopeService`; `AuthorizationTests` |
| Server-side validation enabled | Pass | `ValidationSweepTests` |
| Anti-forgery on state-changing MVC forms | Pass | global filter; all 30 POST endpoints refuse a missing token |
| Sensitive information excluded from logs | Pass | audit scrubbing + full-table scan; reset tokens never audited; request URLs not logged (`Microsoft.AspNetCore: Warning`); EF sensitive-data logging off |
| HTTPS in production/deployment | Pass | HTTP→HTTPS redirect, HSTS 365 days, all cookies Secure + HttpOnly in Production (`DeploymentTests`); runbook `docs/DEPLOYMENT.md` |
| API authorization and input validation | Pass | `ApiTests` |
| Audit logging for security-sensitive actions | Pass | `AuditLogTests` |
| Least-privilege roles | Pass | permission matrix; Manager view-only user admin, no audit log by default |
| DB credentials not hard-coded | Pass | user-secrets/environment only; `AcceptanceTests.No_credentials_or_secrets_are_committed_in_configuration`; app refuses to start without its secrets (`DeploymentTests`) |
