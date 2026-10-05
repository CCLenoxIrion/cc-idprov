# CLAUDE.md – IT-Onboarding-Service (CleanControlling)

Verbindliche Spezifikation: [`docs/SPEC.md`](docs/SPEC.md). Getroffene Entscheidungen und
bewusste Abweichungen von der Spec: [`docs/DECISIONS.md`](docs/DECISIONS.md). Bei Widerspruch
gilt DECISIONS.md, sonst SPEC.md.

Umfang v1: **nur Onboarding**. Offboarding (SPEC §8) ist Entwurf und wird nicht umgesetzt,
Datenmodell und Status-Logik sind aber so gebaut, dass es als zweiter `RequestType` ohne
Umbau dazukommt.

## Harte Regeln (nicht verhandelbar)

- Führe **NIEMALS** Befehle gegen AD, Exchange Online, Graph, Teams, SMB oder NETLOGON aus.
  Kein `Connect-*`, kein `Invoke-Command`. Echte Systeme fasst nur ein Mensch an.
- Bis einschließlich Phase 3 laufen alle Steps über **Fake-Executors**. Ab Phase 4 führt Claude
  weder pwsh noch Pester aus und baut keine Verbindungen auf; getestet wird lokal vom Menschen.
- Keine Werte aus der Spec hart codieren, die in SPEC §4 als Konfiguration stehen
  (Domäne, Mail-Muster, Präfixe, OUs, Server, Pfade, Policies, …). Sie stehen nur in
  Seed-Daten bzw. in der DB-Konfiguration; Tests bauen ihre Konfiguration selbst.
- Passwörter (und Tokens) **nie** loggen, nie in Exceptions, nie in Step-Output, nie im
  Audit-Log (SPEC §9). Typen, die ein Passwort halten, überschreiben `ToString()`.
- Bei Unklarheiten in der Spec: **fragen, nicht raten**. Fragen gebündelt sammeln.
- Nach jeder Phase stoppen und auf OK warten.

## Phasen

1. Core + Data (Domänenmodell, Statusmaschinen, Ableitungen §2, Logon-Skript §4.4,
   Checklisten §4.5, EF Core/SQLite)
2. Web (Blazor Server, Admin-Konfiguration, Auftragsformular, Freigabe, Übersicht, Details)
3. Worker (Polling, Abhängigkeiten, Backoff/Timeout, Retry, Fakes, Integrationstest)
4. PowerShell-Step-Skripte unter `/scripts` + Real-Executor (Mensch testet gegen Test-OU)

## Struktur

| Projekt | Inhalt |
|---|---|
| `src/Onboarding.Core` | Domänenmodell, Statusmaschinen, Namens-/Mail-Ableitung, Logon-Skript-Generator, Checklisten-Logik. **Keine I/O, keine Paketreferenzen.** Externe Abhängigkeiten nur als Interfaces (`ICollisionChecker`, `ISecretEncryptor`, …). Zeit nur über `TimeProvider`. |
| `src/Onboarding.Data` | EF Core + SQLite, `OnboardingDbContext`, Interceptors (Audit append-only, Config-Historie, Concurrency-Token), Migrations, Seed. |
| `src/Onboarding.Web` | Blazor Server: Auth (`Auth/`), Use-Case-Services (`Services/`), Seiten (`Components/Pages`). Services holen I/O-Daten (Konfig-Snapshot, Kollisionsprüfung, Verschlüsselung) und delegieren Entscheidungen an `RequestWorkflow`. |
| `src/Onboarding.Worker` | Worker Service / Windows-Dienst: `WorkerEngine` (Claim mit Concurrency-Token, ein Running-Step pro Auftrag, Aufträge parallel), Polling-Loop, Single-Instance-Lock. Entscheidungen über Status/Backoff/Timeout/Audit trifft `RequestWorkflow` (Core). |
| `src/Onboarding.Steps` | `IStepExecutor` je Step-Key (`Execution/`), simulierte Fake-Welt mit Executors (`Fakes/World/`), Fake-Verzeichnis für das Web, Startpasswort-Verschlüsselung, Skript-Executor für DryRun/Real (`Scripts/`: pwsh, JSON über stdin/stdout, Timeout mit Kill), LDAP-Lese-Adapter für das Web (`Ldap/`). |
| `tests/Onboarding.Tests` | xUnit. |
| `scripts/steps`, `scripts/common` | PowerShell-7-Step-Skripte, je Step ein Skript, JSON über stdin/stdout, Secrets nur über stdin (DECISIONS X3). |
| `scripts/jea` | JEA-Endpunkte `CC.Onboarding` (DC01) und `CC.Onboarding.Sync` (CC01): Module (PS 5.1), Role Capabilities, Registrierung, manueller Test (X5). |
| `scripts/tests` | Pester-5-Tests mit gemockten Cmdlets; `Stubs.ps1` wirft bei vergessenem Mock. |
| `docs/DEPLOYMENT.md` | Checkliste für Rechte, JEA-Registrierung, Dienste und Testreihenfolge. |

