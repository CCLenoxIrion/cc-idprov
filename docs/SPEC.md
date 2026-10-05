# SPEC: IT-Onboarding/Offboarding-Service (CleanControlling GmbH)

Status: Entwurf v0.4 · **Umsetzungsumfang v1: nur Onboarding** · Zielgruppe: Implementierung mit Claude Code

## 1. Ziel

Neue und ausscheidende Mitarbeitende werden aus einem strukturierten Auftrag heraus vollständig, reproduzierbar und protokolliert eingerichtet bzw. entfernt. Alle abteilungsabhängigen Unterschiede (OU, Gruppen, Laufwerke, Postfächer, Telefonie) liegen in einer **grafisch pflegbaren Konfiguration**, nicht im Code.

Nicht-Ziele (v1): **Offboarding** (folgt als v2, §8 ist nur Entwurf; Datenmodell und Status-Logik aber so bauen, dass Offboarding als zweiter Auftragstyp ohne Umbau dazukommt), Hardware-Ausgabe, Intune-Geräteregistrierung, D365-/LIMS-/ELO-Konten.

## 2. Eingabe (Auftrag)

| Feld | Pflicht | Validierung |
|---|---|---|
| Vorname | ja | Freitext |
| Nachname | ja | Freitext |
| Doktortitel | nein | Checkbox „Dr.“ |
| E-Mail | ja | wird aus Name vorbelegt (siehe unten), editierbar; eindeutig in AD/Entra |
| Bereich | ja | **Radio-Buttons, genau einer**: TecSa / Bio / Chemie (aus Konfiguration §4.2) |
| Abteilung | ja | Auswahl aus Konfiguration |
| Vorgesetzte(r) | ja | Auswahl aus AD (wird `manager`) |
| Datum | ja | Eintritt bzw. Austritt |
| Durchwahl | nein | nur Ziffern, 1–4 Stellen, nicht bereits vergeben |
| Typ | ja | v1: nur Onboarding (Feld trotzdem anlegen) |
| Startpasswort | ja | nur für `ITAdmin` sichtbar/editierbar, Eingabe bei Freigabe; Button „Generieren“; Prüfung gegen Domänen-Kennwortrichtlinie (Länge/Komplexität aus `Get-ADDefaultDomainPasswordPolicy`) |

Abgeleitete Werte:

- **sAMAccountName**: erster Buchstabe Vorname + Nachname, lowercase (`Lenox Irion` → `lirion`). Umlaute transliterieren (ä→ae, ö→oe, ü→ue, ß→ss), Leer-/Sonderzeichen entfernen, max. 20 Zeichen. Kollision → Auftrag stoppt mit Status `NeedsInput`, Admin vergibt Namen manuell (keine automatische Nummerierung).
- **E-Mail (`mail`)**: erster Buchstabe Vorname + `.` + Nachname, lowercase, gleiche Transliteration (`Lenox Irion` → `l.irion@cleancontrolling.de`). Muster konfigurierbar.
- **UPN** = E-Mail-Adresse (`l.irion@cleancontrolling.de`).
- **proxyAddresses** (aus Templates in §4.1, Präfix-Schreibweise exakt übernehmen, `SMTP:` groß = primär):
  - `SMTP:l.irion@cleancontrolling.de` (primär, entspricht `mail`)
  - `smtp:l.irion@cleancontrolling.com`
- **company** (Organisation → Firma) aus Bereich (§4.2).
- **extensionAttribute1** = `Dr.` wenn Checkbox Doktortitel gesetzt, sonst leer lassen (Attribut nicht setzen).
- **OU** aus Bereich (§4.2).
- **scriptPath** (Profil → Anmeldeskript) = `{sam}.bat`, nur Dateiname, kein Pfad.
- **Telefonnummer (E.164)** = `PhonePrefix` + Durchwahl → `+497465929678` + `0` = `+4974659296780`.
- **Anzeigeformat** für AD `telephoneNumber`: `+49 7465 929678-<DW>` (Format konfigurierbar).

