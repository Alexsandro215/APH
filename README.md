# APH - Analyzer Poker Hands

APH is a Windows desktop analytics platform for poker hand histories. It parses local hand-history files, builds player and table statistics, generates visual dashboards, protects local access, backs up the analysis database, and restores it through Google Drive.

The project is built as a portfolio-grade WPF application with a strong focus on data visualization, local persistence, report generation, and a polished desktop user experience.

## Highlights

- Visual dashboard with global player stats, recent tables, accumulated winnings, tags, and table trend charts.
- Table analysis with filters, colored stat cells, BB results, chips/money results, and per-table KPIs.
- Data Villans module for villain profiles, recent opponents, known-card samples, and hero-vs-villain readings.
- Hero profile with global stats, positional performance, action EV, best/worst hands, top combos, and quick insights.
- Gain analysis with line and bar charts by table, street, position, action, bluff category, format, and blind level.
- Real-time session tooling for active table tracking and session report generation.
- PDF session reports and an architecture document for technical review.
- Local SQLite backup of imported hand histories, so the app can retain analysis even if original `.txt` files are deleted.
- Google Drive synchronization for uploading and restoring the local `aph.db` backup.
- Google account gate plus local password lock for protected desktop access.
- Multi-language UI support: Spanish, English, French, Portuguese, German, Russian, and Japanese.

## Technical Stack

- C# / .NET 8
- WPF / XAML
- SQLite local persistence
- Google Drive API integration
- Hand-history parsing and stat aggregation
- PDF report generation
- Theme and localization services
- Desktop-first UX with custom charts and visual shell

## Architecture

The detailed system architecture is included here:

[docs/APH-Architecture.pdf](docs/APH-Architecture.pdf)

The document explains the main layers of the application, including presentation, application services, parsing, analytics, persistence, Google Drive integration, security, and reporting.

## Security And Local Data

This repository intentionally does not include private credentials or user data.

Ignored local files include:

- `google_client_secret.json`
- `client_secret*.json`
- Google OAuth tokens
- `aph.db` and SQLite sidecar files
- local poker samples
- build outputs

To enable Google Drive sync on a local machine, create a Google OAuth Desktop client and place the downloaded credentials file as `google_client_secret.json` in the configured local credentials folder. Do not commit that file.

## Local Development

Requirements:

- Windows
- .NET 8 SDK
- Visual Studio or VS Code with C# tooling

Build:

```powershell
dotnet build Hud.App\Hud.App.csproj
```

Run:

```powershell
dotnet run --project Hud.App\Hud.App.csproj
```

If the app executable is already open and blocks the default build output, use an alternate output path:

```powershell
dotnet build Hud.App\Hud.App.csproj -p:BaseOutputPath=obj\local-check\
```

## Release

Current release target: `v1.0.0`

This version represents the first complete APH desktop release: visual dashboard, analytics windows, gain analysis, report generation, local backup, Google Drive restore, account-gated access, local password lock, and multi-language UI.

