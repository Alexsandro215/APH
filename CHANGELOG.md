# Changelog

## [1.0.0] - 2026-05-13

### Added

- Production-ready WPF visual shell for the main APH dashboard.
- Global player stats with colored stat cells and player tags.
- Recent table tracking with BB results, chips/money results, best/worst table cards, and accumulated winnings chart.
- Table analysis, Data Villans, Hero Profile, Gain Analysis, Leak Analysis, Sessions, and Real-Time session windows.
- Gain analysis charts by table, street, position, action, bluff category, format, and blind level.
- Custom chart tooltips and localized explanatory insight cards.
- Local SQLite backup for imported hand histories and analysis continuity.
- Google Drive upload and restore flow for `aph.db`.
- Google sign-in gate and local password unlock flow.
- Logout flow that clears local DB, Google token, and local password.
- Protected PDF report support and configurable report folder.
- Poker room path configuration for multiple rooms.
- Multi-language UI support for Spanish, English, French, Portuguese, German, Russian, and Japanese.
- Academic architecture PDF under `docs/APH-Architecture.pdf`.

### Changed

- Reworked configuration sync controls into a clearer Google Drive backup/restore module.
- Refined the dashboard header, sidebar controls, and theme-aware visual details.
- Improved localization refresh behavior so dashboard labels update immediately after language changes.
- Kept poker terminology such as VPIP, PFR, 3Bet, CBet, BB, showdown, preflop, flop, turn, and river consistent across languages.

### Security

- Google credentials, OAuth tokens, local databases, user samples, and backups are excluded from source control.
- Local database restore is scoped to the connected Google account to avoid cross-account data leakage.