## 3. Architektur

```
[Web-UI: Auftrag + Admin-Config + Status]  --(DB)-->  [Worker-Dienst]
        läuft ohne Schreibrechte                      läuft unter gMSA
                                                       ├─ AD (DC02/DC03/DC04)
                                                       ├─ SMB/NTFS auf DC01 (Fileserver, Member-Server)
                                                       ├─ NETLOGON (\\dc03\NETLOGON)
                                                       ├─ Entra Connect (Delta-Sync)
                                                       ├─ Graph (Entra)
                                                       ├─ Exchange Online
                                                       └─ Microsoft Teams
```

- **Web-UI**: ASP.NET Core (.NET 10 LTS), Blazor Server, Anmeldung über Entra ID. Rollen: `Requester` (Personalabteilung: Aufträge anlegen), `ITAdmin` (freigeben, Konfiguration, Retry).
- **Worker**: .NET 10 Worker Service als Windows-Dienst auf einem Member-Server, Konto: gMSA. Ruft pro Schritt ein PowerShell-7-Skript (`pwsh.exe -File step.ps1 -InputJson ...`) auf, Rückgabe als JSON. Begründung: AD-, EXO- und Teams-Module sind PowerShell-nativ; In-Process-Hosting der Module ist fehleranfällig.
- **DB**: SQLite (v1), Schema per EF Core Migrations.
- **Cloud-Auth**: App-Registrierung mit Zertifikat (im Zertifikatsspeicher des Worker-Hosts, nicht exportierbar).
  - Graph: `User.ReadWrite.All`, `GroupMember.ReadWrite.All`, `LicenseAssignment.ReadWrite.All`, `Organization.Read.All` (für `subscribedSkus`)
  - EXO: `Exchange.ManageAsApp` + Exchange-RBAC for Applications, gescoped
  - Teams: `Connect-MicrosoftTeams -CertificateThumbprint -ApplicationId -TenantId`
  - **Zu verifizieren**: App-Only-Unterstützung für `Set-CsOnlineVoicemailUserSettings` und `Set-CsUserCallingSettings`. Falls nicht unterstützt: diese Schritte als `ManualTask` mit fertigem Befehl im UI ausgeben.

## 4. Konfiguration (grafisch pflegbar)

Alles in der DB, im Admin-UI editierbar, mit Änderungshistorie (wer/wann/alt/neu).

### 4.1 Global

```json
{
  "DomainFqdn": "CC.local",
  "DomainNetBios": "<TBD>",
  "UpnSuffix": "cleancontrolling.de",
  "MailPattern": "{firstInitial}.{lastName}@cleancontrolling.de",
  "ProxyAddressTemplates": [
    "SMTP:{firstInitial}.{lastName}@cleancontrolling.de",
    "smtp:{firstInitial}.{lastName}@cleancontrolling.com"
  ],
  "DoctorTitle": { "Attribute": "extensionAttribute1", "Value": "Dr." },
  "PhonePrefixE164": "+497465929678",
  "PhoneDisplayFormat": "+49 7465 929678-{DW}",
  "Home": {
    "Server": "DC01",
    "LocalRoot": "F:\\Home",
    "ShareNamePattern": "{sam}$",
    "UncPattern": "\\\\dc01\\{sam}$"
  },
  "LogonScript": {
    "Path": "\\\\dc03\\NETLOGON",
    "FileNamePattern": "{sam}.bat"
  },
  "EntraConnectServer": "CC01",
  "UsageLocation": "DE",
  "LicenseMode": "Direct",
  "OneWinNativeOutlookEnabled": false,
  "Teams": {
    "VoiceRoutingPolicy": "INTStandard",
    "VoicemailPolicy": "CleanControlling Personal Voicemail",
    "VoicemailPromptLanguage": "de-DE",
    "PhoneNumberType": "DirectRouting"
  },
  "DisabledUsersOU": "<TBD>"
}
```

