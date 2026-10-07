# Deployment-Checkliste – On-Prem (Phase 4a) und Cloud (Phase 4b)

Checkliste für die Inbetriebnahme von Web und Worker gegen das echte AD und den echten Tenant.
Ausgeführt wird alles **von einem Menschen**; der Code selbst verbindet sich erst, wenn die
jeweilige Gruppe auf `DryRun` bzw. `Real` steht. Cloud-Teil: §11.

Punkte mit **„zu verifizieren“** sind nicht aus der Spec oder Dokumentation belegbar und müssen vor
dem Echtbetrieb an der Umgebung geprüft werden. Namen wie `svc-onboard$` sind Beispiele.

Begriffe:

| Platzhalter | Bedeutung |
|---|---|
| `<ONB-HOST>` | Server, auf dem Web und Worker laufen (DECISIONS B1: derselbe Host) |
| `CC\svc-onboard$` | gMSA des Workers (schreibt in AD, ruft die JEA-Endpunkte auf) |
| `CC\svc-onbweb$` | Dienstkonto des Web (nur lesend) |
| `CC\svc-onbjea-logon$` | Endpunkt-gMSA auf DC03 (Pflicht, Run-As-Variante b) |
| DC01 | **Fileserver (Member-Server, kein DC)** mit F:\Home und den Home-Freigaben (`GlobalConfig.Home.Server`) |
| DC03 | Domänencontroller, auf dem die Anmeldeskripte in NETLOGON geschrieben werden (`GlobalConfig.LogonScript.Server`); DCs: DC02, DC03, DC04 |
| CC01 | Entra-Connect-Server (`GlobalConfig.EntraConnectServer`) |

## 1. Rechte-Übersicht (Soll)

| Identität | Darf | Darf ausdrücklich **nicht** |
|---|---|---|
| Worker-gMSA | Benutzer in den drei Bereichs-OUs anlegen und die Onboarding-Attribute schreiben (Delegation §3), Mitgliedschaften der konfigurierten Gruppen ändern, sich an den JEA-Endpunkten `CC.Onboarding` (DC01), `CC.Onboarding.Logon` (DC03) und `CC.Onboarding.Sync` (CC01) anmelden | lokaler Admin auf DC01/DC03/CC01, Mitglied von `ADSyncOperators`, Schreibzugriff auf F:\Home, SMB oder NETLOGON |
| Run-As-Konto DC01-Endpunkt (Virtual Account) | lokaler Administrator auf dem Member-Server DC01: Home-Ordner anlegen, ACL/Besitzer setzen, SMB-Freigaben anlegen – begrenzt durch die drei sichtbaren Funktionen und die ACE-Allowlist | alles außerhalb dieser Funktionen |
| Run-As-Konto DC03-Endpunkt (gMSA `svc-onbjea-logon$`) | Schreiben/Ändern nur im Ordner `SCRIPTS` unter SYSVOL auf DC03 | jede Admin-Gruppe, jedes andere Schreibrecht |
| Run-As-Konto CC01-Endpunkt | Delta-Sync starten (`ADSyncOperators`) | alles andere |
| Web-Konto | AD lesen (LDAP) | jedes Schreiben, JEA-Endpunkte, privater Schlüssel des Passwort-Zertifikats |
| Worker-App-Registrierung (Cloud) | Graph: Benutzer lesen/`usageLocation` setzen, Lizenzen zuweisen, `subscribedSkus` lesen; EXO: Postfach-/Freigabepostfach-Rechte im Management Scope; Teams: Telefonie-Einstellungen (§11.2) | Gruppen schreiben (`GroupMember.ReadWrite.All` wird **nicht** vergeben), Verzeichnisrollen über die nötigen hinaus |
| Web-Lese-App-Registrierung (Graph) | Lizenzen und Benutzer/Gruppen/Kontakte lesen (§11.3) | jedes Schreiben |

## 2. Konten anlegen

- [ ] KDS-Root-Key vorhanden (`Get-KdsRootKey`), sonst einmalig anlegen (Replikationszeit beachten).
- [ ] Worker-gMSA:
  ```powershell
  New-ADServiceAccount -Name svc-onboard -DNSHostName svc-onboard.<domäne> `
      -PrincipalsAllowedToRetrieveManagedPassword '<ONB-HOST>$'
  # auf <ONB-HOST>:
  Install-ADServiceAccount svc-onboard
  Test-ADServiceAccount svc-onboard   # muss True liefern
  ```
- [ ] Web-Konto `svc-onbweb$` genauso (eigenes gMSA, eigener Name). Es bekommt **keine**
  Delegation und keine JEA-Rechte; Lesen über „Authenticated Users“ genügt (siehe §8).
- [ ] Beide Konten: Recht „Als Dienst anmelden“ auf `<ONB-HOST>` (bei `sc.exe`/Dienst-Installation
  setzt Windows es in der Regel automatisch – **zu verifizieren**).
- [ ] Worker-gMSA ist **nicht** in Domain Admins, Account Operators, lokalen Administratoren oder
  `ADSyncOperators` (`Get-ADPrincipalGroupMembership svc-onboard$`).

## 3. AD-Delegation für den Worker (dsacls)

Für **jede** der drei Bereichs-OUs (`AreaConfig.OuDistinguishedName`, siehe Admin-UI):

```cmd
set OU=OU=...,DC=...,DC=...
set W=CC\svc-onboard$

