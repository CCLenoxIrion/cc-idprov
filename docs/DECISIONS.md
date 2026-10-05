# Entscheidungen und Abweichungen von SPEC v0.4

Stand: Phase 1. Ergänzt/präzisiert `SPEC.md`; bei Widerspruch gilt dieses Dokument.

## Identität und Namensableitung (§2)

| # | Thema | Entscheidung |
|---|---|---|
| D1 | displayName | `Vorname Nachname`. Der Doktortitel geht nur in `extensionAttribute1`. |
| D2 | Transliteration | Explizite Map: ä→ae, ö→oe, ü→ue, ß→ss, ø→o, æ→ae, œ→oe, ł→l, đ→d (jeweils inkl. Großvarianten); danach Unicode-NFD und Entfernen der Combining Marks (é→e, ñ→n, …); Apostrophe werden entfernt. Bleibt danach etwas außerhalb `[a-z]` → `NeedsInput`. |
| D3 | Initial | Erster Buchstabe **nach** Transliteration: `Özlem` → `oezlem` → `o`, Mail `o.nachname@…`. |
| D4 | Doppelnamen | Bindestrich oder Leerzeichen in Vor- **oder** Nachname (`Anna-Lena`, `Müller-Lüdenscheidt`, `von der Leyen`) → keine automatische Ableitung, `NeedsInput`; ITAdmin vergibt sam und Mail manuell. |
| D5 | Länge sam | sam > 20 Zeichen → `NeedsInput`, **kein** Kürzen. |
| D6 | Validierung manueller Werte | sam: `[a-z0-9]`, keine Punkte, 1–20 Zeichen. Mail-Localpart: `[a-z0-9.-]`, kein `.`/`-` am Anfang/Ende, kein `..` (z. B. `m.mueller-luedenscheidt`). |
| D7 | proxyAddresses | Folgen der **tatsächlichen** Mail (auch wenn manuell geändert). Templates nutzen den Platzhalter `{mailLocal}`; Seed: `SMTP:{mailLocal}@cleancontrolling.de`, `smtp:{mailLocal}@cleancontrolling.com`. `MailPattern` mit `{firstInitial}.{lastName}` dient nur der Vorbelegung. |
| D8 | Telefon ohne Durchwahl | `telephoneNumber` und E.164-Nummer bleiben leer (Attribut nicht setzen). |
| D9 | Vorgesetzte(r) | Gespeichert als `objectGUID` (`ManagerObjectGuid`), DN nur zur Anzeige auflösen (DNs ändern sich beim Verschieben/Umbenennen). |

## Kollisionsprüfung

| # | Thema | Entscheidung |
|---|---|---|
| K1 | Umfang | sam, mail, UPN und proxyAddresses; gegen Verzeichnis **und** offene Aufträge in der DB. |
| K2 | proxyAddresses | Prüfung gegen **alle** Verzeichnisobjekte (User, Gruppen, Kontakte, Shared Mailboxes), nicht nur User. Interface `IDirectoryLookup.FindProxyAddressOwnersAsync` liefert die Objektklasse mit. |
| K3 | Zeitpunkte | Bei Anlage (Vorschau im Formular), erneut bei Freigabe, und (Phase 4) unmittelbar in `AD.CreateUser`. |
| K4 | Idempotenz `AD.CreateUser` (Phase 4) | Existiert ein Objekt mit gleicher sam, ist es nur dann „unseres“, wenn es zum Auftrag passt (Request-Id in einem Attribut oder Abgleich aller Attribute); sonst `NeedsInput`. |
| K5 | Auflösung | Kollision → `NeedsInput`, nie automatische Nummerierung. |
| K6 | Eigenes Konto | `AD.CreateUser` speichert die objectGUID sofort im Auftrag (`Request.DirectoryObjectGuid`); die Kollisionsprüfung nimmt dieses Objekt aus. Zusätzlich schreibt `AD.CreateUser` (Phase 4) die Request-Id in das AD-Attribut aus `GlobalConfig.RequestIdAttribute` (Seed: `extensionAttribute15`, derzeit bei keinem User belegt). Objekte mit dieser Request-Id gelten ebenfalls als eigenes Konto (Absturz zwischen Anlage und Speichern der GUID). Der Wert bleibt nach Abschluss stehen, damit sich Konto, Auftrag und Audit-Log zuordnen lassen. |

## Status und Ablauf (§5, §6)