### 4.2 Bereiche (Gesellschaft)

Bestimmen OU und Firma. Im Auftrag als Radio-Buttons, genau einer.

| Bereich | OU (kanonisch) | OU (DN) | company |
|---|---|---|---|
| TecSa | `CC.local/CleanControlling/Technical/Users` | `OU=Users,OU=Technical,OU=CleanControlling,DC=CC,DC=local` | `CleanControlling GmbH` |
| Bio | `CC.local/CleanControlling/Medical/Biology/Users` | `OU=Users,OU=Biology,OU=Medical,OU=CleanControlling,DC=CC,DC=local` | `CleanControlling Medical GmbH & Co. KG` |
| Chemie | `CC.local/CleanControlling/Medical/Chemical/Users` | `OU=Users,OU=Chemical,OU=Medical,OU=CleanControlling,DC=CC,DC=local` | `CleanControlling Medical GmbH & Co. KG` |

UI: OU per AD-Browser auswählen statt DN tippen; beim Speichern prüfen, ob die OU existiert.

### 4.3 Pro Abteilung

Unabhängig vom Bereich (Gruppen, Postfächer, Laufwerke, Telefonie).

```json
{
  "Name": "Vertrieb",
  "AdGroups": ["GG-Vertrieb"],
  "Licenses": {
    "SkuPartNumbers": ["SPB"],
    "SkuPartNumbersIfPhone": ["MCOEV"],
    "DisabledServicePlans": [],
    "LicenseGroup": null
  },
  "SharedMailboxes": [
    { "Mailbox": "vertrieb@cleancontrolling.de", "FullAccess": true, "AutoMapping": true, "SendAs": true }
  ],
  "LogonScript": {
    "DisconnectDrives": ["S", "H", "U", "T", "V"],
    "ConnectDrives": [
      { "Letter": "H", "Unc": "{homeUnc}" },
      { "Letter": "S", "Unc": "\\\\dc01\\Vertrieb" }
    ],
    "ExtraLines": []
  },
  "Teams": {
    "AssignPhone": true,
    "Voicemail": true,
    "UnansweredForward": {
      "Enabled": true,
      "Delay": "00:00:20",
      "TargetType": "singleTarget",
      "Target": "CCVertrieb@cleancontrolling.de"
    }
  }
}
```

### 4.4 Logon-Skript-Editor

- UI: Liste „Laufwerke trennen“ (Buchstaben-Chips), Tabelle „Laufwerke verbinden“ (Buchstabe + UNC, Platzhalter erlaubt), Freitext „Zusätzliche Zeilen“.
- **Live-Vorschau** der generierten `.bat` für einen Beispielbenutzer.
- Platzhalter: `{sam}`, `{homeUnc}`, `{department}`.
- Generator-Ausgabe (Beispiel `lirion`):
  ```bat
  net use s: /del
  net use h: /del
  net use u: /del
  net use t: /del
  net use v: /del
  net use h: \\dc01\lirion$
  ```
  `net use X: /del` mit `/y` ergänzen, damit kein Prompt hängt (Verhalten mit bestehenden Skripten abgleichen).
- Encoding: ASCII/ANSI, CRLF.
- Abteilungsänderung am Template → optionaler Button „Skripte aller Benutzer dieser Abteilung neu generieren“ (mit Diff-Vorschau, Bestätigung).

### 4.5 Checklisten-Vorlagen

Manuelle Aufgaben, die nicht (oder noch nicht) automatisiert sind. Im UI pflegbar: hinzufügen, umsortieren, deaktivieren, Pflicht ja/nein. Geltungsbereich Global, pro Bereich oder pro Abteilung; beim Auftrag werden alle zutreffenden Vorlagen zu einer Liste zusammengeführt (Snapshot, spätere Vorlagenänderungen ändern laufende Aufträge nicht).