rem Benutzerobjekte anlegen (nur Klasse user, nur in dieser OU und darunter)
dsacls "%OU%" /I:T /G "%W%:CC;user"

rem Attribute auf Benutzerobjekten schreiben (Anlage + Drift-Korrektur, DECISIONS X8)
for %A in (givenName sn displayName department company mail userPrincipalName proxyAddresses telephoneNumber scriptPath manager extensionAttribute1 extensionAttribute15 userAccountControl pwdLastSet) do dsacls "%OU%" /I:S /G "%W%:WP;%A;user"

rem Startpasswort bei der Anlage setzen
dsacls "%OU%" /I:S /G "%W%:CA;Reset Password;user"
```

- [ ] `extensionAttribute1` nur, wenn es das konfigurierte Titel-Attribut ist
  (`GlobalConfig.DoctorTitle.Attribute`); `extensionAttribute15` = `GlobalConfig.RequestIdAttribute`.
  Sind andere Attribute konfiguriert, diese stattdessen delegieren.
- [ ] **Zu verifizieren:** ob `New-ADUser` mit den Anlage-Attributen (inkl. `sAMAccountName`,
  `cn`/`name`, `unicodePwd`) allein mit „Create Child“ durchgeht oder zusätzliche Rechte braucht.
  Test in der Test-OU mit `OnPrem = Real` (§11).
- [ ] **Zu verifizieren:** `pwdLastSet` (für „Kennwort bei nächster Anmeldung ändern“) und
  `userAccountControl` (Aktivieren in `AD.Enable`) – evtl. reicht der Property-Set
  „Account Restrictions“ (`dsacls ... /G "%W%:WS;Account Restrictions;user"`).
- [ ] Gruppenmitgliedschaften: auf **jeder** in Abteilungen konfigurierten Gruppe
  ```cmd
  dsacls "CN=<Gruppe>,OU=...,DC=..." /G "CC\svc-onboard$:WP;member"
  ```
  Kommen neue Gruppen in der Konfiguration hinzu, muss die Delegation nachgezogen werden
  (sonst `AD.Groups` → `failed` mit „Zugriff verweigert“).
- [ ] Kein Löschrecht (`DC`) und keine Rechte außerhalb der Bereichs-OUs.
- [ ] Vor dem ersten Schreiben: `extensionAttribute15` ist bei keinem Objekt belegt (DECISIONS X2):
  `Get-ADObject -LDAPFilter '(extensionAttribute15=*)'` liefert nichts.

## 4. JEA-Endpunkte (DECISIONS X5)

Dateien: `scripts/jea/`. Die Endpunkte kennen alle Pfade selbst; der Worker übergibt nur die sam,
beim Home-Ordner zusätzlich das Benutzerrecht und die zusätzlichen Rechte (nur Werte aus der
Allowlist, §4.1), beim Anmeldeskript Base64-Inhalt + SHA-256.

| Endpunkt | Server | Modul | sichtbare Funktionen | Run-As |
|---|---|---|---|---|
| `CC.Onboarding` | DC01 (Fileserver, Member-Server) | `CCOnboarding` | `New-OnbHomeFolder`, `New-OnbHomeShare` | Virtual Account (lokaler Admin) |
| `CC.Onboarding.Logon` | DC03 (Domänencontroller) | `CCOnboardingLogon` | `Set-OnbLogonScript` | eigenes gMSA `svc-onbjea-logon$`, **kein** Virtual Account |
| `CC.Onboarding.Sync` | CC01 (Entra Connect) | `CCOnboardingSync` | `Start-OnbDeltaSync` | Virtual Account mit Gruppe `ADSyncOperators` |

### 4.1 Endpunkt-Konfiguration anpassen

- [ ] **DC01** – `scripts/jea/CCOnboarding/OnboardingEndpoint.psd1`; die Werte müssen zur
  Global-Konfiguration im Admin-UI passen:

  | Schlüssel | muss passen zu |
  |---|---|
  | `HomeRoot` | `GlobalConfig.Home.LocalRoot` |
  | `ShareNamePattern` | `GlobalConfig.Home.ShareNamePattern` |
  | `NetbiosDomain` | `GlobalConfig.DomainNetBios` |
  | `ShareFullAccess` | Vollzugriff auf Home-Freigaben neben dem Benutzer (Änderungsrecht). Bewusst strenger als der Bestand (Jeder = Vollzugriff), DECISIONS X18 |
  | `HomeOwner` | Besitzer jedes Home-Ordners (Vererbung aus). **Zu verifizieren**: auf deutschem Windows heißt die Gruppe `VORDEFINIERT\Administratoren`; alternativ die SID `S-1-5-32-544` eintragen |
  | `HomeUserRights` | erlaubte Werte für `GlobalConfig.Home.UserRight` (`Modify`, `FullControl`) |
  | `HomeAceAllowlist` | **jeder** Principal aus `GlobalConfig.Home.AdditionalAces` und aus `HomeAdditionalAces` aller Abteilungen, jeweils mit den erlaubten Rechten |

  Die Allowlist ist die Sicherheitsgrenze (DECISIONS X17): Der Endpunkt lehnt jeden Principal und
  jedes Recht ab, das dort nicht steht (`ace-not-allowed`). Ein kompromittierter Worker kann also
  keinem beliebigen Konto Vollzugriff geben. Neue Principals im Admin-UI ⇒ hier ergänzen und das
  Modul neu registrieren (§4.3). Principals werden wie im Admin-UI geschrieben (Groß-/Kleinschreibung
  egal); SIDs sind ebenfalls erlaubt.
- [ ] **DC03** – `scripts/jea/CCOnboardingLogon/OnboardingLogonEndpoint.psd1`:
  `LogonScriptDirectory = C:\Windows\SYSVOL\sysvol\CC.local\SCRIPTS` (verifiziert, DFSR-Status
  „Eliminated“), `LogonFileNamePattern` passend zu `GlobalConfig.LogonScript.FileNamePattern`.
- [ ] Admin-UI: `Home.Server = DC01`, `LogonScript.Server = DC03`.

### 4.2 Run-As-Konten

| Server | Run-As | Begründung |
|---|---|---|
| DC01 (Member-Server) | Virtual Account (ohne `-RunAsGroup` lokaler Administrator) | Anlegen von SMB-Freigaben und Setzen des Besitzers braucht lokale Admin-Rechte; auf einem Member-Server ist das unkritisch. Eingeschränkt durch die zwei sichtbaren Funktionen und die Allowlist. |
| DC03 (DC) | eigenes gMSA `svc-onbjea-logon$` | Ein Virtual Account wäre auf einem DC Domain Admin. Das gMSA bekommt nur Schreiben/Ändern auf `SCRIPTS` – keine Admin-Gruppe. `Register-OnboardingJea.ps1 -Role Logon` lehnt `-RunAs VirtualAccount` ab. |
| CC01 | Virtual Account mit `-RunAsGroup ADSyncOperators` | nur Delta-Sync. |

- [ ] gMSA für DC03 anlegen: `New-ADServiceAccount svc-onbjea-logon -DNSHostName … -PrincipalsAllowedToRetrieveManagedPassword 'DC03$'`,
  auf DC03 `Install-ADServiceAccount svc-onbjea-logon`.
- [ ] Rechte nur auf den Skriptordner:
  ```cmd
  icacls "C:\Windows\SYSVOL\sysvol\CC.local\SCRIPTS" /grant "CC\svc-onbjea-logon$:(OI)(CI)M"
  ```
  **Zu verifizieren**, dass DFSR die Datei danach repliziert (Ordner liegt unter SYSVOL).

### 4.3 Registrieren

Als Administrator auf dem jeweiligen Server, Windows PowerShell 5.1:

```powershell
# DC01 (Fileserver)
.\Register-OnboardingJea.ps1 -Role Home -WorkerGmsa 'CC\svc-onboard$' -RunAs VirtualAccount
# DC03 (Domänencontroller)
.\Register-OnboardingJea.ps1 -Role Logon -WorkerGmsa 'CC\svc-onboard$' -RunAs Gmsa -EndpointGmsa 'CC\svc-onbjea-logon$'
# CC01 (Entra Connect)
.\Register-OnboardingJea.ps1 -Role Sync -WorkerGmsa 'CC\svc-onboard$' -RunAs VirtualAccount -RunAsGroup 'ADSyncOperators'
```

- [ ] Das Skript kopiert das Modul nach `%ProgramFiles%\WindowsPowerShell\Modules`, erzeugt die
  `.pssc` (`RestrictedRemoteServer`, `NoLanguage`, Transcripts), prüft sie mit
  `Test-PSSessionConfigurationFile` und registriert sie. Die `.pssc.template`-Dateien zeigen das
  Ergebnis zur Durchsicht.
- [ ] Ein früher auf DC01 registrierter Endpunkt mit `Set-OnbLogonScript` wird durch die neue
  Registrierung ersetzt (gleicher Name `CC.Onboarding`, Rolle `OnboardingHome`).
- [ ] Modulordner auf allen drei Servern: Schreibrechte nur Administratoren.
- [ ] Sichtbare Befehle prüfen (lokal, ohne Verbindung):
  ```powershell
  Get-PSSessionCapability -ConfigurationName CC.Onboarding -Username 'CC\svc-onboard$' | Select-Object Name        # DC01
  Get-PSSessionCapability -ConfigurationName CC.Onboarding.Logon -Username 'CC\svc-onboard$' | Select-Object Name  # DC03
  ```
  Erwartet auf DC01 `New-OnbHomeFolder`, `New-OnbHomeShare`, auf DC03 nur `Set-OnbLogonScript`,
  jeweils plus die `RestrictedRemoteServer`-Standardbefehle.
- [ ] WinRM auf DC01, DC03 und CC01 aktiv (`Test-WSMan` vom `<ONB-HOST>`), Firewall 5985 vom
  `<ONB-HOST>`.

### 4.4 Manueller JEA-Testaufruf

Der Worker ruft die Endpunkte per implizitem Remoting auf (`New-PSSession -ConfigurationName …`,
`Import-PSSession -Prefix Remote`, lokaler Aufruf, Session im `finally` schließen). Dieser Weg ist
**zu verifizieren** – genau das prüft `scripts/tools/Test-OnboardingJea.ps1` (PowerShell 7,
Worker-Host; nicht Teil der Endpunkt-Dateien in `scripts/jea`) unter der Identität des Workers. Ein
gMSA kann sich nicht interaktiv anmelden, daher als geplante Aufgabe:

```powershell
$script = 'C:\Program Files\CCOnboarding\scripts\tools\Test-OnboardingJea.ps1'
$action = New-ScheduledTaskAction -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' `
    -Argument "-NoProfile -NonInteractive -File `"$script`" -ComputerName DC01 -ConfigurationName CC.Onboarding -OutFile C:\Temp\jea-dc01.json"
$principal = New-ScheduledTaskPrincipal -UserId 'CC\svc-onboard$' -LogonType Password
Register-ScheduledTask -TaskName 'Onboarding JEA-Test' -Action $action -Principal $principal
Start-ScheduledTask -TaskName 'Onboarding JEA-Test'
# warten, dann:
Get-Content C:\Temp\jea-dc01.json
Unregister-ScheduledTask -TaskName 'Onboarding JEA-Test' -Confirm:$false
```