| # | Thema | Entscheidung |
|---|---|---|
| S1 | `NeedsInput` → `Approved` | Ohne erneute Freigabe, **nur durch ITAdmin**, Audit mit altem/neuem Wert. War der Auftrag noch **nicht** freigegeben (z. B. Doppelname beim Einreichen), geht er zurück nach `PendingApproval`, damit das Vier-Augen-Prinzip erhalten bleibt. |
| S2 | ManualTask-Steps | Zählen für `AwaitingChecklist` erst, wenn ein Admin sie als erledigt markiert (→ `Done`). |
| S3 | Konfig-Snapshot | Bereichs-/Abteilungs-/Global-Konfig wird bei **Freigabe** eingefroren. ITAdmin-Aktion „Snapshot aktualisieren“ (protokolliert alt/neu) für laufende Aufträge. |
| S4 | Checkliste | Wird bei **Anlage** des Auftrags erzeugt (§6.2) – bewusst anderer Zeitpunkt als der Konfig-Snapshot. Reihenfolge: Global → Bereich → Abteilung, je `SortOrder`. |
| S5 | `AD.Enable` | Fällig ab Eintrittsdatum 00:00 in der konfigurierten Zeitzone (Europe/Berlin) minus globalem `EnableLeadTime`. Nie Server-Lokalzeit. |
| S6 | Bereiche | TecSa/Bio/Chemie nur als Seed; im UI erweiterbar. |
| S7 | „Snapshot aktualisieren“ | Plant nur Steps neu, die noch nicht gestartet wurden (Pending/Skipped ohne Versuch); erledigte Steps bleiben unverändert. |
| S9 | Steps nach Aktivierung | `Sync.DeltaAfterEnable` [AD.Enable, Sync.Delta] stößt direkt nach der Kontoaktivierung einen Delta-Sync an, `Entra.WaitEnabled` [Sync.DeltaAfterEnable, Entra.WaitUser] wartet auf `accountEnabled = true`. Grund: Die dynamische Gruppe APP-Intranet-Remote hängt an accountEnabled + Entra-ID-P1-Plan (in SPB); ohne Sync würde der Mitarbeiter am ersten Tag auf den regulären Sync-Zyklus warten. Damit ist ein Auftrag frühestens am Eintrittstag `Completed` (alle Steps zählen, §6.2). |
| S10 | Step-Status `NeedsInput` | Zielzustand widerspricht dem Auftrag (fremdes Konto mit gleicher sam, manuell geänderte Logon-Datei, Share mit anderem Pfad). Blockiert nur abhängige Steps, Auftrag → `NeedsInput`. Admin-Aktionen: Retry (erneut prüfen), „Überschreiben“ (`ForceStep`, mit Begründung), „Vorhandenes übernehmen“ (als erledigt markieren, mit Begründung), bei Identitätskonflikt sam/Mail neu vergeben (setzt den Step zurück). |
| S11 | Ausführung | Pro Auftrag höchstens ein Step gleichzeitig in `Running`; ein `Waiting`-Step blockiert andere fällige Steps nicht. Vor `NotBefore` ist ein Step nicht fällig (kein Claim, kein Versuch); die Step-Timeout-Uhr startet beim ersten echten Versuch. `Failed`/`NeedsInput` blockieren nur abhängige Steps, unabhängige laufen weiter. Ausführungs-Timeout pro Aufruf (Konfig `Worker:ExecutionTimeout`) → `Waiting` mit Backoff; unerwartete Exception → `Failed` (nur Typ und Meldung). |
| S12 | Audit bei Waiting | Audit nur bei Statuswechseln (Step gestartet, erstmals Waiting, abschließendes Ergebnis mit Anzahl Versuche). Einzelne Wiederholungen nur im Log (`ILogger`). |
| S13 | Absturz des Workers | Beim Start werden Steps in `Running` auf `Waiting` (sofort fällig) gesetzt und auditiert; Idempotenz macht die Wiederholung sicher. |
| S8 | Request-Status-Ableitung | `RequestStatusEvaluator`: ein `Failed`-Step → `Failed`; alle Steps `Done`/`Skipped` → `AwaitingChecklist` bzw. `Completed`; sonst `Running` oder `Waiting` (Retry geplant oder `ManualTask` offen). Fehlende direkte Übergänge (z. B. `Failed` → `Completed`) laufen über Zwischenstatus und werden einzeln protokolliert. |

## Telefonie und Checkliste

