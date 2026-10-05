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
| W8 | Fakes | Verzeichnis, Lizenzen und Passwortrichtlinie kommen bis Phase 4 aus `Integrations:FakeDataFile` (`src/Onboarding.Web/DevData/fake-directory.json`). |

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

## Offen für spätere Phasen

- „Skripte aller Benutzer dieser Abteilung neu generieren“ (§4.4): Phase 2 liefert die Diff-Vorschau; das Schreiben nach NETLOGON als Worker-Job folgt mit Phase 4 (es braucht den echten Datei-Executor).
- Unerwartete Executor-Exceptions landen mit ihrer Meldung in `LastError`. Für die PowerShell-Executors (Phase 4) muss sichergestellt sein, dass Fehlermeldungen keine Secrets enthalten (Passwort nur über stdin).
- Ablaufwarnung für das Zertifikat der App-Registrierung (§9): sobald der Worker die Zertifikatsdaten melden kann (Phase 3/4).