Erwartung:

- [ ] DC01 (`-ConfigurationName CC.Onboarding`, optional `-UserRight`/`-AdditionalAces` wie im Admin-UI):
  `visibleCommands` nur die zwei Home-Funktionen plus Standardbefehle; zwei Dry-Run-Aufrufe mit
  `plannedActions` (für die Test-sam `jeatest` meldet `New-OnbHomeFolder` ggf. `waiting`, weil das
  Konto nicht existiert – korrekt); ungültige sam und Principal außerhalb der Allowlist abgelehnt;
  auf DC01 wurde **nichts** angelegt.
- [ ] DC03 (`-ComputerName DC03 -ConfigurationName CC.Onboarding.Logon`): nur `Set-OnbLogonScript`
  sichtbar; Dry-Run mit `plannedActions`, falscher Hash abgelehnt; NETLOGON unverändert.
- [ ] Gegenprobe: derselbe Aufruf mit einem Admin-Konto, das nicht in den `RoleDefinitions` steht,
  wird abgewiesen („Zugriff verweigert“).
- [ ] CC01: `-ComputerName CC01 -ConfigurationName CC.Onboarding.Sync` (nur Befehlsliste); mit
  `-TriggerSync` wird ein **echter** Delta-Sync gestartet.

### 4.5 NTFS-Rechte der Home-Ordner (DECISIONS X17)