| # | Thema | Entscheidung |
|---|---|---|
| T1 | Auslöser Telefonie | Die **Durchwahl** ist der einzige Auslöser: Ist sie angegeben, laufen die Teams-Steps, `telephoneNumber` wird gesetzt und die Lizenzen aus `SkuPartNumbersIfPhone` (MCOEV) werden zugewiesen – unabhängig von der Abteilung. `Voicemail` und `UnansweredForward` bleiben Abteilungseinstellungen und greifen nur mit Durchwahl. |
| T2 | `PhoneExpected` | Abteilungsfeld (vormals `AssignPhone`), steuert nur Formular-Warnungen: `true` + keine Durchwahl → Warnung; `false` + Durchwahl → Warnung „Abteilung hat normalerweise keine Telefonie“. Nie ein Fehler. |
| C1 | Zuständigkeit Checkliste | Vorlagen und Punkte haben `Responsible` (IT \| HR, Default IT). Requester dürfen nur HR-Punkte abhaken/zurücksetzen, ITAdmin alle. Startbestand: alle IT. |

## Logon-Skript (§4.4)

| # | Thema | Entscheidung |
|---|---|---|
| L1 | Format | `net use s: /del /y` (Kleinbuchstaben, `/y` am Ende), dann Verbindungen, dann Zusatzzeilen. |
| L3 | Idempotenz über SHA-256 | Datei fehlt → schreiben; gleicher Hash → `Done`; anderer Hash → Step `NeedsInput` („manuell angelegt oder geändert“), Überschreiben nur nach Admin-Aktion „Überschreiben“. SHA-256 steht im Step-Output und im Audit-Log. |
| L2 | Encoding | **Strikt ASCII**, CRLF. cmd.exe liest `.bat` in der OEM-Codepage (CP850), daher kein Windows-1252; jedes Zeichen > 0x7F → Fehler. |

## Sicherheit (§9)

| # | Thema | Entscheidung |
|---|---|---|
| P1 | Startpasswort | **Abweichung von „DPAPI“**: Das Web-UI läuft nicht auf dem Worker-Host (§9 Tier 0) und kann daher nicht mit dessen Maschinen-DPAPI verschlüsseln. Stattdessen verschlüsselt das Web mit dem öffentlichen Schlüssel eines Worker-Zertifikats (`ISecretEncryptor`), nur der Worker entschlüsselt mit dem nicht exportierbaren privaten Schlüssel (`ISecretDecryptor`). Thumbprint in der Konfiguration. Ciphertext wird nach `AD.CreateUser` gelöscht. |
| P3 | Lebensdauer Startpasswort | Ciphertext wird gelöscht, sobald `AD.CreateUser` `Done` ist (auch per „als erledigt markieren“) oder der Auftrag `Cancelled` wird. Steht `AD.CreateUser` in `NeedsInput`, bleibt er erhalten. Löschung wird auditiert. |
| P2 | Concurrency | SQLite kennt keine generierte rowversion; Concurrency-Token (`Version`, long) wird in `SaveChanges` bei jedem Update selbst hochgezählt. |
| A1 | Freigabe / Vier-Augen | **Abweichung von SPEC §9 („Antragsteller ≠ Freigebender“), Grund: Ein-Personen-IT.** Global-Konfig `ApprovalPolicy` = `FourEyes` \| `SelfApprovalWithReason` (Seed und Standard für fehlende Werte: `SelfApprovalWithReason`). Bei `SelfApprovalWithReason` darf ein ITAdmin einen selbst angelegten Auftrag freigeben, muss aber eine Begründung eingeben (Pflicht, mindestens 10 Zeichen); sie wird als Audit „Request.SelfApproved“ festgehalten, der Auftrag trägt das Kennzeichen „Selbstfreigabe“ (Badge und Filter in der Übersicht, Begründung in der Detailansicht). Aufträge anderer Personen (z. B. HR) gibt ein ITAdmin ohne Begründung frei. Requester dürfen in keinem Modus freigeben. Die Regel gilt mit der Konfiguration zum Zeitpunkt der Freigabe. NeedsInput auflösen und „Snapshot aktualisieren“ bleiben ITAdmin-only ohne Vier-Augen-Bedingung. Die Selbstfreigabe wird damit nicht verhindert, sondern nachvollziehbar gemacht. |


## Web (Phase 2)