Startbestand Onboarding (Global, alle Pflicht):

1. SwissSign-Zertifikat (S/MIME) erstellen
2. ILIAS-Konto anlegen
3. Mail an HR
4. Einladung EDV-Einführung
5. MFA einrichten (1Password)
6. In Multifunktionsdrucker eintragen
7. Rechnerarbeitsplatz einrichten

Startbestand Offboarding (Vorschlag, mit IT abstimmen): SwissSign-Zertifikat widerrufen, ILIAS-Konto deaktivieren, 1Password-Zugang entziehen, aus Multifunktionsdrucker entfernen, Hardware zurücknehmen, Mail an HR.

Kandidaten für spätere Automatisierung (v2): „Mail an HR“ und „Einladung EDV-Einführung“ per Graph (Mail bzw. Kalendereinladung aus Vorlage).

## 5. Datenmodell (Kern)

- `Request` (Id, Typ, Eingabefelder, abgeleitete Werte, Status, CreatedBy, ApprovedBy, Timestamps)
- `RequestStep` (RequestId, StepKey, Status, Attempts, NextAttemptAt, LastError, OutputJson)
- `ChecklistItem` (RequestId, Titel, Beschreibung, Pflicht, Status `Open`/`Done`/`NotApplicable`, ErledigtVon, ErledigtAm, Notiz)
- `ChecklistTemplate` (Typ Onboarding/Offboarding, Geltungsbereich Global/Bereich/Abteilung, Titel, Beschreibung, Pflicht, Reihenfolge, aktiv)
- `AuditLog` (Zeit, Akteur, RequestId, StepKey, Aktion, Ergebnis, Details) — append-only
- `GlobalConfig`, `DepartmentConfig`, `ConfigHistory`

Request-Status: `Draft` → `PendingApproval` → `Approved` → `Running` → `Waiting` ↔ `Running` → `AwaitingChecklist` → `Completed` | `Failed` | `NeedsInput` | `Cancelled`

Step-Status: `Pending`, `Running`, `Waiting` (Vorbedingung nicht erfüllt, Retry geplant), `Done`, `Skipped`, `Failed`, `ManualTask`

## 6. Ausführungsmodell

- Worker pollt alle 60 s fällige Steps.
- Jeder Step ist **idempotent**: erst prüfen, ob Zielzustand schon erreicht ist (→ `Done`), sonst ausführen.
- Vorbedingung nicht erfüllt → `Waiting`, Backoff (1, 2, 5, 10, 15 min, dann alle 15 min), Timeout pro Step konfigurierbar (Default 24 h) → `Failed`.
- `Failed` blockiert abhängige Steps; Admin kann im UI „Retry“ oder „Als erledigt markieren“ (mit Begründung, landet im Audit-Log).
- Kein Step loggt Passwörter oder Tokens.
- Mehrere Aufträge laufen parallel und unabhängig; ein hängender Auftrag blockiert keine anderen.

### 6.1 Auftragsübersicht (Startseite)

- Tabelle aller **nicht abgeschlossenen** Aufträge: Name, Typ, Bereich, Abteilung, Datum, Status, Fortschritt (`x/y Steps`, `x/y Checkliste`), Tage bis Stichtag.
- Filter: Status, Typ, Bereich, Zeitraum; Umschalter „auch abgeschlossene anzeigen“.
- Hervorhebung: `Failed`/`NeedsInput` rot, Stichtag in ≤ 3 Tagen und nicht `Completed` orange.
- Detailansicht pro Auftrag: Steps mit Status/Fehler/Retry, Checkliste, Audit-Log.

### 6.2 Manuelle Checkliste