- Soll: Vererbung aus, Besitzer `HomeOwner`, Benutzer mit `GlobalConfig.Home.UserRight`
  (Startwert **Ändern** – bewusste Abweichung vom uneinheitlichen Bestand, damit Benutzer keine
  Rechte ändern können), dazu `GlobalConfig.Home.AdditionalAces` (Startwert SYSTEM und
  BUILTIN\Administrators = Vollzugriff) und `HomeAdditionalAces` der Abteilung (gleicher Principal:
  Abteilung gewinnt). Alles mit Vererbung auf Unterordner und Dateien.
- Vorhandener Ordner mit abweichenden Rechten ⇒ Step `NeedsInput` mit `home-acl-mismatch` und den
  Abweichungen; „Überschreiben“ setzt die Rechte vollständig auf das Soll.
- [ ] **Zu verifizieren** auf DC01: `NTAccount`-Auflösung von `SYSTEM` und
  `BUILTIN\Administrators` (Sprache des Betriebssystems); sonst SIDs (`S-1-5-18`,
  `S-1-5-32-544`) im Admin-UI **und** in der Allowlist verwenden.

## 5. Worker-Host (`<ONB-HOST>`)

- [ ] PowerShell 7 (`C:\Program Files\PowerShell\7\pwsh.exe`) und RSAT AD-Modul
  (`Install-WindowsFeature RSAT-AD-PowerShell` bzw. Windows-Capability). Das Modul wird in pwsh 7
  nativ geladen – **zu verifizieren** mit `pwsh -c "Import-Module ActiveDirectory; Get-ADDomain"`
  unter einem Testkonto.