| # | Thema | Entscheidung |
|---|---|---|
| W1 | Dev-Anmeldung | `Authentication:Mode = Dev` (Cookie, Benutzer aus `DevAuth:Users`) ist nur in der Umgebung `Development` erlaubt; sonst bricht der Start ab. Produktion: Entra ID (Microsoft.Identity.Web), App-Rollen `Requester`/`ITAdmin` im Claim `roles`. |
| W2 | Startpasswort-Verschlüsselung (zu P1) | Umschlagverfahren: AES-256-GCM für das Passwort, der AES-Schlüssel wird mit RSA-OAEP-SHA256 (öffentlicher Schlüssel des Worker-Zertifikats, `LocalMachine\My`, Thumbprint aus der Global-Konfig) verpackt. Der Web-Host braucht nur das Zertifikat ohne privaten Schlüssel. Für die Entwicklung: `SecretProtection:Mode = DevelopmentPem` (lokale Schlüsseldatei, nur `Development`). |
| W3 | E-Mail bei Doppelnamen | Bleibt Pflichtfeld (§2), wird aber nicht vorbelegt; HR trägt einen Vorschlag ein, ITAdmin bestätigt bzw. ändert ihn beim Auflösen von `NeedsInput`. |
| W4 | Validierung im Formular | Harte Fehler: Pflichtfelder, Mail-Format, Durchwahl-Format und **Durchwahl bereits vergeben** (§2). Alle anderen Ableitungsprobleme und Kollisionen werden nur angezeigt; der Auftrag kann eingereicht werden und landet in `NeedsInput`. |
| W5 | Freigabe | Prüft Passwortrichtlinie (Domänenrichtlinie über `IPasswordPolicyProvider`), friert die aktuelle Konfiguration ein, prüft Ableitung und Kollisionen erneut. Schlägt die Prüfung fehl → `NeedsInput`, das Passwort wird **nicht** gespeichert und bei der späteren Freigabe neu eingegeben. |
| W6 | Byte-Identität Logon-Skript (AK 3) | Vorschau und Datei kommen aus demselben Generator; die Vorschau zeigt zusätzlich die SHA-256 der Bytes. Der Worker soll in Phase 3/4 dieselbe Prüfsumme für die geschriebene Datei protokollieren. |
| W7 | Sichtbarkeit | Requester und ITAdmin sehen alle Aufträge; CSV-Export des Audit-Logs nur ITAdmin. |
| W9 | Requester-Ansicht | Requester sehen alle Aufträge (HR arbeitet als Team), in der Detailansicht aber nur Status, Fortschritt, Stichtag und Checkliste (HR-Punkte abhakbar). Keine Step-Fehler, kein Audit-Log, keine technischen Werte – der Service liefert dafür ein eigenes DTO (`RequestSummary`). Volle Ansicht nur ITAdmin; Requester laden Volldetails nur für eigene Entwürfe (Bearbeiten). |
| W10 | Eintritt in der Vergangenheit | Erlaubt (Nachmeldungen), mit Warnung und Pflicht-Bestätigung im Formular; der Service lehnt ohne Bestätigung ab und auditiert die Bestätigung. `AD.Enable` ist dann sofort fällig. |
| W8 | Fakes | Im Modus `Fake` kommen Verzeichnis, Lizenzen und Passwortrichtlinie aus `Integrations:FakeDataFile` (`src/Onboarding.Web/DevData/fake-directory.json`). Umschaltung pro Lese-Adapter siehe X6. |

## Betrieb (Phase 3)

| # | Thema | Entscheidung |
|---|---|---|
| B1 | Deployment | Web und Worker laufen auf **demselben Host**, die SQLite-Datei liegt lokal, nie auf einer Netzwerkfreigabe. Relative `Data Source` wird gegen das Content-Root des jeweiligen Hosts aufgelöst. |
| B2 | SQLite | WAL-Modus und `busy_timeout` (Standard 10 s) werden beim Öffnen jeder Verbindung gesetzt (`SqlitePragmaInterceptor`) – die einzige SQLite-spezifische Stelle neben der Provider-Registrierung. DbContext und Abfragen bleiben providerneutral (kein Raw-SQL), damit ein späterer Umstieg auf SQL Server nur die Konfiguration betrifft. |
| B3 | Single-Instance | Der Worker hält eine exklusive Lock-Datei `<db>.worker.lock` (unter Windows zusätzlich Mutex `Global\Onboarding.Worker`); ein zweiter Start bricht mit Meldung und Exit-Code 1 ab. Doppel-Claims sind zusätzlich über Concurrency-Token ausgeschlossen. |
| B4 | Fake-Welt | Die Fake-Executors simulieren AD, Dateien, Entra, EXO und Teams im Speicher des Workers (Konfig `FakeWorld`: Verzögerungen, freie Lizenzen, vorhandene Konten, Fehlerinjektion). Der Zustand geht bei Worker-Neustart verloren und ist vom Fake-Verzeichnis des Web-Prozesses (Kollisionsprüfung) getrennt. |

