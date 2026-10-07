# AcxiomCRM — Deployment Guide

Host-neutral: the app is a standard ASP.NET Core (.NET 10) web app with a SQL Server database. It runs the same way
on IIS, a Linux/Windows service, a container, or a PaaS, directly or behind a reverse proxy / load balancer.

## 1. Build

```bash
dotnet publish src/AcxiomCRM -c Release -o publish
```

Run with `ASPNETCORE_ENVIRONMENT=Production` (the default when unset). Production loads `appsettings.Production.json`:
warnings-only logging, no developer error pages, HSTS on.

## 2. Secrets (never in source control or appsettings files)

Provide these as environment variables or through the host's secret store. The `__` (double underscore) form works everywhere.

| Setting | Environment variable | Required | Notes |
|---|---|---|---|
| Database connection | `ConnectionStrings__DefaultConnection` | Yes | The app will not start without it. Use a login that can read/write data only (see §4). |
| JWT signing key | `Jwt__Key` | Yes | Random, at least 32 characters (e.g. 48+ random bytes, base64). The app will not start without a strong key. Changing it signs out all API clients. |
| First Admin email | `Seed__AdminEmail` | First start | Creates the first Admin only if no Admin exists. |
| First Admin password | `Seed__AdminPassword` | First start | Must meet the password policy. Remove both seed settings after the first start. |

Example key: `openssl rand -base64 48`.

## 3. HTTPS

HTTPS is mandatory in production (AUTH-13, API-09):

- Plain HTTP requests are redirected to HTTPS, and browsers are told to stay on HTTPS (HSTS, 365 days, include sub-domains).
- All cookies (login, anti-forgery, messages) are `Secure` and `HttpOnly`.
- The app must know the public HTTPS port to redirect. This is automatic when Kestrel/IIS has an HTTPS binding; otherwise set
  `ASPNETCORE_HTTPS_PORT` (usually `443`). Behind a TLS-terminating proxy, the proxy's `X-Forwarded-Proto: https` is used instead (see §5).
- Provide a valid TLS certificate via the host (IIS binding, Kestrel certificate settings, or the proxy).

## 4. Database

1. Create an empty SQL Server database.
2. Apply the schema with the idempotent migration script, run by a deployment account that may change the schema:

   ```bash
   dotnet ef migrations script --idempotent --project src/AcxiomCRM -o deploy/migrations.sql
   # then run deploy/migrations.sql against the database (sqlcmd, SSMS, or your pipeline)
   ```

   The script is safe to run repeatedly; it applies only missing migrations. It also creates the trigger that makes the
   audit log append-only.
3. Give the application's own login only `db_datareader` and `db_datawriter` on this database (least privilege). It never needs
   to change the schema, and without `ALTER` rights it cannot disable the audit-log trigger.

## 5. Reverse proxy / load balancer

If requests reach the app through a proxy, list the proxy addresses so the real client IP and scheme are used. This matters for
the 5-per-minute API login limit (otherwise every client would share the proxy's IP), the audit log IP addresses, and HTTPS detection:

```text
ForwardedHeaders__KnownProxies__0=10.0.0.5
ForwardedHeaders__KnownProxies__1=10.0.0.6
```

With none listed, only a proxy on the same machine (loopback, e.g. IIS or nginx on localhost) is trusted. Headers from any other
address are ignored, so clients cannot spoof their IP.

## 6. Data-protection keys

Login cookies, anti-forgery tokens and password-reset links are protected by keys the app generates. Store them persistently,
or every restart or new instance signs everyone out and voids open reset links:

```text
DataProtection__KeysPath=/var/acxiomcrm/keys        (Linux/container: a persistent volume)
DataProtection__KeysPath=D:\AcxiomCRM\keys           (Windows)
```

With several instances, point all of them at the same shared location. Restrict access to this folder to the app account.

## 7. Optional settings

| Setting | Default | Meaning |
|---|---|---|
| `Manager__CanViewTeamUsers` | `true` | Managers can view (not edit) their team's users |
| `Manager__CanViewAuditLog` | `false` | Managers can view their team's audit entries |
| `AllowedHosts` | `*` | Set to the public host name(s), e.g. `crm.example.com` |

## 8. Go-live checklist (§14)

- [ ] `ASPNETCORE_ENVIRONMENT=Production` (or unset).
- [ ] `ConnectionStrings__DefaultConnection` and `Jwt__Key` set from the secret store; nothing secret in files.
- [ ] Database created and `deploy/migrations.sql` applied; the app login has read/write only.
- [ ] HTTPS certificate in place; HTTP redirects to HTTPS; `ASPNETCORE_HTTPS_PORT` set if no HTTPS binding.
- [ ] Proxy addresses listed in `ForwardedHeaders__KnownProxies` (if behind a proxy).
- [ ] `DataProtection__KeysPath` points to persistent (shared) storage.
- [ ] First start done with `Seed__AdminEmail` / `Seed__AdminPassword`; then those two settings removed.
- [ ] Sign in as the Admin, create Managers and Sales Executives, and set each Sales Executive's manager.