- [ ] Skriptordner, z. B. `C:\Program Files\CCOnboarding\scripts` (Inhalt von `scripts/`, ohne
  `tests/` im Betrieb). **Schreibrechte nur Administratoren** – wer hier schreiben kann, führt Code
  als Worker-gMSA aus:
  ```cmd
  icacls "C:\Program Files\CCOnboarding\scripts" /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "CC\svc-onboard$:(OI)(CI)RX"
  ```
- [ ] Heruntergeladene Dateien entsperren (`Get-ChildItem -Recurse | Unblock-File`), sonst blockiert
  die Ausführungsrichtlinie. Signieren aller Skripte mit eigenem Code-Signing-Zertifikat und
  `AllSigned` ist **empfohlen, später**.
- [ ] Worker-`appsettings.json`:
  ```json
  "Integrations": {
    "Steps": { "OnPrem": "DryRun", "Cloud": "Fake" },
    "Scripts": {
      "PwshPath": "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
      "ScriptsDirectory": "C:\\Program Files\\CCOnboarding\\scripts",
      "Timeout": "00:04:00",
      "TimeoutOverrides": { "EXO.": "00:06:00", "Teams.": "00:10:00" },
      "HomeConfigurationName": "CC.Onboarding",
      "LogonConfigurationName": "CC.Onboarding.Logon",
      "SyncConfigurationName": "CC.Onboarding.Sync"
    }
  },
  "Worker": { "ExecutionTimeout": "00:12:00" }
  ```
  `Worker:ExecutionTimeout` muss größer als der längste Skript-Timeout sein (inkl.
  `TimeoutOverrides`), sonst bricht der Start ab. Teams-Steps brauchen mehr Zeit, weil das Modul
  `MicrosoftTeams` langsam lädt.

## 6. Passwort-Zertifikat (DECISIONS W2/P1)

- [ ] Zertifikat in `LocalMachine\My` auf `<ONB-HOST>`, privater Schlüssel **nicht exportierbar**.
- [ ] Private-Key-ACL (certlm.msc → „Private Schlüssel verwalten“): Lesen nur für das
  Worker-gMSA (plus Administratoren/SYSTEM). Das Web-Konto braucht nur den öffentlichen Schlüssel.
- [ ] Thumbprint im Admin-UI (Global-Konfiguration) eintragen; `SecretProtection:Mode = Certificate`
  in Web und Worker.

## 7. Windows-Dienst Worker

```cmd
sc.exe create Onboarding.Worker binPath= "C:\Program Files\CCOnboarding\worker\Onboarding.Worker.exe" obj= "CC\svc-onboard$" start= delayed-auto
sc.exe failure Onboarding.Worker reset= 86400 actions= restart/60000
```

- [ ] Datenbankordner: Lesen/Schreiben für Worker- **und** Web-Konto (gemeinsame SQLite-Datei,
  DECISIONS B1), lokal, keine Netzwerkfreigabe.
- [ ] Ein zweiter Start bricht mit Exit-Code 1 ab (DECISIONS B3) – nicht als Fehler werten.

## 8. Web (nur lesend, DECISIONS X6)

- [ ] Web-Prozess läuft unter `CC\svc-onbweb$`; LDAP bindet per Negotiate mit dieser Identität
  (Signing + Sealing, Port 389).
- [ ] `appsettings.json` des Web:
  ```json
  "Integrations": {
    "Read": {
      "Directory": "Real",
      "Graph": "Fake",
      "Ldap": { "Server": "", "Port": 389, "Timeout": "00:00:15", "PageSize": 500 }
    }
  }
  ```
  `Server` leer = `GlobalConfig.DomainFqdn`; alternativ ein fester DC.
- [ ] **Zu verifizieren**, dass das Web-Konto als „Authenticated User“ lesen kann: `proxyAddresses`,
  `telephoneNumber`, `msExchRecipientTypeDetails`, `extensionAttribute15`, `canonicalName` der OUs,
  `minPwdLength`/`pwdProperties` am Domänenobjekt.
- [ ] Hinweis: Die Kennwortrichtlinie kommt aus der Default Domain Policy; Fine-Grained Password
  Policies werden nicht berücksichtigt.

## 9. Pester-Tests (lokal, ohne Verbindungen)

```powershell
Install-Module Pester -MinimumVersion 5.5.0 -Scope CurrentUser
pwsh -NoProfile -Command "Invoke-Pester ./scripts/tests -Output Detailed"
```