## Testpunkte Phase 4

| # | Testpunkt |
|---|---|
| X1 | `Sync.Delta` hängt bewusst **nicht** von `AD.Enable` ab; die Cloud-Einrichtung läuft mit deaktiviertem Konto. Prüfen, ob Lizenzzuweisung, Mailbox-Provisionierung und `Set-CsPhoneNumberAssignment` bei `accountEnabled = false` in Entra funktionieren. |
| X2 | Vor dem ersten Schreiben prüfen, dass `extensionAttribute15` (K6) weiterhin bei keinem Objekt belegt ist. |

## Integrationen (Phase 4a On-Prem, 4b Cloud)

| # | Thema | Entscheidung |
|---|---|---|
| X3 | Skript-Vertrag | Je Step ein PowerShell-7-Skript `scripts/steps/<StepKey>.ps1`, gemeinsame Logik in `scripts/common/`. Der Worker startet `pwsh -NoProfile -NonInteractive -File`; **Eingabe nur JSON über stdin** (`StepScriptInput`: Identität, Manager-/Objekt-GUID, Gruppen, JEA-Endpunktnamen, Anmeldeskript als Base64 + SHA-256, `dryRun`, `force`; Startpasswort nur bei `AD.CreateUser`). **Keine Pfade und keine Freigabenamen** – die kennt nur der JEA-Endpunkt. **Ausgabe genau ein JSON-Objekt auf stdout** `{status, code, reason, output, directoryObjectGuid, dryRun, plannedActions}` (`code` siehe X10). Jeder Fehlerpfad liefert `failed` mit bereinigter Meldung (Exception-Typ → fester Text), nie rohe Exception-Texte oder Parameterwerte. |
| X4 | Dry-Run | Modus pro Step-Gruppe: `Integrations:Steps:OnPrem` / `:Cloud` = `Fake` \| `DryRun` \| `Real` (Cloud bis 4b nur `Fake`; ungültige Werte brechen den Start ab). Im Dry-Run prüft das Skript lesend und meldet geplante Änderungen; der Step geht nach `NeedsInput` mit „Dry-Run – würde: …“. Ist der Zielzustand bereits erreicht (keine geplanten Aktionen), ist der Step `Done`. Nach Umschalten auf `Real` wird der Step per „Erneut versuchen“ ausgeführt. Läuft On-Prem nicht als Fake, legen die Cloud-Fakes ihre Benutzer selbst an (`DetachedFromOnPrem`). |
| X5 | JEA-Endpunkte auf DC01 und CC01 | Datei-, Freigabe- und Sync-Operationen laufen ausschließlich über eingeschränkte JEA-Funktionen: `CC.Onboarding` auf DC01 (`New-OnbHomeFolder`, `New-OnbHomeShare`, `Set-OnbLogonScript`) und `CC.Onboarding.Sync` auf CC01 (nur `Start-OnbDeltaSync`, parameterlos). Das Worker-gMSA ist weder lokaler Admin auf DC01 noch Mitglied von `ADSyncOperators`, sondern nur in den `RoleDefinitions` eingetragen; die Rechte hat das Run-As-Konto des Endpunkts (Varianten in DEPLOYMENT.md §4.2). Die Endpunkte validieren selbst: sam `^[a-z0-9]{1,20}$`, Pfade nur aus der festen Endpunkt-Konfiguration `OnboardingEndpoint.psd1` (kanonisiert, Traversal-Schutz), Anmeldeskript nur ASCII mit CRLF und passender SHA-256; idempotent (gleicher Hash → nichts; abweichender Inhalt ohne `force` oder Freigabe mit anderem Pfad → `needsInput`). **Die Endpunkt-Konfiguration muss zur Global-Konfiguration passen** (Home-Stamm, Freigabemuster, NetBIOS-Domäne, Skript-Dateiname); Abgleich ist Deployment-Schritt. Aufruf per implizitem Remoting (`New-PSSession -ConfigurationName`, `Import-PSSession -Prefix Remote`, lokaler Aufruf, Session im `finally` schließen), weil JEA-Sitzungen in `NoLanguage` keine Variablen in Scriptblöcken erlauben – **zu verifizieren**. |
| X6 | Web-Identität | Das Web liest AD unter einem eigenen, nur lesenden Dienstkonto (LDAP über System.DirectoryServices.Protocols, Negotiate, Signing + Sealing, Paging). Modus pro Lese-Adapter: `Integrations:Read:Directory` = `Fake` \| `Real` (Kollisionen, Vorgesetzte, OUs, Kennwortrichtlinie), `Integrations:Read:Graph` = `Fake` \| `Real` (Lizenzen, Cloud-only-Objekte in der Kollisionsprüfung) mit eigener, rein lesender App-Registrierung (X16). Die alten Schlüssel `Integrations:Mode` und `Integrations:Read:Licenses` brechen den Start mit Hinweis ab. Vorgesetztensuche zeigt nur aktivierte Konten. |
| X7 | stderr und Exit-Codes | stderr eines Skripts geht nur gekürzt (2000 Zeichen) und mit entferntem Startpasswort ins Worker-Log, nie in `LastError`, Step-Output oder Audit. Exit-Code ≠ 0 oder kein gültiges JSON → `Failed` mit generischer Meldung („Details im Worker-Log“). Zeitüberschreitung (`Integrations:Scripts:Timeout`) → Prozessbaum wird beendet, Step `Waiting` (Backoff). Auch `reason` aus dem JSON wird vor dem Speichern um das Passwort bereinigt. |
| X8 | Bestehendes eigenes Konto | `AD.CreateUser` erkennt das eigene Konto an der gespeicherten objectGUID oder am Request-Id-Attribut (K6). Abweichende Attribute (inkl. `proxyAddresses`, Manager) werden korrigiert, eine abweichende OU führt zu `needsInput`. Das Passwort wird **nie** neu gesetzt. Fremdes Konto mit gleicher sam/UPN/Mail/Proxy-Adresse → `needsInput`. |
| X9 | LDAP-Filter | Skripte fragen AD nur mit `-Identity` oder `-LDAPFilter` ab, Werte RFC-4515-escaped (`ConvertTo-LdapFilterValue`); nie String-Interpolation in `-Filter`. Im Web genauso (`LdapFilter.Escape`, GUIDs als Byte-Escape, Attributnamen aus der Konfiguration werden validiert). **Durchwahl-Kollision:** AD speichert `telephoneNumber` als freien Text. Gesucht wird `(telephoneNumber=*<Durchwahl>*)`, verglichen werden clientseitig nur die Ziffern mit der E.164-Nummer; `+…`, `00…`, nationale Schreibweise mit führender 0 und `(0)` werden erkannt (`PhoneNumberMatcher`). Die Durchwahl steht dafür zusätzlich im abgeleiteten Ergebnis (`DerivedIdentity.Extension`). |
| X10 | Grund-Codes | Jedes Ergebnis außer `done` trägt einen festen Code (kebab-case, `^[a-z][a-z0-9-]{1,48}$`); Tests und UI prüfen den Code, nie den Text. Der Code wird in `RequestStep.ReasonCode` gespeichert (Code des letzten Ergebnisses, `null` nach Done), im Audit als „Code: …“ vermerkt und in der Detailansicht (ITAdmin) angezeigt. Fehlt er, setzen Skript-Modul und Executor `unspecified`; ein ungültiger wird zu `invalid-code` (nie Freitext). Katalog siehe unten. |
| X11 | Graph-/OData-Escaping | Skripte sprechen Graph nur über `Invoke-MgGraphRequest` an. UPNs stehen nur URL-escaped im Pfad (`/users/{upn}`); Filterwerte nur als OData-Literal (`'` verdoppelt) und URL-encodiert (`ConvertTo-ODataLiteral` bzw. `OData.Literal`/`OData.Query` im Web); nie String-Interpolation. Graph-Fehler: 404 → nicht vorhanden, 429/5xx → `waiting` (`graph-throttled`), 401/403 → `graph-access-denied`, sonst `graph-error` – jeweils nur mit HTTP-Status, nie mit der Antwort. |
| X12 | Cloud-Anmeldung | App-only mit Zertifikat aus `LocalMachine\My` des Worker-Hosts. Tenant-ID, App-ID, Thumbprint und Exchange-Organisation stehen in der **Worker-appsettings** (`Integrations:Cloud`), nicht im Admin-UI; es sind nur Bezeichner, kein Geheimnis. Validierung beim Start. Cloud `DryRun`/`Real` setzt On-Prem `DryRun`/`Real` voraus (der Fake-AD-Benutzer erreicht Entra nie). Je Step-Lauf eine Verbindung, Trennung im `finally`; Verbindungsfunktionen geben nichts aus. Ein fehlgeschlagener Connect wird `cloud-auth-failed` ohne Modulmeldung. Teams-/EXO-Steps haben eigene Timeouts (`Integrations:Scripts:TimeoutOverrides`, längster Präfix gewinnt; `Worker:ExecutionTimeout` muss größer sein). |
| X13 | ManualTask-Schalter | `Integrations:Cloud:ManualSteps` (nur `Teams.Voicemail`, `Teams.Forwarding` erlaubt): Der Step verbindet sich nicht und liefert eine ManualTask mit fertigem Befehl (UPN und Konfigurationswerte, PowerShell-Literale escaped). Umschalten nach der Verifikation der app-only-Unterstützung (SPEC §3). |
| X14 | Entra-Eigentumsprüfung | Cloud-Steps arbeiten nur auf dem Entra-Benutzer des Auftrags: primär `onPremisesSecurityIdentifier` = objectSid des AD-Kontos (unabhängig vom Source Anchor; `AD.CreateUser` liefert die SID, gespeichert in `Request.DirectoryObjectSid`). Nur ohne gespeicherte SID: `onPremisesImmutableId` = Base64(objectGUID) – **zu verifizieren**, hängt am Source Anchor. Sonst `needsInput` (`foreign-cloud-account`). Ungültige SIDs werden verworfen, eine abweichende SID für denselben Auftrag abgelehnt. |
| X15 | Zertifikats-Ablaufwarnung | Der Worker prüft beim Start und täglich Passwort- und Cloud-Zertifikat (Ablauf, vorhanden) und schreibt das Ergebnis in die Tabelle `CertificateStatus` (Betriebszustand, kein Audit). Das Web prüft sein Graph-Lese-Zertifikat selbst. ITAdmins sehen ein Banner bei Ablauf innerhalb `Web:CertificateWarningDays` (Standard 30; rot ab 7 Tagen oder abgelaufen/fehlend) und wenn die letzte Worker-Prüfung älter als 48 h ist. |
| X16 | Graph-Lese-Adapter | Eigene, rein lesende App-Registrierung des Web (MSAL, Zertifikat). Lizenzen aus `subscribedSkus` (frei = Enabled − Consumed). Kollisionsprüfung = AD (LDAP) **plus** Cloud-only-Objekte aus Entra (`/users`, `/groups`, `/contacts`); synchronisierte Entra-Objekte werden übersprungen, weil das AD sie bereits liefert – so hängt nichts am Source Anchor. Filter auf `proxyAddresses` nutzen `ConsistencyLevel: eventual` + `$count=true` und fragen beide Präfixe (`SMTP:`/`smtp:`) ab, weil die Groß-/Kleinschreibung des Vergleichs nicht verifiziert ist. **Einschränkung:** Kontakte werden nur über `mail` gesucht (ob `/contacts` den Lambda-Filter unterstützt, ist zu verifizieren). sAMAccountName und Telefon bleiben AD-only; die Teams-Nummer prüft `Teams.Phone` zur Laufzeit. |

