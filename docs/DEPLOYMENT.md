# Deployment-Checkliste – On-Prem (Phase 4a)

Checkliste für die Inbetriebnahme von Web und Worker gegen das echte AD. Ausgeführt wird alles
**von einem Menschen**; der Code selbst verbindet sich erst, wenn die jeweilige Gruppe auf `DryRun`
bzw. `Real` steht. Cloud-Teil (Entra, EXO, Teams, Graph-App-Registrierungen) folgt mit Phase 4b.

Punkte mit **„zu verifizieren“** sind nicht aus der Spec oder Dokumentation belegbar und müssen vor
dem Echtbetrieb an der Umgebung geprüft werden. Namen wie `svc-onboard$` sind Beispiele.

Begriffe:

| Platzhalter | Bedeutung |
|---|---|
| `<ONB-HOST>` | Server, auf dem Web und Worker laufen (DECISIONS B1: derselbe Host) |
| `CC\svc-onboard$` | gMSA des Workers (schreibt in AD, ruft die JEA-Endpunkte auf) |
| `CC\svc-onbweb$` | Dienstkonto des Web (nur lesend) |
| `CC\svc-onbjea-dc$`, `CC\svc-onbjea-sync$` | optionale Endpunkt-gMSAs (Run-As-Variante b) |
| DC01 | Home-Server und DC mit NETLOGON (`GlobalConfig.Home.Server`) |
| CC01 | Entra-Connect-Server (`GlobalConfig.EntraConnectServer`) |

## 1. Rechte-Übersicht (Soll)

| Identität | Darf | Darf ausdrücklich **nicht** |
|---|---|---|
| Worker-gMSA | Benutzer in den drei Bereichs-OUs anlegen und die Onboarding-Attribute schreiben (Delegation §3), Mitgliedschaften der konfigurierten Gruppen ändern, sich an den JEA-Endpunkten `CC.Onboarding` (DC01) und `CC.Onboarding.Sync` (CC01) anmelden | lokaler Admin auf DC01/CC01, Mitglied von `ADSyncOperators`, Schreibzugriff auf F:\Home, SMB oder NETLOGON |
| Run-As-Konto DC01-Endpunkt | F:\Home (Ordner anlegen, ACL setzen), SMB-Freigaben anlegen, NETLOGON-Skriptordner schreiben | alles andere |
| Run-As-Konto CC01-Endpunkt | Delta-Sync starten (`ADSyncOperators`) | alles andere |
| Web-Konto | AD lesen (LDAP) | jedes Schreiben, JEA-Endpunkte, privater Schlüssel des Passwort-Zertifikats |

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

Dateien: `scripts/jea/`. Die Endpunkte kennen alle Pfade selbst; der Worker übergibt nur die sam
(und beim Anmeldeskript Base64-Inhalt + SHA-256).

### 4.1 Endpunkt-Konfiguration anpassen (DC01)

- [ ] `scripts/jea/CCOnboarding/OnboardingEndpoint.psd1` an die Umgebung anpassen. Die Werte
  müssen mit der Global-Konfiguration im Admin-UI übereinstimmen:

  | Schlüssel | muss passen zu |
  |---|---|
  | `HomeRoot` | `GlobalConfig.Home.LocalRoot` |
  | `ShareNamePattern` | `GlobalConfig.Home.ShareNamePattern` |
  | `NetbiosDomain` | `GlobalConfig.DomainNetBios` |
  | `LogonFileNamePattern` | `GlobalConfig.LogonScript.FileNamePattern` |
  | `LogonScriptDirectory` | lokaler Pfad hinter der NETLOGON-Freigabe: `(Get-SmbShare NETLOGON).Path` – **zu verifizieren** |
  | `ShareFullAccess` | Vollzugriff auf Home-Freigaben neben dem Benutzer – an bestehender Freigabe prüfen (`Get-SmbShareAccess <sam>$`), **zu verifizieren** (SPEC §11) |

- [ ] Ändert jemand später die Global-Konfiguration (Home-Pfad, Freigabemuster, Skriptname),
  muss diese Datei auf DC01 mitgezogen und das Modul neu registriert werden (§4.3).

### 4.2 Run-As-Konto wählen

JEA führt die Funktionen unter einem Run-As-Konto aus, nicht unter dem Worker-gMSA. Zwei Varianten:

| | (a) Virtual Account + `RunAsVirtualAccountGroups` | (b) eigenes Endpunkt-gMSA (`GroupManagedServiceAccount`) |
|---|---|---|
| Einrichtung | keine Kontopflege; Rechte über eine Gruppe | gMSA anlegen, auf DC01/CC01 installieren, Rechte direkt vergeben |
| DC01 | **Achtung:** Virtual Accounts sind auf DCs ohne Gruppenangabe **Domain Admins**. Mit `RunAsVirtualAccountGroups` ersetzt die Gruppe diese Mitgliedschaft – **zu verifizieren** (`whoami /groups` im Endpunkt). Auf DCs gibt es keine lokalen Gruppen, die Gruppe muss eine Domänengruppe sein. | Rechte genau auf F:\Home, SMB-Anlage, NETLOGON-Ordner; keine Admin-Gruppe nötig – **zu verifizieren**, ob `New-SmbShare` ohne lokale Admin-Rechte (bzw. auf DCs ohne Administrators/Server Operators) möglich ist |
| CC01 | Gruppe `ADSyncOperators` (lokal) | gMSA Mitglied in `ADSyncOperators` |
| Netzwerkzugriff | Virtual Account greift als Computerkonto ins Netz (hier nicht nötig, alles lokal) | als gMSA |
| Nachvollziehbarkeit | Transcripts unter `C:\ProgramData\CCOnboarding\Transcripts`, Konto pro Sitzung neu | zusätzlich feste Identität in Sicherheitsprotokoll und ACLs |

Empfehlung: **(b) auf DC01** (kein Risiko einer Domain-Admin-Sitzung), **(a) auf CC01**
(einfach, `ADSyncOperators` ist lokal). Entscheidung und Test durch IT.

Rechte des Run-As-Kontos auf DC01 (Variante b, sinngemäß für die Gruppe in a):

- [ ] F:\Home: „Ordner erstellen“ und „Berechtigungen ändern“ auf `HomeRoot` (diese Ebene und
  Unterordner), damit `New-OnbHomeFolder` Ordner anlegen und dem Benutzer „Ändern“ geben kann.
- [ ] SMB: Freigaben anlegen und Freigabeberechtigungen setzen – **zu verifizieren**, welche
  Mitgliedschaft dafür auf einem DC mindestens nötig ist.
- [ ] NETLOGON: Schreiben/Ändern im `LogonScriptDirectory` (nicht im ganzen SYSVOL). DFSR
  repliziert die Datei auf die übrigen DCs.

### 4.3 Registrieren

Auf DC01 bzw. CC01 als Administrator, Windows PowerShell 5.1:

```powershell
# DC01, Variante b
.\Register-OnboardingJea.ps1 -Role Dc -WorkerGmsa 'CC\svc-onboard$' -RunAs Gmsa -EndpointGmsa 'CC\svc-onbjea-dc$'
# CC01, Variante a
.\Register-OnboardingJea.ps1 -Role Sync -WorkerGmsa 'CC\svc-onboard$' -RunAs VirtualAccount -RunAsGroup 'ADSyncOperators'
```

- [ ] Das Skript kopiert das Modul nach `%ProgramFiles%\WindowsPowerShell\Modules`, erzeugt die
  `.pssc` (`RestrictedRemoteServer`, `NoLanguage`, Transcripts), prüft sie mit
  `Test-PSSessionConfigurationFile` und registriert sie. Die `.pssc.template`-Dateien zeigen das
  Ergebnis zur Durchsicht.
- [ ] Modulordner auf DC01/CC01: Schreibrechte nur Administratoren (wer das Modul ändert, ändert,
  was der Endpunkt mit Run-As-Rechten ausführt).
- [ ] Prüfen, welche Befehle das Worker-gMSA sieht (lokal, ohne Verbindung):
  ```powershell
  Get-PSSessionCapability -ConfigurationName CC.Onboarding -Username 'CC\svc-onboard$' | Select-Object Name
  ```
  Erwartet: `New-OnbHomeFolder`, `New-OnbHomeShare`, `Set-OnbLogonScript` plus die
  `RestrictedRemoteServer`-Standardbefehle (`Get-Command`, `Exit-PSSession`, …).
- [ ] WinRM auf DC01 und CC01 aktiv (`Test-WSMan DC01` vom `<ONB-HOST>`), Firewall 5985 vom
  `<ONB-HOST>`.

### 4.4 Manueller JEA-Testaufruf

Der Worker ruft die Endpunkte per implizitem Remoting auf (`New-PSSession -ConfigurationName …`,
`Import-PSSession -Prefix Remote`, lokaler Aufruf, Session im `finally` schließen). Dieser Weg ist
**zu verifizieren** – genau das prüft `scripts/jea/Test-OnboardingJea.ps1` unter der Identität des
Workers (ein gMSA kann sich nicht interaktiv anmelden, daher als geplante Aufgabe):