Die Gesamtsuite läuft unter **PowerShell 7 (`pwsh`)**, weil die Step-Skripte `#Requires -Version 7.2`
tragen. Unter Windows PowerShell 5.1 überspringen `AdSteps`, `CloudSteps`, `JeaSteps` und
`OnboardingStep` ihre Tests mit einer Warnung (`scripts/tests/TestEnv.ps1`). Unter 5.1 läuft nur
`JeaEndpoint.Tests.ps1`, siehe unten.

Alle AD-/SMB-/ADSync-, Graph-, EXO- und Teams-Cmdlets sind gemockt; `scripts/tests/Stubs.ps1` definiert werfende Stubs, damit
ein vergessener Mock fehlschlägt statt ein echtes System zu berühren. Die Tests prüfen bei jedem
`needsInput`/`failed`/`waiting` auch den Grund-Code (DECISIONS X10).

Zusätzlich die JEA-Endpunkt-Tests unter **Windows PowerShell 5.1** (Standard-Host der Endpunkte):

```powershell
powershell.exe -NoProfile -Command "Invoke-Pester .\scripts\tests\JeaEndpoint.Tests.ps1 -Output Detailed"
```

Der C#-Test `ScriptConventionsTests` prüft nur, was sich ohne Fehlalarme statisch prüfen lässt
(UTF-8-BOM aller PowerShell-Dateien, `Join-Path` mit mehr als zwei Teilen, einige PS7-/.NET-Core-only
Bezeichner, `#Requires -Version 7` in `scripts/jea`). Operatoren wie `&&`, `||`, `??`, `?.` und der
Ternary-Operator sind nur durch diesen 5.1-Lauf abgedeckt.

## 10. Testreihenfolge

1. **Fake** (Standard): Web + Worker wie in der Entwicklung; nichts verlässt den Host.
2. **Web `Directory = Real`**: Kollisionsprüfung, Vorgesetztensuche, OU-Liste und
   Passwortrichtlinie gegen das echte AD (nur lesend). Durchwahl-Kollision mit einer bekannten
   belegten Durchwahl prüfen.
3. **Worker `OnPrem = DryRun`**: Auftrag für einen Testbenutzer in der **Test-OU** (Bereich
   vorübergehend auf die Test-OU zeigen lassen). Jeder On-Prem-Step endet in `NeedsInput` mit
   „Dry-Run – würde: …“ (DECISIONS X4). Bericht prüfen. Steps, deren Zielzustand bereits erreicht
   ist, stehen auf `Done`.
4. **Worker `OnPrem = Real`**, Worker neu starten, betroffene Steps per „Erneut versuchen“
   anstoßen. Ergebnis im AD, auf DC01 (F:\Home: Vererbung aus, Besitzer, ACEs; Freigabe) und in
   NETLOGON auf DC03 prüfen; SHA-256 der Skriptdatei mit der Vorschau vergleichen.
5. Idempotenz: Steps erneut ausführen → keine Änderung, `Done`. Ein Attribut am Testkonto von Hand
   ändern → `AD.CreateUser` korrigiert es beim nächsten Lauf, setzt aber **nie** das Passwort neu
   (DECISIONS X8).
6. Fehlerpfade: Gruppe ohne Delegation, Endpunkt nicht erreichbar → `failed`/`waiting` mit
   bereinigter Meldung; Details nur im Worker-Log.
7. Erst danach Bereiche wieder auf die echten OUs stellen.

Solange Cloud auf `Fake` steht, legt die Fake-Cloud ihre Benutzer selbst an (`DetachedFromOnPrem`).
Cloud = `DryRun`/`Real` ist nur mit On-Prem `DryRun`/`Real` erlaubt (sonst Startfehler). Cloud-Tests:
§11.8.

## 11. Cloud (Phase 4b)

### 11.1 Zertifikate (beide ≤ 1 Jahr Laufzeit, SPEC §9)

- [ ] **Worker-App-Zertifikat** auf `<ONB-HOST>` in `LocalMachine\My`, privater Schlüssel nicht
  exportierbar, Private-Key-ACL nur Worker-gMSA (+ Administratoren/SYSTEM). Öffentlichen Teil
  (`.cer`) an die Worker-App-Registrierung hochladen.
- [ ] **Web-Lese-Zertifikat** separat, Private-Key-ACL nur Web-Konto; öffentlichen Teil an die
  Lese-App hochladen.
- [ ] Ablaufwarnung (DECISIONS X15): Der Worker prüft beim Start und täglich Passwort- und
  Cloud-Zertifikat, das Web sein Lese-Zertifikat. ITAdmins sehen ab `Web:CertificateWarningDays`
  (Standard 30) Tagen vor Ablauf ein Banner; ebenso bei fehlendem Zertifikat oder wenn die letzte
  Worker-Prüfung älter als 48 h ist.

