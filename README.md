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

Automated tests (hand parsing, results and stats, with hand-written sample hands in `HandReader.Tests/TestData`):

```powershell
dotnet test HandReader.Tests\HandReader.Tests.csproj
```

HUD calibration with real hands: `APH.Calibration` reads hands in PHH format (for example the
public [Dataset of Poker Hand Histories](https://doi.org/10.5281/zenodo.10796885), CC BY 4.0)
and writes `Hud.App/Data/population_baselines.json`, the population percentiles APH uses to
color HUD stats and classify opponents. To recalibrate:

```powershell
dotnet run --project APH.Calibration -c Release -- D:\APH_Datos\phh\data Hud.App\Data\population_baselines.json
```

Preflop charts: the reference charts are `Hud.App/Data/APH_Reference_100bb_6max.csv` (13x13 grids,
also used by the Trainer). The draft charts for vs limp, squeeze and heads-up are
`Hud.App/Data/APH_Rangos_Borrador_6max.txt`, one line per spot in range notation
(`SITUACION | HEROE | RIVALES | SUBIR | PAGAR`, e.g. `77+, ATs+, A5s-A2s, T9s-54s, resto`), so they
can be reviewed and edited without touching code. The tests check that every spot has its chart
and that the notation parses.

Simulator: `Hud.App/Services/Simulator` holds the hand engine (`SimEngine`), the machine profiles
(`SimBot`), the range reading and decision review (`SimReview`), the skills (`SimSession`) and the
self-play measurement (`SimTraining`). `SimulatorTests` plays tens of thousands of machine-only hands
and fails if a profile leaves its statistical band, so re-run the tests after changing a profile.

All-in solver: `Hud.App/Services/Solver/PushFoldSolver.cs` computes the heads-up push/fold Nash
equilibrium (CFR+ over the 169 starting hands) from `Hud.App/Data/APH_Equidad_HU_169.txt`, the
all-in equity of every hand against every hand. To regenerate that table (about 7 minutes):

```powershell
dotnet run --project APH.Calibration -c Release -- --equidad Hud.App\Data\APH_Equidad_HU_169.txt 1000000
```

`PushFoldTests` check the equities against known values and that no perfect opponent can exploit
the solution.

GTO solver: `Hud.App/Services/Solver/PostflopGtoSolver.cs` solves one heads-up postflop street
(the river, or the turn with every river) with discounted CFR over both ranges. The Modo
Entrenador live decision (play money only) uses it on the turn and river; `GtoSolverTests` check
a known equilibrium and the exploitability. Run APH from a Release build: the solver is several
times slower in Debug.

Flop: `FlopGtoSolver.cs` solves the flop as an approximate two-street game (flop and turn betting,
the river dealt without betting) with a precomputed equity matrix per turn card. The live
decision solves it in about 5 seconds, or reads it instantly from the flop library
(`FlopLibrary.cs`): the 1,755 suit-distinct flops for LJ, HJ, CO, BTN and SB opens called by the
big blind, built in the background from Real time ("Biblioteca GTO del flop", about 6 hours on
4 threads) into `%APPDATA%\APH\gto-flop`. `FlopGtoTests` cover it; `FlopGtoBenchTests` (skipped
by default) measure speed and convergence.

If the app executable is already open and blocks the default build output, use an alternate output path:

```powershell
dotnet build Hud.App\Hud.App.csproj -p:BaseOutputPath=obj\local-check\
```

## Release

Current release target: `v1.0.0`

This version represents the first complete APH desktop release: visual dashboard, analytics windows, gain analysis, report generation, local backup, Google Drive restore, account-gated access, local password lock, and multi-language UI.

