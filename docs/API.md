# AcxiomCRM REST API

Base path: `/api`. All requests and responses are JSON. HTTPS only in deployed environments.

## Authentication

The API uses **JWT bearer tokens** (the web pages use a separate cookie that the API never accepts).

1. `POST /api/auth/login` with your username or email and password.
2. Send the returned token on every other call: `Authorization: Bearer <token>`.
3. A token is valid for **60 minutes**. It stops working earlier if the account is deactivated, its role changes or its password is reset; log in again in that case.

Login is limited to **5 attempts per minute per IP address**; further attempts get `429 Too Many Requests` with `Retry-After: 60`. Failed logins also count towards account lockout (5 failures lock the account for 15 minutes), exactly as on the web.

## Access and scope

Every endpoint except login needs a token for an Admin, Manager or SalesExecutive.

| Role | Records returned and editable |
|---|---|
| Admin | All records |
| Manager | Own records and those of their team (users whose manager they are) |
| SalesExecutive | Own / assigned records only |

A record outside your scope behaves as if it does not exist (`404`). Assigning a record to another user follows the same rule as the web: Admins can assign to any active user, Managers to themselves or their team, SalesExecutives only to themselves. Every create, update and delete is written to the audit log.

## Endpoints

| Method | Path | Purpose | Success |
|---|---|---|---|
| POST | `/api/auth/login` | Get a token (public, rate limited) | 200 |
| POST | `/api/auth/logout` | Record logout; discard the token afterwards | 200 |
| GET | `/api/customers` | List/search customers | 200 |
| POST | `/api/customers` | Create a customer | 201 + `Location` |
| GET | `/api/customers/{id}` | Get one customer | 200 |
| PUT | `/api/customers/{id}` | Update a customer (all fields) | 200 |
| DELETE | `/api/customers/{id}` | Deactivate a customer (status becomes `Inactive`) | 200 |
| GET | `/api/leads` | List/search leads | 200 |
| POST | `/api/leads` | Create a lead | 201 |
| GET | `/api/opportunities` | List/search opportunities | 200 |
| POST | `/api/opportunities` | Create an opportunity | 201 |
| GET | `/api/followups` | List follow-ups | 200 |
| POST | `/api/followups` | Schedule a follow-up | 201 |
| GET | `/api/reports/pipeline` | Pipeline totals per stage (own / team / all by role) | 200 |

### Query parameters for lists

All lists take `page` (default 1) and `pageSize` (default 20, maximum 100).

| Endpoint | Filters |
|---|---|
| `/api/customers` | `search` (name, email, phone or company), `status` (`Active`, `Inactive`) |
| `/api/leads` | `search` (name or company), `status`, `assignedTo` (user id) |
| `/api/opportunities` | `search` (name), `customer` (customer name), `stage`, `status` |
| `/api/followups` | `from`, `to` (`yyyy-MM-dd`), `status` |

List response:

```json
{ "items": [ ... ], "page": 1, "pageSize": 20, "totalCount": 42 }
```

## Request models

Enum values are sent and returned as names, for example `"Qualification"`; numbers are not accepted for them. Dates are `yyyy-MM-dd`.

**Login**: `{ "login": "user@example.com", "password": "..." }`. Response: `{ "token", "expiresAt", "user": { "id", "name", "role" } }`.

**Customer** (POST, PUT)

| Field | Rules |
|---|---|
| `customerName` | required, max 150 |
| `email` | required, valid email, max 256, unique (case-insensitive) |
| `phone` | required, 10-digit mobile number starting with 6–9, unique |
| `companyName` | max 150 |
| `address` | max 250 |
| `city`, `state` | max 100 |
| `status` | `Active` or `Inactive` (default `Active`) |
| `notes` | max 2000 |
| `ownerId` | optional user id; defaults to you |

**Lead** (POST)

| Field | Rules |
|---|---|
| `leadName` | required, max 150 |
| `email`, `phone` | optional; same formats as customers |
| `companyName` | max 150 |
| `source` | `Website`, `Referral`, `Campaign`, `ColdCall`, `Event`, `Other` |
| `status` | must be `New` for a new lead |
| `priority` | `Low`, `Medium`, `High` |
| `expectedValue` | 0 to 100,000,000; at most 2 decimals |
| `notes` | max 2000 |
| `assignedTo` | optional user id; defaults to you |

**Opportunity** (POST)

| Field | Rules |
|---|---|
| `opportunityName` | required, max 150 |
| `customerId` | required, a customer you can see |
| `leadId` | optional, a lead you can see |
| `amount` | required; greater than 0; at most 2 decimals |
| `stage` | `Qualification`, `Proposal`, `Negotiation`, `Won`, `Lost` |
| `probability` | required, 0 to 100 |
| `expectedCloseDate` | required; not in the past while the stage is open |
| `source` | as for leads |
| `notes` | max 2000 |
| `assignedTo` | optional user id |

`status` (`Open`, `Won`, `Lost`) follows the stage and cannot be set. Responses include `weightedAmount` = amount × probability / 100.

**Follow-up** (POST)

| Field | Rules |
|---|---|
| `customerId` / `leadId` / `opportunityId` | exactly one, and one you can see |
| `followUpDate` | required; not earlier than today |
| `followUpType` | `Call`, `Meeting`, `Email`, `Task` |
| `subject` | required, max 200 |
| `remarks` | max 2000 |
| `assignedTo` | optional user id |

New follow-ups are always `Planned`.

**Pipeline report** response:

```json
{
  "scope": "team",
  "stages": [ { "stage": "Qualification", "count": 3, "amount": 120000.00, "weightedAmount": 30000.00 }, ... ],
  "openCount": 7, "openAmount": 450000.00, "openWeightedAmount": 160000.00
}
```

`scope` is `own` for SalesExecutives, `team` for Managers and `all` for Admins.

## Errors

Every error is an [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807) problem object (`Content-Type: application/problem+json`) without stack traces or database details. Validation errors list messages per field:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "Amount": [ "Opportunity Amount must be greater than 0." ] },
  "traceId": "..."
}
```

| Status | When |
|---|---|
| 400 | Invalid or malformed payload, or a business rule failed |
| 401 | Missing, invalid or expired token; failed login |
| 403 | Signed in, but the role may not use this endpoint |
| 404 | Unknown endpoint or record, or a record outside your scope |
| 405 | Unknown path called with a method other than GET (see note) |
| 409 | Conflicts with an existing record (duplicate customer email or phone) |
| 429 | Too many login attempts from this IP address |

Note: an unknown path called with POST, PUT or DELETE answers 405 instead of 404, because of a GET-only static-file fallback route that the framework registers. Nothing is executed either way.