## Build / Test

```bash
dotnet tool restore                   # dotnet-ef (lokales Tool, dotnet-tools.json)
dotnet build Onboarding.slnx          # Warnings sind Fehler
dotnet test Onboarding.slnx
# Migration anlegen / Modell-Drift prüfen
dotnet ef migrations add <Name> -p src/Onboarding.Data -s src/Onboarding.Data -o Migrations
dotnet ef migrations has-pending-model-changes -p src/Onboarding.Data -s src/Onboarding.Data
```

Web lokal starten (Dev-Anmeldung, Fakes, SQLite-Datei `onboarding.db` im Projektordner):

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Onboarding.Web --launch-profile http
# http://localhost:5202 → Dev-Anmeldung mit Benutzern aus appsettings.Development.json
```

Worker lokal dazu starten (nutzt in Development dieselbe DB und denselben Dev-Schlüssel wie das Web):

```bash
cd src/Onboarding.Worker && DOTNET_ENVIRONMENT=Development dotnet run
```

Für schnelle lokale Läufe im Admin-UI Polling-Intervall (min. 5 s) und Backoff verkürzen.
Fehlerinjektion und Verzögerungen der Fake-Welt: Abschnitt `FakeWorld` in
`src/Onboarding.Worker/appsettings.json`.

Konfiguration Web (`appsettings*.json`): `Authentication:Mode` (`EntraId` | `Dev`, Dev nur in
Development), `AzureAd` (TenantId/ClientId), `Integrations:Read:Directory` (`Fake` | `Real` = LDAP),
`Integrations:Read:Licenses` (bis 4b nur `Fake`), `SecretProtection:Mode` (`Certificate` | `DevelopmentPem`).

Konfiguration Worker: `Integrations:Steps:OnPrem` / `:Cloud` (`Fake` | `DryRun` | `Real`, Cloud bis 4b
nur `Fake`), `Integrations:Scripts` (PwshPath, ScriptsDirectory, Timeout, JEA-Endpunktnamen).

PowerShell-Skripte werden von Claude **nicht** ausgeführt (kein pwsh, kein Pester). Der Mensch
testet lokal:

```powershell
Invoke-Pester ./scripts/tests -Output Detailed   # Pester >= 5.5
```

Hinweis Cloud-Umgebung: `builds.dotnet.microsoft.com` ist gesperrt; das .NET-10-SDK kommt
dort per `apt-get install dotnet-sdk-10.0` (Ubuntu-Paket).

## Konventionen

- .NET 10, C# latest, Nullable an, `TreatWarningsAsErrors`, file-scoped namespaces,
  Central Package Management (`Directory.Packages.props`, keine Versionen in csproj).
- Code, Bezeichner und Code-Kommentare auf Englisch; UI-Texte und Doku auf Deutsch.
- Zeitzone für fachliche Zeitpunkte (Eintrittsdatum, `AD.Enable`) ist **Europe/Berlin**
  aus der Konfiguration, nie Server-Lokalzeit. In der DB werden Zeitpunkte als UTC
  (`DateTimeOffset`) gespeichert.
- Enums in der DB als String.
- Ableitungen liefern Ergebnisobjekte mit `NeedsInput`-Gründen statt Exceptions; Exceptions
  nur für Programmier-/Konfigurationsfehler (z. B. unbekannter Platzhalter).
- Fachliche Statusübergänge ausschließlich über `RequestStateMachine` / `StepStateMachine`.
- `AuditEntry` ist append-only (vom DbContext erzwungen); jede schreibende Aktion erzeugt einen.
- Neue Step-Keys nur in `StepKeys` + Step-Plan; Offboarding-Steps (§8) erst in v2.