- Wird beim Anlegen des Auftrags aus den Vorlagen (§4.5) erzeugt und ist sofort sichtbar, abhakbar aber erst, wenn `AD.CreateUser` erledigt ist (vorher ausgegraut).
- Sind alle automatischen Steps `Done`/`Skipped`, wechselt der Auftrag nach `AwaitingChecklist`.
- `Completed` erst, wenn alle Pflichtpunkte `Done` oder `NotApplicable` sind. `NotApplicable` erfordert eine Notiz.
- Abhaken protokolliert Benutzer + Zeitpunkt im Audit-Log; Zurücksetzen möglich (ebenfalls protokolliert).

## 7. Onboarding-Steps

| Key | Aktion | Vorbedingung |
|---|---|---|
| `AD.CreateUser` | User in **Bereichs-OU** (§4.2) anlegen, **deaktiviert**; `givenName`, `sn`, `displayName`, `mail`, `userPrincipalName`, `department`, `company`, `manager`, `telephoneNumber`, `scriptPath = {sam}.bat`, `proxyAddresses` (beide Werte, mehrwertig), `extensionAttribute1 = Dr.` falls Titel; Startpasswort aus Auftrag, „Kennwort bei nächster Anmeldung ändern“ gesetzt. Idempotenz-Check vergleicht alle Attribute, nicht nur Existenz | Freigabe |
| `AD.Groups` | Gruppen der Abteilung zuweisen | `AD.CreateUser` |
| `Home.Folder` | `F:\Home\{sam}` auf DC01 anlegen; NTFS: User = Ändern, Admins/SYSTEM = Vollzugriff (an bestehendem Ordner verifizieren, siehe §11) | `AD.CreateUser` |
| `Home.Share` | `New-SmbShare -Name '{sam}$' -Path ... -ChangeAccess DOMAIN\{sam}` per CIM-Session auf DC01 (Share-Rechte an bestehendem Share verifizieren) | `Home.Folder` |
| `Logon.Script` | `.bat` aus Abteilungs-Template generieren, nach `\\dc03\NETLOGON\{sam}.bat` schreiben | `Home.Share` |
| `Sync.Delta` | `Invoke-Command -ComputerName CC01 { Start-ADSyncSyncCycle -PolicyType Delta }`; gMSA benötigt auf CC01 Mitgliedschaft in `ADSyncOperators` und WinRM-Zugriff; bei „busy“ → `Waiting` | AD-Steps |
| `Entra.WaitUser` | User per Graph über UPN auffindbar | `Sync.Delta` |
| `Entra.UsageLocation` | `usageLocation = DE` per Graph setzen (Pflicht vor jeder Lizenzzuweisung) | `Entra.WaitUser` |
| `Entra.AssignLicense` | `LicenseMode = Direct`: SKUs der Abteilung per Graph (`assignLicense`) zuweisen; `SkuPartNumbersIfPhone` nur, wenn eine Durchwahl angegeben ist; SKU-IDs über `subscribedSkus` auflösen; vorher prüfen, ob freie Lizenzen vorhanden sind, sonst `Failed` mit klarer Meldung. `LicenseMode = Group`: Step `Skipped`, Lizenzgruppe kommt über `AD.Groups` | `Entra.UsageLocation` |
| `Entra.WaitLicense` | Lizenz(en) aktiv, kein Fehlerstatus (`licenseAssignmentStates`) | `Entra.AssignLicense` |
| `EXO.WaitMailbox` | `Get-EXOMailbox -Identity $upn` erfolgreich | `Entra.WaitLicense` |
| `EXO.DisableNewOutlook` | `Set-CASMailbox -Identity $upn -OneWinNativeOutlookEnabled $false` | `EXO.WaitMailbox` |
| `EXO.SharedMailboxes` | je Eintrag `Add-MailboxPermission -AccessRights FullAccess -AutoMapping`, ggf. `Add-RecipientPermission -AccessRights SendAs` | `EXO.WaitMailbox` |
| `Teams.WaitUser` | `Get-CsOnlineUser $upn` liefert User mit Teams-Phone-Plan (kann Stunden dauern) | `Entra.WaitLicense`, nur wenn Durchwahl gesetzt |
| `Teams.Phone` | `Set-CsPhoneNumberAssignment -Identity $upn -PhoneNumber $e164 -PhoneNumberType DirectRouting` | `Teams.WaitUser` |
| `Teams.VoiceRouting` | `Grant-CsOnlineVoiceRoutingPolicy -Identity $upn -PolicyName INTStandard` | `Teams.Phone` |
| `Teams.Voicemail` | `Grant-CsOnlineVoicemailPolicy -Identity $upn -PolicyName "CleanControlling Personal Voicemail"`; `Set-CsOnlineVoicemailUserSettings -Identity $upn -VoicemailEnabled $true -PromptLanguage de-DE -DefaultGreetingPromptOverwrite ""` | `Teams.Phone`, Abteilung `Voicemail = true` |
| `Teams.Forwarding` | `Set-CsUserCallingSettings -Identity $upn -IsUnansweredEnabled $true -UnansweredDelay 00:00:20 -UnansweredTargetType singleTarget -UnansweredTarget CCVertrieb@...` | `Teams.Phone`, Abteilung `UnansweredForward.Enabled` |
| `AD.Enable` | Konto aktivieren | Eintrittsdatum erreicht (00:00 lokal oder konfigurierbar früher) |