### 11.2 Worker-App-Registrierung

- [ ] App-Registrierung „CC Onboarding Worker“, nur Zertifikat (kein Client-Secret).
- [ ] **Graph-Anwendungsberechtigungen** (Admin-Zustimmung): `User.ReadWrite.All` (usageLocation),
  `LicenseAssignment.ReadWrite.All` (assignLicense), `Organization.Read.All` (subscribedSkus).
  `GroupMember.ReadWrite.All` aus SPEC §3 wird **nicht** vergeben: kein Cloud-Step schreibt Gruppen.
  **Zu verifizieren**, ob `User.ReadWrite.All` für `usageLocation` reicht oder
  `LicenseAssignment.ReadWrite.All` allein genügt.
- [ ] **Exchange Online – RBAC for Applications** (statt `Exchange.ManageAsApp` mit globaler Rolle):
  ```powershell
  Connect-ExchangeOnline
  New-ServicePrincipal -AppId <AppId> -ObjectId <ObjectId der Enterprise-App> -DisplayName 'CC Onboarding Worker'
  New-ManagementScope -Name 'CC Onboarding' -RecipientRestrictionFilter '<Filter, siehe unten>'
  New-ManagementRoleAssignment -App <ObjectId> -Role 'Mail Recipients' -CustomResourceScope 'CC Onboarding'
  ```
  - Der Scope muss **neue Benutzer und alle konfigurierten Freigabepostfächer** umfassen:
    `Add-MailboxPermission` und `Add-RecipientPermission` laufen auf dem Freigabepostfach, nicht
    auf dem Benutzer. Ein Scope nur über die neuen Benutzer reicht nicht.
  - Möglicher Filter (**zu verifizieren**): Benutzer über `Company` der Bereiche
    (`AreaConfig.Company`) und Freigabepostfächer über ein gesetztes Custom Attribute, z. B.
    `(Company -eq '<Firma A>') -or (Company -eq '<Firma B>') -or (CustomAttribute10 -eq 'Onboarding')`;
    an jedem Freigabepostfach `Set-Mailbox <Postfach> -CustomAttribute10 Onboarding`.
    Alternative: Administrative Unit mit dynamischer Mitgliedschaft.
  - **Zu verifizieren**, ob die Rolle `Mail Recipients` `Set-CASMailbox`, `Add-MailboxPermission`
    und `Add-RecipientPermission` abdeckt; Prüfung:
    ```powershell
    Test-ServicePrincipalAuthorization -Identity <ObjectId> -Resource <Freigabepostfach>
    Test-ServicePrincipalAuthorization -Identity <ObjectId> -Resource <Testbenutzer>
    ```
  - Neue Freigabepostfächer in der Abteilungskonfiguration ⇒ Scope-Attribut setzen.
- [ ] **Teams**: Entra-Rolle für den Service Principal der App, **zu verifizieren**: „Teams
  Telephony Administrator“ (bevorzugt) oder „Teams Administrator“ – je nachdem, was
  `Set-CsPhoneNumberAssignment`, `Grant-Cs*Policy` und `Get-CsOnlineUser` app-only verlangen.
- [ ] Conditional Access für Workload Identities: falls genutzt, die App auf den Worker-Host
  beschränken (Standort/IP).

### 11.3 Web-Lese-App-Registrierung (DECISIONS X6/X16)

- [ ] Eigene App „CC Onboarding Web (lesend)“, nur Zertifikat, Anwendungsberechtigungen
  `Organization.Read.All`, `User.Read.All`, `Group.Read.All`, `OrgContact.Read.All`.
- [ ] Web-`appsettings.json`:
  ```json
  "Integrations": {
    "Read": {
      "Graph": "Real",
      "GraphAuth": { "TenantId": "<GUID>", "ClientId": "<GUID>", "CertificateThumbprint": "<40 hex>" }
    }
  },
  "Web": { "CertificateWarningDays": 30 }
  ```
- [ ] Lizenzübersicht im Auftragsformular kommt dann aus `subscribedSkus`; die Kollisionsprüfung
  sucht zusätzlich Cloud-only-Objekte (Benutzer, Microsoft-365-Gruppen, Kontakte) in Entra.
  Synchronisierte Objekte prüft weiter das AD.

### 11.4 Module auf dem Worker-Host (feste Versionen, AllUsers)

```powershell
Install-Module Microsoft.Graph.Authentication -RequiredVersion <x.y.z> -Scope AllUsers
Install-Module ExchangeOnlineManagement -RequiredVersion <x.y.z> -Scope AllUsers
Install-Module MicrosoftTeams -RequiredVersion <x.y.z> -Scope AllUsers
```

- [ ] Versionen festhalten (hier eintragen) und Updates bewusst testen; Modulordner nur für
  Administratoren beschreibbar (wie der Skriptordner).

### 11.5 Worker-Konfiguration

