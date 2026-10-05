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
| S8 | Request-Status-Ableitung | `RequestStatusEvaluator`: ein `Failed`-Step → `Failed`; alle Steps `Done`/`Skipped` → `AwaitingChecklist` bzw. `Completed`; sonst `Running` oder `Waiting` (Retry geplant oder `ManualTask` offen). Fehlende direkte Übergänge (z. B. `Failed` → `Completed`) laufen über Zwischenstatus und werden einzeln protokolliert. |

## Logon-Skript (§4.4)

| # | Thema | Entscheidung |
|---|---|---|
| L1 | Format | `net use s: /del /y` (Kleinbuchstaben, `/y` am Ende), dann Verbindungen, dann Zusatzzeilen. |
| L2 | Encoding | **Strikt ASCII**, CRLF. cmd.exe liest `.bat` in der OEM-Codepage (CP850), daher kein Windows-1252; jedes Zeichen > 0x7F → Fehler. |

## Sicherheit (§9)

| # | Thema | Entscheidung |
|---|---|---|
| P1 | Startpasswort | **Abweichung von „DPAPI“**: Das Web-UI läuft nicht auf dem Worker-Host (§9 Tier 0) und kann daher nicht mit dessen Maschinen-DPAPI verschlüsseln. Stattdessen verschlüsselt das Web mit dem öffentlichen Schlüssel eines Worker-Zertifikats (`ISecretEncryptor`), nur der Worker entschlüsselt mit dem nicht exportierbaren privaten Schlüssel (`ISecretDecryptor`). Thumbprint in der Konfiguration. Ciphertext wird nach `AD.CreateUser` gelöscht. |
| P2 | Concurrency | SQLite kennt keine generierte rowversion; Concurrency-Token (`Version`, long) wird in `SaveChanges` bei jedem Update selbst hochgezählt. |

## Offene Fragen (Phase 1 – mit vorläufiger Umsetzung)

1. **Telefonie-Steps**: Umgesetzt als „Durchwahl angegeben **und** Abteilung `Teams.AssignPhone = true`“. Was gilt, wenn eine Durchwahl angegeben ist, die Abteilung aber `AssignPhone = false` hat (Spec §7 nennt nur die Durchwahl)?
2. **Checkliste abhaken**: Umgesetzt nur durch `ITAdmin`. Sollen auch Requester (HR) Punkte wie „Mail an HR“ abhaken dürfen?
3. **`Sync.Delta`-Abhängigkeit „AD-Steps“**: Umgesetzt als `AD.CreateUser`, `AD.Groups`, `Home.Folder`, `Home.Share`, `Logon.Script`. `AD.Enable` (Eintrittsdatum) gehört bewusst nicht dazu, sonst würde die Cloud-Einrichtung bis zum Eintritt warten.
4. **Kollisionsprüfung bei „Snapshot aktualisieren“**: Nach `AD.CreateUser` existiert das eigene Konto im AD; die Prüfung muss dann das eigene Objekt ausnehmen (vgl. K4). Wird in Phase 2/4 umgesetzt.