Die bestehenden interaktiven Skripte (`Read-Host`) werden in parametrisierte, nicht-interaktive Step-Skripte überführt; Konstanten kommen aus der Konfiguration.

## 8. Offboarding-Steps (v2 – Entwurf, nicht umsetzen)

| Key | Aktion | Zeitpunkt |
|---|---|---|
| `AD.Disable` | Konto deaktivieren, Passwort zufällig zurücksetzen | Austrittsdatum |
| `Entra.RevokeSessions` | `Revoke-MgUserSignInSession` | nach Sync |
| `EXO.ConvertShared` | `Set-Mailbox -Type Shared`; Vorgesetzte(r) erhält FullAccess | vor Lizenzentzug |
| `EXO.AutoReply` | Abwesenheitsnotiz (Template konfigurierbar) | optional |
| `EXO.HideGal` | `msExchHideFromAddressLists` im AD (hybrid) bzw. passendes Attribut | — |
| `EXO.RemoveDelegations` | Freigabepostfach-Rechte entfernen | — |
| `Teams.RemovePhone` | `Remove-CsPhoneNumberAssignment -RemoveAll`; Durchwahl wird wieder frei | — |
| `AD.RemoveGroups` | alle Gruppen außer Domain Users entfernen (inkl. Lizenzgruppe) | nach `EXO.ConvertShared` |
| `Logon.Remove` | `.bat` in NETLOGON entfernen (vorher in Archiv kopieren) | — |
| `Home.Unshare` | Share `{sam}$` entfernen, Ordner bleibt | — |
| `Home.Archive` | Ordner archivieren/löschen | nach Aufbewahrungsfrist + Freigabe Vorgesetzte(r) |
| `AD.Move` | in Disabled-OU verschieben | zuletzt |
| `AD.Delete` | löschen | manuell, nach Frist |

## 9. Sicherheit / TISAX

