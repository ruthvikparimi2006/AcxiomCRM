# AcxiomCRM

A role-based CRM web application for a sales organisation: customers, leads, opportunities, follow-ups, activities,
a sales pipeline dashboard, reports, a REST API and a tamper-proof audit log. Built with ASP.NET Core MVC, ASP.NET Core
Identity, Entity Framework Core and SQL Server.

![Manager dashboard](docs/screenshots/01-dashboard.png)

## Features

| Area | What it does |
|---|---|
| Authentication | Register, login, logout and password change with ASP.NET Core Identity. Password policy (8+ characters, upper, lower, digit, symbol), account lockout (5 failures → 15 minutes), admin-issued one-time password-reset links. |
| Roles and scope | Admin (everything), Manager (own + team), SalesExecutive (own/assigned only). Enforced on the server for every page, record and API call. |
| Dashboard | KPI cards (customers, leads, open leads, opportunities, open/won/lost, pipeline value), date filters, and Chart.js charts for lead status, opportunity pipeline and monthly sales. |
| Customers | Create, edit, details, deactivate and search. Unique email and phone, change history and related activities. |
| Leads | Status workflow (New → Contacted → Qualified → Converted, or Unqualified/Lost) and conversion of qualified leads into a customer and an optional opportunity, in one transaction. |
| Opportunities | Stages Qualification → Proposal → Negotiation → Won/Lost, amount, probability, weighted pipeline and a sales pipeline board. |
| Follow-ups | Schedule, complete, mark missed, cancel and reschedule follow-ups against customers, leads or opportunities, with overdue and upcoming reminders in the header bell. |
| Activities | Calls, meetings, emails and tasks linked to customers and leads. |
| Reports | Customer, lead, follow-up, opportunity, pipeline, sales/conversion, user activity and audit reports with filters, sorting and paging. |
| Audit log | Logins, failures, lockouts, password and role changes, and every create/update/delete/workflow event. Append-only, enforced by a database trigger. |
| REST API | JWT-secured endpoints for customers, leads, opportunities, follow-ups and the pipeline report. See [docs/API.md](docs/API.md). |
| Validation | Client-side (unobtrusive) and server-side validation with business rules, e.g. opportunity amount > 0, probability 0–100, no past close or follow-up dates. |

## How it works

### Request flow

Every page and API call goes through the same authentication, authorization and business services, so the web UI and
the REST API always apply identical rules, scope and auditing.

```mermaid
flowchart LR
    Browser["Browser<br/>Razor pages"] -->|"Identity cookie<br/>+ anti-forgery token"| Auth
    Client["API client"] -->|"JWT bearer token<br/>(5 logins/min per IP)"| Auth

    subgraph App["AcxiomCRM (ASP.NET Core)"]
        Auth["Authentication<br/>active users only, lockout"] --> Roles["Role policies<br/>Admin / Manager / SalesExecutive"]
        Roles --> Controllers["MVC and API controllers<br/>client + server validation"]
        Controllers --> Services["Business services<br/>rules, workflows"]
        Services --> Scope["ScopeService<br/>own / team / all records"]
        Services --> Audit["AuditService<br/>no secrets stored"]
    end

    Scope --> DB[("SQL Server<br/>EF Core")]
    Services --> DB
    Audit --> AuditTable[("AuditLogs<br/>append-only trigger")]
```

### CRM workflow

```mermaid
flowchart TD
    A["Create lead<br/>status: New"] --> B["Assign to a Sales Executive"]
    B --> C["Contact lead<br/>status: Contacted"]
    C --> Q{"Qualified?"}
    Q -->|"No"| X["Unqualified / Lost"]
    Q -->|"Yes"| D["Convert lead"]

    D --> E{"Customer with same<br/>email or phone?"}
    E -->|"Yes"| F["Link existing customer"]
    E -->|"No"| G["Create customer"]
    F --> H["Create opportunity<br/>(optional)"]
    G --> H

    H --> I["Qualification → Proposal → Negotiation<br/>amount, probability, weighted pipeline"]
    I --> J{"Outcome"}
    J -->|"Won"| K["Won: counted in monthly sales"]
    J -->|"Lost"| L["Lost"]

    B -.->|"schedule"| FU["Follow-up<br/>date not before today"]
    G -.->|"schedule"| FU
    I -.->|"schedule"| FU
    FU --> R{"Planned"}
    R -->|"Complete / Missed / Cancel"| S["Closed; related record updated"]
    R -->|"Reschedule"| FU
    R -.->|"overdue or due within 7 days"| BELL["Reminder bell and Pending page"]

    D -.->|"every step"| AUD[("Audit log")]
    I -.-> AUD
    S -.-> AUD
```

## Screenshots