### Code-Katalog (X10)

| Code | Herkunft | Bedeutung |
|---|---|---|
| `foreign-account` | AD.CreateUser, AD.Groups, AD.Enable | Konto mit dieser sam gehört nicht zum Auftrag (weder objectGUID noch Request-Id-Attribut) |
| `ou-mismatch` | AD.CreateUser | eigenes Konto liegt in einer anderen OU als konfiguriert |
| `address-in-use` | AD.CreateUser | UPN/Mail/Proxy-Adresse gehört einem anderen Objekt |
| `cn-in-use` | AD.CreateUser | CN (Anzeigename) in der Ziel-OU belegt |
| `missing-password` | AD.CreateUser | kein Startpasswort in der Eingabe |
| `config-missing` | AD.CreateUser | RequestIdAttribute nicht konfiguriert |
| `account-not-found` | AD.Groups, AD.Enable | Konto des Auftrags nicht gefunden |
| `group-not-found` | AD.Groups | konfigurierte Gruppe existiert nicht |
| `missing-logon-content` | Logon.Script | Anmeldeskript fehlt in der Eingabe |
| `invalid-sam` | Skripte, JEA | sam entspricht nicht `^[a-z0-9]{1,20}$` |
| `invalid-input` | Skripte | Eingabe leer/kein JSON, sonstige sichere Ablehnung |
| `invalid-base64`, `invalid-hash-format`, `hash-mismatch`, `non-ascii`, `line-endings`, `control-chars`, `too-large` | JEA `Set-OnbLogonScript` | Inhalt abgelehnt |
| `logon-script-modified` | JEA `Set-OnbLogonScript` | Datei existiert mit anderem Inhalt, kein `force` |
| `account-not-resolvable` | JEA `New-OnbHomeFolder` | Konto auf dem DC noch nicht bekannt (Replikation) → waiting |
| `home-folder-missing`, `home-folder-error`, `invalid-share-name`, `share-path-mismatch`, `home-share-error`, `logon-script-error` | JEA DC01 | Ordner/Freigabe/Datei |
| `sync-busy`, `sync-error` | JEA CC01 | Sync läuft bereits / Start fehlgeschlagen |
| `jea-unreachable`, `jea-invalid-result` | Skripte | Endpunkt nicht erreichbar / ungültige Antwort |
| `password-policy`, `ad-object-not-found`, `ad-object-exists`, `dc-unreachable`, `ad-error`, `access-denied`, `command-missing`, `unexpected-error` | Skripte (Exception-Typ) | bereinigte Fehler |
| `script-exit-code`, `invalid-output`, `script-timeout`, `dry-run`, `unspecified`, `invalid-code` | Script-Executor (C#) | Prozess-/Vertragsfehler, Dry-Run-Bericht |
| `execution-timeout`, `unexpected-error`, `step-timeout` | Worker / Workflow | Ausführungs- bzw. Step-Timeout, unerwartete Exception |
| `entra-user-pending`, `entra-user-not-found`, `foreign-cloud-account`, `entra-enable-pending` | Entra-Steps | Benutzer noch nicht synchronisiert / nicht vorhanden / gehört nicht zum Auftrag (X14) / noch deaktiviert |
| `license-mode-group`, `sku-not-found`, `service-plan-not-found`, `usage-location-missing`, `no-free-license`, `license-pending`, `license-error`, `license-not-assigned` | Entra-Lizenz-Steps | Lizenzzuweisung |
| `mailbox-pending`, `mailbox-not-found`, `shared-mailbox-not-found` | EXO-Steps | Postfach |
| `no-extension`, `teams-user-pending`, `teams-user-not-found`, `number-in-use`, `voicemail-disabled`, `forwarding-disabled`, `manual-step` | Teams-Steps | Telefonie, ManualTask (X13) |
| `cloud-auth-failed`, `graph-throttled`, `graph-access-denied`, `graph-error`, `exo-error`, `teams-error` | Cloud-Hilfsfunktionen | Anmeldung und bereinigte Modulfehler (X11/X12) |
| `injected-fault` und fachliche Codes der Fake-Welt (`entra-user-pending`, `license-pending`, `no-free-license`, …) | Fakes | simulierte Zustände |

## Verworfene Alternativen

| Alternative | Warum verworfen |
|---|---|
| Home-Ordner, Freigabe und Anmeldeskript direkt vom Worker über Admin-Share bzw. UNC-Pfad auf NETLOGON | Worker-gMSA bräuchte Schreibrechte auf F:\Home und NETLOGON bzw. lokale Admin-Rechte auf einem DC; Pfade kämen vom Worker. Ersetzt durch X5. |
| Freigabe/ACL per CIM-Session oder `Invoke-Command` mit Admin-Rechten | Gleiche Rechteausweitung, keine Validierung auf dem Zielsystem. Ersetzt durch X5. |
| Worker-gMSA in `ADSyncOperators` auf CC01 | Erlaubt mehr als den Delta-Sync. Ersetzt durch den Sync-Endpunkt (X5). |
| Entra-Kollisionen über `onPremisesImmutableId` mit AD-Treffern deduplizieren | Hängt am Source Anchor von Entra Connect; ein eigenes Konto könnte als Kollision erscheinen. Ersetzt durch „nur Cloud-only-Objekte aus Entra“ (X16). |
| `Exchange.ManageAsApp` mit Verzeichnisrolle „Exchange Administrator“ | Ganzer Tenant; ersetzt durch RBAC for Applications mit Management Scope (DEPLOYMENT.md §11.2). |

## Offen für spätere Phasen

- „Skripte aller Benutzer dieser Abteilung neu generieren“ (§4.4): Phase 2 liefert die Diff-Vorschau; das Schreiben nach NETLOGON als Worker-Job (über `Set-OnbLogonScript -Force`, X5) ist noch offen.