```powershell
$script = 'C:\Program Files\CCOnboarding\scripts\jea\Test-OnboardingJea.ps1'
$action = New-ScheduledTaskAction -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' `
    -Argument "-NoProfile -NonInteractive -File `"$script`" -ComputerName DC01 -ConfigurationName CC.Onboarding -OutFile C:\Temp\jea-dc.json"
$principal = New-ScheduledTaskPrincipal -UserId 'CC\svc-onboard$' -LogonType Password
Register-ScheduledTask -TaskName 'Onboarding JEA-Test' -Action $action -Principal $principal
Start-ScheduledTask -TaskName 'Onboarding JEA-Test'
# warten, dann:
Get-Content C:\Temp\jea-dc.json
Unregister-ScheduledTask -TaskName 'Onboarding JEA-Test' -Confirm:$false
```

Erwartung in `jea-dc.json`:

- [ ] `identity` = `CC\svc-onboard$`; `visibleCommands` nur die drei Funktionen plus Standardbefehle.
- [ ] Drei Dry-Run-Aufrufe mit `plannedActions` (für die Test-sam `jeatest` meldet
  `New-OnbHomeFolder` ggf. `waiting`, weil das Konto nicht existiert – das ist korrekt).
- [ ] Beide Negativ-Aufrufe (ungültige sam, falscher Hash) abgelehnt.
- [ ] Auf DC01 wurde **nichts** angelegt (F:\Home, Freigaben, NETLOGON unverändert); Transcript liegt vor.
- [ ] Gegenprobe: derselbe Aufruf mit einem Admin-Konto, das nicht in den `RoleDefinitions` steht,
  wird abgewiesen („Zugriff verweigert“).
- [ ] CC01: wie oben mit `-ComputerName CC01 -ConfigurationName CC.Onboarding.Sync` (nur
  Befehlsliste); mit `-TriggerSync` wird ein **echter** Delta-Sync gestartet.

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
      "DcConfigurationName": "CC.Onboarding",
      "SyncConfigurationName": "CC.Onboarding.Sync"
    }
  }
  ```
  `Worker:ExecutionTimeout` muss größer als `Scripts:Timeout` bleiben.

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
      "Licenses": "Fake",
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
Invoke-Pester ./scripts/tests -Output Detailed
```

Alle AD-/SMB-/ADSync-Cmdlets sind gemockt; `scripts/tests/Stubs.ps1` definiert werfende Stubs, damit
ein vergessener Mock fehlschlägt statt ein echtes System zu berühren.

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
   anstoßen. Ergebnis im AD, auf F:\Home, an der Freigabe und in NETLOGON prüfen; SHA-256 der
   Skriptdatei mit der Vorschau vergleichen.
5. Idempotenz: Steps erneut ausführen → keine Änderung, `Done`. Ein Attribut am Testkonto von Hand
   ändern → `AD.CreateUser` korrigiert es beim nächsten Lauf, setzt aber **nie** das Passwort neu
   (DECISIONS X8).
6. Fehlerpfade: Gruppe ohne Delegation, Endpunkt nicht erreichbar → `failed`/`waiting` mit
   bereinigter Meldung; Details nur im Worker-Log.
7. Erst danach Bereiche wieder auf die echten OUs stellen.

Cloud bleibt bis Phase 4b auf `Fake`; die Fake-Cloud legt ihre Benutzer dann selbst an
(`DetachedFromOnPrem`).

## Verworfene Alternativen

| Alternative | Warum verworfen |
|---|---|
| Home-Ordner, Freigabe und Anmeldeskript direkt vom Worker über Admin-Share (`\\DC01\F$`) bzw. UNC auf NETLOGON | Worker-gMSA bräuchte Schreibrechte auf F:\Home und NETLOGON bzw. lokale Admin-Rechte auf einem DC; Pfade kämen vom Worker (Traversal-Risiko). |
| `New-SmbShare`/ACL-Änderungen per CIM-Session oder `Invoke-Command` mit Admin-Rechten | Gleiche Rechteausweitung (lokaler Admin auf DC01 = faktisch Domain Admin); keine Parametervalidierung auf dem Zielsystem. |
| Worker-gMSA in `ADSyncOperators` auf CC01 | Erlaubt mehr als den Delta-Sync (alle ADSync-Cmdlets der Gruppe); JEA beschränkt auf genau `Start-OnbDeltaSync`. |