| | |
|---|---|
| ![Customers](docs/screenshots/02-customers.png) | ![Leads](docs/screenshots/03-leads.png) |
| Customers with search and filters | Leads across every status |
| ![Sales pipeline](docs/screenshots/04-opportunity-pipeline.png) | ![Pending follow-ups](docs/screenshots/05-project-highlight.png) |
| Sales pipeline with weighted amounts | Overdue and upcoming follow-up reminders |

All screenshots use synthetic demo data.

## Roles

| Module | Admin | Manager | SalesExecutive |
|---|---|---|---|
| Dashboard, customers, leads, opportunities, follow-ups, activities | All records | Own and team records | Own/assigned records |
| Users | Full administration | View team (read-only) | No access |
| Roles and permissions | Full | No access | No access |
| Audit log | Full | No access (configurable: team, read-only) | No access |
| Reports | All | Team | Own |
| REST API | All records | Team records | Own records |

A team is a Manager plus the SalesExecutives whose manager is set to them (set by an Admin on the user page).
Self-registered users become active SalesExecutives.

## Tech stack

.NET 10, ASP.NET Core MVC with Razor views, ASP.NET Core Identity, Entity Framework Core with SQL Server, JWT bearer
authentication for the API, Bootstrap 5, jQuery unobtrusive validation and Chart.js 4. Tests use xUnit with
WebApplicationFactory against a real SQL Server database.

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server (Express, LocalDB or full). The examples use `.\SQLEXPRESS` with Windows authentication.

### 1. Restore tools and packages

```bash
git clone https://github.com/ruthvikparimi2006/AcxiomCRM.git
cd AcxiomCRM
dotnet tool restore        # installs dotnet-ef from dotnet-tools.json
dotnet restore
```

### 2. Configure secrets with user-secrets

Secrets are never stored in the repository. For local development, store them with
[user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets):

```bash
cd src/AcxiomCRM
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=.\SQLEXPRESS;Database=AcxiomCRM;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
dotnet user-secrets set "Jwt:Key" "<a random string of at least 32 characters>"
dotnet user-secrets set "Seed:AdminEmail" "admin@example.com"
dotnet user-secrets set "Seed:AdminPassword" "<a password meeting the policy, e.g. Admin#Pass123>"
cd ../..
```

| Setting | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server database (required) |
| `Jwt:Key` | Signs REST API tokens; at least 32 characters (required) |
| `Seed:AdminEmail`, `Seed:AdminPassword` | Creates the first Admin on startup if no Admin exists |

### 3. Create the database

```bash
dotnet ef database update --project src/AcxiomCRM
```

This applies all EF Core migrations, including the trigger that makes the audit log append-only.

### 4. Run

```bash
dotnet run --project src/AcxiomCRM
```

Open the HTTPS URL shown in the console (trust the development certificate once with `dotnet dev-certs https --trust`),
then sign in with the seeded Admin. From **Users** you can create Managers and SalesExecutives, assign each
SalesExecutive to a Manager, and give new users a one-time link to set their password.

## Testing

```bash
dotnet test
```

The suite (310 tests) runs the real application against a throwaway SQL Server database named `AcxiomCRM_Tests`, which
it creates and deletes automatically. It covers authentication, authorization and scope, validation (including crafted
requests), every module, the four CRM workflows, the audit log, the REST API, dashboard and reports, and production
settings. To use a different server, set the `ACXIOMCRM_TEST_DB` environment variable to a connection string.

How each requirement is verified is recorded in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).

## REST API

Authenticate with `POST /api/auth/login` and send the returned token as `Authorization: Bearer <token>`. Tokens last
60 minutes, and login is limited to 5 attempts per minute per IP address. Endpoints, request rules and error formats
are documented in [docs/API.md](docs/API.md).

```bash
curl -k -X POST https://localhost:<port>/api/auth/login -H "Content-Type: application/json" \
     -d '{"login":"admin@example.com","password":"<password>"}'
curl -k https://localhost:<port>/api/customers -H "Authorization: Bearer <token>"
```

## Project structure

```text
src/AcxiomCRM/
  Controllers/        MVC controllers; Api/ holds the REST API controllers
  Data/               EF Core DbContext, seeding and migrations
  Dtos/               API request/response models
  Models/             Entities, enums, roles and policies
  Services/           Business rules, scope, audit, login and token services
  ViewComponents/     Header notifications (follow-up reminders)
  ViewModels/         Form and page models, validation rules
  Views/              Razor views
  wwwroot/            CSS, JavaScript and client libraries
tests/AcxiomCRM.Tests/  Automated tests
docs/                 API reference, acceptance record, deployment guide, screenshots
```

## Deployment

Production configuration (environment variables, HTTPS/HSTS, reverse proxy, data-protection keys, least-privilege
database login and an idempotent migration script) is described in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).