```json
"Integrations": {
  "Steps": { "OnPrem": "Real", "Cloud": "DryRun" },
  "Cloud": {
    "TenantId": "<GUID>",
    "AppId": "<GUID>",
    "CertificateThumbprint": "<40 hex>",
    "ExchangeOrganization": "<tenant>.onmicrosoft.com",
    "ManualSteps": []
  }
}
```

Ungültige Werte brechen den Start ab (Meldung nennt den Schlüssel, nie den Wert).

### 11.6 Voicemail und Weiterleitung (SPEC §3, DECISIONS X13)

- [ ] **Zu verifizieren**: app-only-Unterstützung von `Set-CsOnlineVoicemailUserSettings` und
  `Set-CsUserCallingSettings`. Test mit einem Testbenutzer im DryRun und dann Real.
- [ ] Nicht unterstützt ⇒ Step in `Integrations:Cloud:ManualSteps` eintragen
  (`Teams.Voicemail`, `Teams.Forwarding`). Der Step liefert dann eine **ManualTask** mit fertigem
  Befehl (nur UPN und Konfigurationswerte, keine Anmeldedaten) und verbindet sich nicht.

### 11.7 Zu verifizieren (Cloud)

- [ ] Eigentumsprüfung: `onPremisesSecurityIdentifier` des synchronisierten Benutzers = objectSid
  aus `AD.CreateUser` (DECISIONS X14). Rückfall `onPremisesImmutableId` = Base64(objectGUID) nur
  ohne gespeicherte SID; hängt am Source Anchor von Entra Connect.
- [ ] Form der Graph-Fehler (`Exception.Response.StatusCode`) für 404/429/403.
- [ ] `Get-EXOMailbox` bei nicht vorhandenem Postfach (wird als „nicht gefunden“ gewertet).
- [ ] `Get-CsOnlineUser`: `FeatureTypes` enthält `PhoneSystem`, sobald MCOEV wirkt.
- [ ] Graph-Filter auf `proxyAddresses`: Groß-/Kleinschreibung (es werden beide Präfixe
  abgefragt) und ob `/contacts` den Lambda-Filter kann (Kontakte werden nur über `mail` gesucht).
- [ ] Testpunkt X1: Lizenz, Postfach und Telefonnummer bei `accountEnabled = false`.

### 11.8 Testreihenfolge Cloud

1. Pester (§9), dann Web `Graph = Real`: Lizenzübersicht und eine bekannte Cloud-only-Adresse als
   Kollision prüfen.
2. Worker `OnPrem = Real`, `Cloud = DryRun` mit Testbenutzer: Jeder Cloud-Step endet mit
   „Dry-Run – würde: …“ oder ist `Done`; Bericht prüfen. Warte-Steps (`Entra.WaitUser`,
   `EXO.WaitMailbox`, `Teams.WaitUser`) laufen auch im Dry-Run gegen den echten Tenant.
3. `Cloud = Real`, Worker neu starten, Steps „Erneut versuchen“. Lizenzen, Postfach,
   Freigabepostfach-Rechte, Nummer, Policies, Voicemail und Weiterleitung prüfen.
4. Idempotenz: alle Cloud-Steps erneut ausführen → `Done` ohne Änderung.
5. Fehlerpfade: SKU ohne freie Lizenz → `failed` mit `no-free-license`; fremder Entra-Benutzer mit
   gleicher UPN → `needsInput` mit `foreign-cloud-account`.

## Verworfene Alternativen

| Alternative | Warum verworfen |
|---|---|
| Home-Ordner, Freigabe und Anmeldeskript direkt vom Worker über Admin-Share (`\\DC01\F$`) bzw. UNC auf NETLOGON | Worker-gMSA bräuchte Schreibrechte auf F:\Home und NETLOGON bzw. lokale Admin-Rechte auf dem Fileserver; Pfade kämen vom Worker (Traversal-Risiko). |
| `New-SmbShare`/ACL-Änderungen per CIM-Session oder `Invoke-Command` mit Admin-Rechten | Worker-gMSA wäre lokaler Admin auf DC01; keine Parametervalidierung auf dem Zielsystem. |
| Anmeldeskript über den DC01-Endpunkt | DC01 ist Fileserver, NETLOGON liegt auf den DCs; ein Virtual Account auf einem DC wäre Domain Admin. Ersetzt durch `CC.Onboarding.Logon` auf DC03 mit gMSA. |
| Worker-gMSA in `ADSyncOperators` auf CC01 | Erlaubt mehr als den Delta-Sync (alle ADSync-Cmdlets der Gruppe); JEA beschränkt auf genau `Start-OnbDeltaSync`. |
| `Exchange.ManageAsApp` mit Verzeichnisrolle „Exchange Administrator“ | Ganzer Tenant statt Management Scope; RBAC for Applications begrenzt auf Benutzer und Freigabepostfächer (§11.2). |
