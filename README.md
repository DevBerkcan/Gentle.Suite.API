# GentleSuite - All-in-One Agentur-Management System

Vollwertiges CRM + Buchhaltung für Werbeagenturen. GoBD-konform, Self-Hosted.

## Features

### CRM & Kunden
- Kunden anlegen mit Kontakten, Adressen, Steuerdaten
- Auto-Onboarding mit konfigurierbarem Workflow (10 Schritte, 40+ Tasks)
- Willkommens-E-Mail automatisch
- Kundennotizen (Passwörter, Zugänge, intern)

### Angebote (Quotes)
- Angebote aus Vorlagen erstellen (z.B. "Website + Unlimited Care")
- Einmalige + monatliche Positionen
- Per E-Mail versenden mit **digitalem Unterschrift-Link**
- Kunde kann online ansehen, Unterschrift zeichnen, annehmen/ablehnen
- Bei Annahme: automatisch Projekt + Rechnung erstellt
- PDF-Export mit professionellem Design

### Rechnungen (GoBD-konform)
- Fortlaufende, unveränderbare Rechnungsnummern
- Status: Draft → Final → Sent → Paid / Overdue / Cancelled
- **Nach Finalisierung: nur Storno möglich** (§14 UStG)
- Automatische MwSt-Berechnung (19%, 7%, 0%)
- Reverse Charge & Kleinunternehmerregelung
- **Hash-Chain** für Revisionssicherheit
- 10 Jahre Aufbewahrungspflicht-Flag
- 3-stufiges Mahnwesen (automatisch via Hangfire)
- PDF mit allen Pflichtangaben

### Abonnements & Wartungsverträge
- Pläne: GentleSuite Care (79€/Mon), Unlimited Care (199€/Mon)
- **Automatische monatliche Rechnungsgenerierung** via Hangfire
- Fair-Use Regelungen & SLA-Definitionen
- Pause/Kündigung mit Logging

### Buchhaltung
- Ausgaben erfassen mit Kategorien (SKR03 vorbereitet)
- Vorsteuer-Berechnung
- Buchungssätze (Soll/Haben)
- EÜR-Modus aktiv, doppelte Buchführung vorbereitet
- Umsatzsteuer-Voranmeldung Berechnung
- **DATEV-Export** (CSV)

### Dashboard
- KPIs: Aktive Kunden, Offene Angebote, Überfällige Rechnungen, MRR
- Finanzen: Umsatz, Forderungen, Steuer-Rücklage-Empfehlung

## Tech Stack

| Layer | Technologie |
|-------|-------------|
| Backend | .NET 8, ASP.NET Core Web API, Clean Architecture |
| Auth | ASP.NET Identity + JWT |
| Database | PostgreSQL + EF Core |
| Background Jobs | Hangfire |
| Email | SMTP + Fluid Templates + MailKit + MailHog |
| PDF | QuestPDF |
| Frontend | Next.js 14 + TypeScript + Tailwind CSS |
| Containerisierung | Docker Compose |

## Schnellstart

```bash
# 1. Starten
docker-compose up -d

# 2. Öffnen
# Frontend: http://localhost:3000
# API + Swagger: http://localhost:5000/swagger
# Hangfire Dashboard: http://localhost:5000/hangfire
# MailHog (E-Mails): http://localhost:8025
```

## Login-Daten

| E-Mail | Passwort | Rolle |
|--------|----------|-------|
| admin@gentlesuite.local | Password123! | Admin |
| pm@gentlesuite.local | Password123! | ProjectManager |

## GoBD-Konformität

- **Revisionssicherheit**: SHA256 Hash-Chain über alle finalisierten Rechnungen
- **Unveränderbarkeit**: Finalisierte Dokumente können nicht editiert werden
- **Storno statt Löschung**: Storno-Rechnungen mit negativen Beträgen
- **Audit Trail**: Automatisches Logging aller Änderungen (CreatedBy, UpdatedBy)
- **10-Jahres-Aufbewahrung**: RetentionUntil-Flag auf allen Belegen
- **Pflichtangaben §14 UStG**: Vollständig im PDF implementiert

## Firmeneinstellungen

Die Stammdaten werden beim ersten Start geseedet:
- **Firma**: Gentle Webdesign UG (haftungsbeschränkt)
- **Adresse**: Musterstr. 1, 42103 Wuppertal
- **Steuernr./USt-IdNr.**: In Settings anpassbar
- **Bankverbindung**: In Settings anpassbar

Änderbar über: `GET/PUT /api/settings`

## API-Endpunkte

| Endpoint | Beschreibung |
|----------|-------------|
| POST /api/auth/login | JWT Login |
| GET/POST /api/customers | Kunden CRUD |
| GET/POST /api/quotes | Angebote CRUD |
| POST /api/quotes/{id}/send | Angebot per Mail versenden |
| GET/POST /api/approval/{token} | Angebot online annehmen (öffentlich) |
| GET/POST /api/invoices | Rechnungen CRUD |
| POST /api/invoices/{id}/finalize | Rechnung finalisieren (GoBD) |
| POST /api/invoices/{id}/payment | Zahlung erfassen |
| POST /api/invoices/{id}/cancel | Storno erstellen |
| GET/POST /api/expenses | Ausgaben |
| GET/POST /api/subscriptions | Abos verwalten |
| GET /api/dashboard/kpis | Dashboard KPIs |
| GET /api/dashboard/finance | Finanzdashboard |

## Seed-Daten

- **10 Service-Kategorien** mit 30+ Leistungen
- **2 Angebots-Vorlagen**: "Website + Unlimited Care", "Website + Care"
- **2 Abo-Pläne**: Care (79€), Unlimited Care (199€)
- **7 Ausgaben-Kategorien** (SKR03)
- **11 Konten** (Kontenrahmen SKR03)
- **7 E-Mail-Templates** (Welcome, Angebot, Rechnung, 3× Mahnung, Erinnerung)
- **2 Legal-Text-Blöcke** (Fair-Use, SLA)
- **1 Demo-Kunde** mit Kontakt

## Projektstruktur

```
gentlesuite/
├── src/
│   ├── GentleSuite.Domain/          # Entities, Enums, Interfaces
│   ├── GentleSuite.Application/     # DTOs, Service Interfaces, Validators
│   ├── GentleSuite.Infrastructure/  # DbContext, Services, PDF, Email, Jobs
│   └── GentleSuite.API/            # Controllers, Auth, Swagger
├── frontend/                        # Next.js 14 App
├── docker/                          # Dockerfiles
├── docker-compose.yml
└── GentleSuite.sln
```
