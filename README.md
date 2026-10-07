# AcxiomCRM

A role-based CRM web application for a sales organisation: customers, leads, opportunities, follow-ups, activities,
a sales pipeline dashboard, reports, a REST API and a tamper-proof audit log. Built with ASP.NET Core MVC, ASP.NET Core
Identity, Entity Framework Core and SQL Server.

I used Ananya Rao(demo name) - AS my login details. 

![Manager dashboard](docs/screenshots/01-dashboard.png)

## Features

### Accounts and security

- Register, log in, log out and change your password
- Strong passwords: 8+ characters with upper and lower case letters, a number and a symbol
- Accounts lock for 15 minutes after 5 failed logins
- Three roles: Admins see everything, Managers see their team's records, Sales Executives see their own
- Admins manage users and roles, and can reset passwords
- Audit log of logins, security changes and every create, update and delete

### Sales

- Customers: add, edit, view, deactivate and search, with no duplicate email or phone
- Leads: New → Contacted → Qualified → Converted (or Unqualified / Lost)
- Convert a qualified lead into a customer and, optionally, an opportunity
- Opportunities: Qualification → Proposal → Negotiation → Won / Lost, with amount and win probability
- Sales pipeline board grouped by stage
- Follow-ups for customers, leads and opportunities, with reminders for overdue and upcoming ones
- Activities: log calls, meetings, emails and tasks
- Search and filters on every list page

### Dashboard and reports

- Dashboard with key numbers (customers, leads, opportunities, pipeline value), date filters and charts
- Eight reports, including pipeline, sales and audit, with filters, sorting and paging

### API and validation

- REST API secured with JWT for customers, leads, opportunities and follow-ups (see [docs/API.md](docs/API.md))
- Every form is checked in the browser and again on the server
- Business rules such as: amount above 0, probability 0–100, and no past close or follow-up dates
## How it works

### Request flow

```mermaid
flowchart LR
    A["User or API client"] --> B["Login"]
    B --> C["Role check"]
    C --> D["Pages and API"]
    D --> E["Business rules"]
    E --> F[("Database")]
    E --> G[("Audit log")]
```

### CRM workflow

```mermaid
flowchart LR
    A["New lead"] --> B["Contacted"]
    B --> C["Qualified"]
    C --> D["Customer"]
    D --> E["Opportunity"]
    E --> F{"Won or Lost"}
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