- Startpasswort: im UI maskiert, in der DB nur verschlüsselt (DPAPI, Maschinenkontext des Worker-Hosts) bis `AD.CreateUser` erledigt ist, danach gelöscht; nie im Audit-Log, nie in Step-Output oder Fehlermeldungen; Übergabe an Prozess über stdin, nicht als Kommandozeilenparameter.
- ~~Vier-Augen: jeder Auftrag braucht Freigabe durch `ITAdmin`, Antragsteller ≠ Freigebender.~~ **Überholt durch DECISIONS A1** (Selbstfreigabe mit Pflicht-Begründung, konfigurierbar).
- gMSA mit **delegierten** Rechten: Benutzer anlegen/ändern nur in den konfigurierten OUs; Schreibrechte auf `F:\Home` und NETLOGON.
- **Risiko, bewusst dokumentieren**: SMB-Shares auf einem DC anlegen erfordert lokale Adminrechte auf dem DC; Schreibrechte auf NETLOGON erlauben Code-Ausführung bei jeder Anmeldung. Das Dienstkonto ist damit faktisch Tier 0 und muss so behandelt werden (Host-Härtung, keine interaktive Anmeldung, Monitoring).
  **Korrigiert (DECISIONS X5, Offen-Liste):** DC01 ist ein Fileserver (Member-Server), kein Domänencontroller (DCs: DC02, DC03, DC04). Home-Freigaben entstehen per JEA auf DC01; NETLOGON wird über einen eigenen JEA-Endpunkt auf DC03 geschrieben. Das Worker-gMSA ist auf keinem Server Admin.
- CC01 ist Entra-Connect-Server und damit Tier 0 (das `MSOL_`-Konto hat Replikationsrechte auf das AD). Der Worker darf nicht auf einem Host laufen, auf dem Nicht-Admins Code ausführen können (Web-Apps, CI-Runner).
- Audit-Log append-only, exportierbar (CSV), Aufbewahrung konfigurierbar.
- Zertifikat der App-Registrierung: Laufzeit ≤ 1 Jahr, Ablaufwarnung im UI.

## 10. Akzeptanzkriterien v1

1. Onboarding eines Testbenutzers in einer Test-OU läuft ohne manuellen Eingriff bis `Completed` (außer dokumentierten `ManualTask`s).
2. Erneutes Ausführen jedes Steps verursacht keine Fehler oder Duplikate.
3. Generierte `.bat` ist byte-identisch zur Vorschau im UI.
4. Kollision beim sAMAccountName führt zu `NeedsInput`, nicht zu einem falschen Konto.
5. Jede schreibende Aktion steht im Audit-Log.
6. Drei parallel angelegte Aufträge erscheinen in der Übersicht mit korrektem Fortschritt; ein `Failed`-Auftrag beeinflusst die anderen nicht.
7. Ein Auftrag mit offenen Pflicht-Checklistenpunkten bleibt in `AwaitingChecklist`, auch wenn alle automatischen Steps erledigt sind.

## 11. Offene Punkte

Für v1 (Onboarding):

- NetBIOS-Domänenname (→ `DomainNetBios`).
- SKUs pro Abteilung: Standard `SPB` (M365 Business Premium), mit Durchwahl zusätzlich `MCOEV` (Teams Phone Standard). Abweichungen je Abteilung noch prüfen.
- Übersicht freier Lizenzen im Auftragsformular anzeigen (frei = `PrepaidUnits.Enabled − ConsumedUnits`), Warnung bei 0.
- NTFS- und Share-Rechte eines bestehenden Home-Ordners auslesen (`Get-Acl`, `Get-SmbShareAccess`) und als Referenz übernehmen.
- Liste aller Abteilungen inkl. heutiger Logon-Skripte (aus NETLOGON auslesen und als Startkonfiguration importieren → Import-Funktion). *In Arbeit.*

Später:

- Umstellung auf gruppenbasierte Lizenzierung (`LicenseMode = Group`), erfordert Entra ID P1 (in M365 Business Premium enthalten). Vorher bestehende Direktzuweisungen migrieren.
- Offboarding (v2): Disabled-OU, Aufbewahrungsfristen für Home-Ordner und Postfächer.
- Abteilungswechsel als dritter Auftragstyp.
- Home-Ordner vom DC auf einen Fileserver mit einer gemeinsamen `Home$`-Freigabe migrieren, Logon-Skripte durch GPO-Laufwerkszuordnung ersetzen. Konfiguration (§4.1 `Home`, §4.4) ist so ausgelegt, dass das ohne Codeänderung möglich ist.
