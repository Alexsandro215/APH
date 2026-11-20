# Changelog

## [Unreleased]

### Added

- When APH sees open PokerStars tables and Tiempo real is closed, a small always-on-top notice
  asks whether to start it ("Arrancar Tiempo real", "Ahora no", "No volver a preguntar"). It does
  not take the focus, sits in the screen corner that covers the fewest tables, asks once per batch
  of tables, closes by itself after a minute, and never starts Tiempo real on its own. It can be
  turned off in Configuración > Tiempo real.

- Interface overhaul. Pages enter with a short fade and slide, the menu highlight fades in on
  hover and its marker grows when a page is chosen, the big dashboard numbers count up to their
  new value, charts draw from left to right after loading, bar lists grow from their axis, and
  the session performance window counts its tiles up and draws its curve the first time.
- Notices that come and go (toasts) at the bottom right of the main window for informational
  messages (hands loaded, settings saved); questions and errors stay in dialogs.
- The main window dims while a dialog is open on top of it (Configuración, room paths...).
- Loading screens show APH's spade beating inside a spinning ring instead of a bare progress
  bar, on the dashboard while hands load and on every page that loads in the background.
- Configuración redesigned with sections (Apariencia, Idioma, Informes, Respaldo): themes in a
  grid with a preview of each one in its own colors and a check on the chosen one, an
  Animaciones option (Normal, Reducidas, Apagadas) and an interface size option (90 %, 100 %,
  110 %, 125 %) that scales the main window at once. Languages show their real names
  (Español, Français, Português).
- Eight new themes: Royal Flush, Monaco Night, Vegas Neon, Sakura Night, Deep Ocean, Sunset
  Strip, Aurora Boreal and Alto contraste (pure black, white text and strong yellow for big
  screens).

- Trainer mode (`Modo Entrenador`) for coaching sessions on PokerStars play-money cash tables:
  live screen card reader that learns the user's deck from each finished hand, live preflop plan
  per scenario, equity vs. a typical range, outs and pot-odds limits, and post-hand grading of
  every student decision with an explanation (APH preflop charts per scenario, estimated villain
  range, pot odds and villain stats).
- The trainer refuses real-money and tournament tables, both by window title and by hand history.
- Automatic reader calibration: the trainer finds the board and the hero cards on the table by
  itself (and recalibrates if the table is resized), tuned on the real PokerStars 4-color deck.
- Multiway equity simulation for postflop grading (all opponents' ranges dealt at once).
- Live decision on the student's turn: APH reads the PokerStars action buttons, the pot and the
  player plates (Windows OCR) and shows what to do and why, with sizes capped to the hero's
  stack. The student always clicks; APH never acts on the table. After the hand, each graded
  decision shows the live recommendation and whether the student followed it.
- The trainer binds the exact hand history of the table and the hero's account, follows the
  new file when PokerStars starts another day, and never analyzes hands from another table.
- Multi-table grading ("Corregir todas"): the trainer follows every open play-money No-Limit
  Hold'em table at once and grades each decision when the hand ends, using only the hand
  histories (no screen capture or OCR). The decision list shows the table of each decision and
  can be filtered by table or to mistakes only; each table has its own score. One table can still
  be read live at the same time.

### Changed

- The flop and turn GTO solvers walk each possible turn or river card in parallel on all CPU
  threads but two, and add the results in the same order as before, so strategies are identical
  to the single-threaded ones. Measured on a Ryzen 5 5600G with six tables open: about 4 times
  more iterations per second (BTN vs BB on Ks 7d 2c: 11 to 47 per second). The live flop budget
  drops from 5 s to 2 s and the turn budget from 4.5 s to 2 s, still with more iterations than
  before.
- Opening Tiempo real while it is already open brings the open window to the front instead of
  starting a second one (two at once put two HUDs on every table).
- Simulator range chart is larger and readable (bigger hand labels, wider review column), and a
  click on it opens it large over the table (click or Esc to close).
- Real-time table range chart has a size option in Tiempo real (Chica, Normal, Grande); Normal is
  now larger than before and its texts grow with it. Chica keeps the previous size.
- Sesiones shows real-money and play-money sessions together, each grouped on its own and
  marked "Real" or "Ficticio"; before, only the mode chosen on the dashboard (CASH or PLAY) was
  listed, so a play-money session was missing while the dashboard was on CASH. Reports for a
  session use only the tables of that money type.
- The list of real-time PDF reports shows times in local time (hand histories are in UTC), adds
  money type, hands and result columns, and shows each session once: when the same session was
  generated several times, the newest PDF opens and the row says how many copies there are.
  "Última sesión" in the top bar is the most recent session played, in local time.
- Spanish texts across the app now carry their accents (Configuración, Análisis, sesión,
  Menú, ratón, línea...), and the settings and backup descriptions no longer call Google Drive
  backup a "future module".
- The bottom of the side menu is compact (Configuración plus three labeled icon buttons), so
  the whole menu fits without a scroll bar; big dashboard numbers shrink instead of being cut
  on narrow windows; the Configuración footer message wraps instead of overlapping the buttons.
- Live decisions and post-hand grading share the same rules, including the new price-driven
  preflop spots (all-ins, short all-in raises, calls that put the hero all-in, tiny prices).
- Pot odds use the real price when the hero is covered: what the hero can call and the pot they
  can win, not the full bet.
- Ranges facing several raises use a premium order (AA, KK, QQ, AKs...) instead of equity against
  a random hand.
- Opponent ranges are narrowed street by street with the action read on each player plate.
- The live decision tracks what each player has put in, from the stacks on the plates: the real
  pot when the hero is covered, the order of the preflop raises (3bet/4bet scenarios and the
  preflop aggressor) and who actually called the last raise.
- Live advice and post-hand grading build villain preflop ranges with the same function.
- The preflop hand ranking is a precomputed equity table instead of a short simulation at
  startup, so simulation noise no longer ranks offsuit hands above their suited versions.
- Only one trainer window can be open; the score summary counts every decision of the session.
- Single-window layout: a labeled side menu (Main, Analysis, Live) replaces the icon rail, and the
  analysis tools (hero profile, tables, my tables, gain, villains, leaks, sessions) open as pages
  inside the main window instead of separate windows. Details (a table, a villain, a leak list)
  open on top with a "Back" button (also mouse back button and Alt+Left). Real time and Trainer
  mode still open in their own windows so they can sit next to the poker tables. Page headers
  move to a shared top bar with the page summary.
- At-a-glance charts: diverging bars (green won, red lost, centered on zero) show the result by
  table, against each villain, and every leak breakdown (position, action, pot type, hand, board
  texture, session) plus EV by action in the hero profile. The tables page adds tables won out of
  total, total result and best/worst table; the villains page adds opponent types. Hover a bar
  for its details; click it to open that table, villain or list of hands. Win/loss colors were
  checked for color-blind separation against the dark background.
- New dashboard: hero card with tags and CASH/PLAY switch, four large indicators, ten colored stat
  tiles, a larger gain chart with best/worst table, and a recent-tables list with result badges
  (double-click a table to open its hands).
- Visual refresh: the default "Classic Poker" palette is now green felt; buttons, text boxes,
  check boxes, drop-downs, tabs and date boxes follow the theme (no more stock Windows controls
  or the accent stripe on top of buttons); primary actions use a filled accent button; window
  title bars take the palette colors; the HUD and the main cards follow the selected palette
  instead of fixed navy blue; stat and range-matrix colors are softer with the same meaning.
- Analysis windows (tables, gain, my tables, villains, villain detail, hero profile, leaks,
  session leaks) load hand histories in the background with a "Loading hands..." overlay instead
  of freezing the screen. The global combo winrate in Leak Review is computed once and cached.
- Errors are written to `%APPDATA%\APH\logs` and unexpected UI errors show a message instead of
  closing the app.
- Side-bar buttons have accessible names, so Windows voice control and screen readers can use them.
- Signing out keeps a local safety copy and uploads the Drive backup before deleting local data;
  signing in with a different Google account asks first and keeps a safety copy.

### Fixed

- Zoom hands (`PokerStars Zoom Hand #`), home games and the older `PokerStars Game #` header
  were ignored completely: only `PokerStars Hand #` was recognized. All four readers now share
  one header rule.
- Euro and pound tables: amounts with `€`/`£` were not read, so results on PokerStars.es and
  similar sites came out as 0. Table results now show the table's own currency.
- Checked against public PokerStars sample hands (cash, Zoom, 9/10-max, heads-up, side pots,
  run it twice, antes, tournament with bounty): every consistent hand balances to the rake.

### Added (preflop charts)

- "Tablas preflop" page (side menu, under LIVE): what to do with every hand by situation (open,
  vs raise, vs 3bet, vs 4bet, vs 5bet), the player's position and the raiser's position, as a
  13x13 grid colored raise / call / fold with the share of hands of each action. Same charts the
  Trainer uses (`Data/APH_Reference_100bb_6max.csv`, 6-max, 100 bb).
- Hand lookup: type a hand (AJo, 77, KTs; without s/o both are shown) or click a cell to see
  what to do with it from every position.
- Floating window: a compact copy of the charts that stays on top of the PokerStars tables.
  It is a static reference: it never reads the table or the cards; the player picks the spot.
- Chart tests: every position pair has its chart, only raise/call/fold codes, AA never folds,
  72o never opens, earlier positions open fewer hands, hand-text parsing.
- Draft charts for the spots the reference CSV lacked: vs limp (isolate / over-limp / check from
  the big blind), squeeze, and heads-up (open, vs raise, vs 3bet, vs 4bet). They live in
  `Data/APH_Rangos_Borrador_6max.txt` in range notation (`77+, ATs+, A5s-A2s, T9s-54s, resto`)
  so the coach can review and edit them; the page marks them "Borrador: revisar". Half of the
  students' first preflop decisions were vs limpers, which had no chart.
- Raise sizes under every chart (open, iso vs limpers, 3bet in/out of position, squeeze, 4bet,
  heads-up).
- "Tú contra la tabla": for the chart on screen, what the loaded players actually did in that
  spot (raise/call/fold % vs the chart), how often they matched it and the hands they played
  differently, with a player filter.

### Added (preflop practice)

- "Práctica preflop" page: topics (open from each position, big blind defense, vs raise, vs
  limp, squeeze, vs 3bet, vs 4bet/5bet, heads-up, a mix weighted like the player's real spots,
  the player's own hands, and a review of past mistakes) and four levels. Difficulty comes from
  the chart itself: easy hands are far from the edge of the range, hard and expert hands sit on
  it; expert also gives 10 seconds per answer and shows rare spots more often.
- Every answer is explained: how much the chart plays in that spot, how the same hand is played
  from other positions or against other raisers, the closest similar hand played differently,
  the raise size, a population fact from the 21.6 million calibration hands, and for real
  hands what the player actually did. Keys F / C / R and Enter; progress and mistakes are
  saved in `%APPDATA%\APH\practica.json`.

### Added (simulator vs the machine)

- "Simulador" page: full No-Limit Hold'em hands (preflop to river, 100 bb) against the machine,
  heads-up or at a 6-max table, with blinds, minimum raises, all-ins, side pots and showdown.
- Six machine profiles (nit, TAG, LAG, maniac, calling station, fish). The TAG plays the APH
  charts; the others use "top X%" ranges and postflop frequencies by hand strength.
- Profiles are "trained" by self-play: 30,000 6-max hands and 20,000 heads-up hands are played
  machine vs machine and every profile must land in its band (6-max: nit VPIP 13 / PFR 9.7,
  TAG 20.5 / 14, LAG 29.5 / 22.6, maniac 55.9 / 42.6, calling station 42 / 3.9, fish 40.4 / 6,
  plus the order of 3bet %, aggression, WTSD and fold to c-bet). The tests fail if a change
  breaks a profile.
- Exact range reading: each machine action multiplies every possible hand by the probability
  that the profile would have played it that way, so the review shows the machine's real range
  at each decision (heat map, with its real hand marked). The real hand is always inside the
  read range (checked in tests).
- Review of every hero decision: equity vs that range, pot odds, how often the machine folds to
  each bet size, and the approximate value of each option vs the best one (one street); preflop
  spots without an all-in are graded with the APH charts. Recommended sizes go from 1/3 pot to
  pot, all-in only when the stack is short compared with the pot.
- Beginner-friendly review: each decision first says what was better and why, in plain words
  ("you won 19 times out of 100 and calling needed 31"; "with that bet the machine folded 54 times
  out of 100"; for preflop, why the hand is too weak or strong enough), plus the average cost of
  the mistake. The numbers (range, equity, pot odds, value of each option, range heat map) sit
  behind a "Technical details" switch (key T), off by default and remembered in `simulador.json`.
- Thirteen skills: fold strong hands to the nit, catch the maniac's bluffs, value vs the calling
  station, steal and defend blinds, flop c-bet, draws and pot odds, free heads-up with a chosen
  profile, a play-money-like table, vs limpers, squeeze and 3bet, reading nits at a full table,
  a tough table and a random table. Results per skill are saved in `%APPDATA%\APH\simulador.json`.
- Tests now reference the app project (`net8.0-windows`); run them with
  `-p:BaseOutputPath=obj/test-out/` while APH is open.

### Added (all-in solver)

- "Solver all-in" page: the exact heads-up push/fold equilibrium (Nash) for any stack from 1 to
  25 bb, with or without ante. The button can only go all-in or fold, the big blind can only call
  or fold. Two views: what to do with every hand at the chosen stack (mixed hands drawn split), and
  the classic chart with the highest stack each hand is played with. Picking a hand shows, for both
  seats, how much each play wins, how often the big blind folds, the equity when called and the
  equity the big blind needs to call.
- `Data/APH_Equidad_HU_169.txt`: all-in equity of every hand against every hand (169 x 169),
  1,000,000 deals per pair spread over every concrete card combination, with the APH hand
  evaluator. `APH.Calibration --equidad <output> [deals]` regenerates it.
- The solver (CFR+ over the 169 hands, weighted by the possible card combinations of each pair)
  stops when a perfect opponent could not win more than 0.00001 bb per hand; only truly
  indifferent hands keep a mixed frequency. At 10 bb the button goes all-in with 58% of hands and
  the big blind calls with 37%, as in the published Nash charts.
- New practice topic "All-in o retirarse (pocas ciegas)": heads-up spots from 2 to 20 bb as the
  button or the big blind, graded by the solver; the level is how much a mistake costs (one big
  blind or more is easy, under 0.3 bb is hard). Mistakes are reviewed at the same stack.

### Changed (Mis Tablas)

- "Situación" filter (open, vs limp, vs raise, squeeze, vs 3bet, vs 4bet), read with the same
  logic as the preflop charts. With "Todas" each hand still counts once (its first preflop
  decision); with a situation, the preflop shows the decisions in that spot (including the second
  decision, e.g. vs the 3bet after opening) and the later streets show the hands that went through
  it.
- "Comparar con la tabla" (on by default): a corner marks the preflop cells where you played
  differently from the APH chart; the tooltip, the selected-cell panel and the new "Tabla APH"
  column of the exact hands say what the chart does there. A free check is never counted as a
  difference.
- Legend for the cell colors (and for the "Ganancia bb" scale), the active street is highlighted,
  and cells are easier to read: the color of what you did most, a bottom bar with the mix of
  actions, black or white text by contrast, the hand count in the corner, and fainter cells with
  fewer than 5 hands.
- With no cell selected, the bottom panel shows a summary of the street: hands, average result,
  how often you played, how often you matched the chart, the hands most different from it and the
  hands you win and lose the most with. Clicking the selected cell again returns to it.
- The "Mesa" filter is now called "Formato" (it filters 6-max/3-max), actions are shown in
  Spanish, the filter chips use the theme colors, and every cell has a name for voice control.

### Added (training solver in "Decisión en vivo", play money only)

- Modo Entrenador's live decision now works like a training solver. It is still gated to play-money
  cash tables (the session blocks real money by window title and hand history) and the student
  always clicks.
- Short stacks, two players: as small blind with everyone folded and 15 bb or less, the advice
  is the exact push/fold Nash equilibrium (the "Solver all-in"); as big blind against the small
  blind's all-in, up to 25 bb, the Nash calling range.
- Postflop: `TrainerSolver` values every option on this street against each opponent's estimated
  range (check or bet 1/3, 1/2, 2/3, pot and short all-in; fold, call or raise to 3x and all-in).
  Each part of the range folds by its strength and the bet size (air 55/60/75% to 1/3, 1/2 and
  pot, weak 28/35/55%, medium 8/11/20%, strong never), adjusted by the opponent's fold to c-bet,
  calling station or nit profile; when called, the hand is played against what remains. The
  advice is the option that wins the most, with the others (always including check or call) in
  the details, a "casi igual" note when two are close and a plain reason (value, semi-bluff,
  fold equity or price). Without a readable pot it falls back to the previous rules.
- Post-hand grading runs the same solver and adds its numbers; a postflop decision that is
  among the best by the solver (within 0.3 bb or 5% of the pot) is never graded as a mistake, so
  following the live advice is never marked wrong. `TrainerSolverTests` cover push/fold,
  calling, the fold model, value betting, river folds and the live advice.

### Added (room rhythm: how much people really fold)

- `TrainerRoomRhythm` ("ritmo de la sala") learns from the hand histories of the last 30 days how
  often everyone folds to a postflop bet or raise (any player's, since whoever answers can't see
  the bettor's cards), by bet or raise, street, opponents who can fold (1, 2, 3+), whether they
  had all checked, and size; each cell shrinks toward its parent (30 bets of weight). Only players
  dealt into the hand count (sitting-out seats made folds look impossible), and a hand copied in
  two folders counts once. Play money and real money have separate tables and never mix; a table
  is used only from 400 bets, otherwise the fixed fold table stays.
- The training solver (`TrainerSolver.Postflop`, new `room` argument) uses it: the room says how
  many fold and the estimated range says who (every fold chance moves together in logit until
  the chance that all fold matches the room), so when called the range left is stronger. The
  extra folding comes from air, not pairs: with the room a hand folds at most 97% (air), 85%
  (weak), 45% (medium) and never if strong (`RoomFoldCap`). Of the river callers who showed their
  hand in play money (367 on 18/19-11), more than half had a pair or two pair; without the cap a
  straight with 3 opponents "only got called by better" and checked. Live advice uses the
  play-money table (it only exists on play-money tables) and says so in the details; post-hand
  grading uses the table of the hand's money type. Heads-up spots without a known leak still use
  the GTO solvers, which don't use fold chances.
- Play-money limit fix (`PlayMoneyAggression`): on a dangerous board (straight or flush possible)
  a hand with no pair (and no strong draw heads-up before the river) can no longer bet; that
  branch used to allow 35% of the pot, and with the room's higher folds AQ ace-high bet the river
  on 5-2-9-6-4.
- Why: on 19-11 the fixed table said everyone folds 18% of the time to the hero's 264 postflop
  bets and they folded 33% (river 14% vs 50%, 3+ opponents 3% vs 22%), worse than always guessing
  the average. Learned only from the 18-11 hands, the room predicted the 19-11 bets better (log
  loss 0.60 vs 0.78, Brier 0.209 vs 0.238). Replaying the 360 hands of 19-11 where the live advice
  used the training solver, with the room learned from 18-11 (3,264 bets) and both versions run at
  the same time (villain profiles come from `villain-history.json`, which the open APH keeps
  updating): on 137 hero bets log loss 0.85 -> 0.68 and Brier 0.263 -> 0.231; 64 of 904 postflop
  advices change (28 bet sizes, 27 bet -> check, mostly multiway one-pair hands on wet boards, 8
  check -> bet, 1 call -> raise). Table and opponent layers on top of the room did not
  improve that sample, so they are not used yet. `TrainerRoomRhythmTests` cover who counts as
  facing the bet, separate money types, the solver reaching the room's fold rate (also multiway)
  and staying unchanged without data.

### Added (session performance window in Tiempo real)

- `SessionPerformanceWindow`: a pop-up like the usual poker trackers that opens by itself with the
  first graded hand of a Tiempo real session (and from the new "Rendimiento" button), stays on top
  without taking focus from the tables and refreshes on every graded hand and every minute. It
  shows hands, time and tables; won, won without all-in luck and the luck itself; bb/100 with and
  without luck; the hand-by-hand curve (solid: won, dashed: without luck); how the decisions go (%
  without mistakes, advice followed and the decision traffic light); the mistakes you repeat the
  most with how to avoid them; the costliest mistakes without luck; your stats with the APH traffic
  light; and what to focus on now. If you close it, it does not reopen by itself that session.
- Redesigned as a dashboard for a separate big screen: big figure tiles (won, without luck, luck,
  bb/100 with and without luck, decisions without mistakes, advice followed), a full-width curve
  with a bb scale, and cards in three, two or one columns depending on the width. It remembers
  where it was left (another monitor, size, maximized) and has an "always on top" switch; a saved
  spot on a monitor that is gone is ignored.
- Last hands recap: every hand of the session is graded by how it was played and what it left
  without all-in luck (`RealTimeSessionReportService.QualityOf`): very bad (a mistake that cost 20
  bb or more), bad (any mistake, even if it won), excellent (all correct and won 20 bb or more),
  good (no mistakes and won), standard (no mistakes and did not win, like folding in time) or not
  graded. The card shows how many of each, how many hands had good or bad all-in luck (5 bb or
  more) with the bb, and the latest hands worth telling (folds under 2 bb are counted but not
  listed) with time, cards, position, table, result, grade, luck and why. The luck card lists the
  biggest all-ins: cards against cards, street, equity, result and luck.
- `RealTimeSessionReportService.BuildLiveSummary`: the same calculation as the end-of-session PDF
  (last continuous stretch of the linked tables), returned as `LiveSessionSummary`.
  `BuildMistakePatterns` groups the graded mistakes by what you did and what was right, before or
  after the flop ("Pagas cuando tocaba no ir"), with the count, the bb lost in those hands without
  luck, example hands and a tip; same-action size mistakes are not a pattern. The report reads the
  hand histories without locking them (PokerStars keeps writing). `SessionPerformanceTests` cover
  the grouping and a two-hand summary; the tests that switch the review-log folder run in one
  collection so they never overlap.

### Changed (a player's type is a guess until 100 hands, and no bluffs until then)

- Measured how often the type tag is right with few hands, on 1,788 real-money PokerStars NL25
  6-max players with 1,000+ hands (their tag from all hands as the truth): exact tag 36% with 30
  hands, 58% with 100, 82% with 500; the family (caller / loose-aggressive / tight / regular) 68%,
  81% and 91%. So the HUD and the report keep the "?" until 100 hands (`PlayerProfileClassifier.
  SureHands`), not 30, and the legend says so.
- Live advice: the leaks that call for bluffing ("nit", "se retira mucho", and with them the Farol
  note) need 100 hands (`TrainerVillainProfile.KnownHands`); until the type is known the advice plays
  tight and for value. Value leaks (pagador, maníaco, trampero) still apply from 40 hands, since a
  wrong read there costs little.

### Added (player type in the report tables, with its legend)

- The player tables of the session report ("Rivales de la sesión" and every table in "Mesa por
  mesa") have a "Tipo" column with the same tag and color as the table HUD (Fish, Maniac, TAG,
  LAG, Nit, Tight, Loose pasivo, Pasivo, Regular; "?" with 15-29 hands, "Poca muestra" under 15).
  The HUD and the report share one rule (`PlayerProfileClassifier.Tag` / `ColorOf`), so they never
  disagree.
- Under the rivals table, "Cómo leer los tipos" explains each tag with the limits actually in use
  (calibrated from the population file), numbered in the order they are checked, in two aligned
  columns with same-width chips and wrapped text. The page lists as many rivals as fit (up to 20)
  above both legends. `PlayerTypeTagTests` cover the tag rules.
- Maniac now means very loose AND aggressive: VPIP at the Maniac limit with a LAG PFR or an AF at
  the population median or more, or a very high AF while loose. Very loose players who do not push
  (86/12 with AF 0.5 on 20-11) are "Loose pasivo" (callers), and a high 3Bet alone no longer makes
  a Maniac: the hero was tagged Maniac at a table for 3-betting 13% in 107 hands. The HUD, the
  dashboard and the report all use this rule.

### Changed (passivity judged by whether you bet your strong hands)

- New measure from the hand histories, for play and real money: after the flop, with nothing to
  call and a strong hand (fewer than 5% of holdings beat it then), how often the hero bet
  (`RealTimeSessionReportService.StrongValueSpots`, `HeroStatInput.StrongValue`). The session report
  and the performance window show it ("con mano fuerte y nada que pagar apostaste el 79%").
- Play money: a low AF/AFq (and a very low CBet or WWSF) is no longer "a mejorar" when you bet your
  strong hands at least 75% of the time (12+ spots): it comes from not bluffing, which is the play-
  money plan. If you check your strong hands too much, that is what gets flagged; without enough
  spots a low AF is only "por ajustar". Why: on 20-11 the report said "very passive, bet more" while
  the advice was followed 97%; 2,876 live postflop advices of 18-20/11 showed APH checks only 9% of
  very strong and 7% of strong hands with nothing to call (raises 20 of 25 near-nut hands facing a
  bet); the low AF came from the 1,339 weak spots where it does not bluff.
- Real money keeps flagging a low AF as passive (thin value bets and semi-bluffs matter there); the
  advice now says whether your strong hands are already bet so the leak is the rest.
  `HeroStatCoachTests` cover both money types.

### Added (range box on the table: win % and the range behind the advice)

- On the hero's turn, with "Solver en la mesa" on, a box at the bottom left of the table (the black
  area under the left seat) explains the advice (`LiveDecision.RangeView`, `LiveRangeView`): the
  hero's chance to win the hand and a 13x13 range grid with rank letters.
  - Preflop it is the hero's range: the APH chart of the exact scenario (open, vs raise, squeeze,
    vs 3bet...) with raises in orange, calls in blue and the hero's hand outlined, plus "Tu mano
    AKo: sube"; without a chart, the top % the advice uses. Win % is vs any hand when opening and
    vs the raiser's estimated range (same top % as the grading) when facing a raise. Price spots
    (all-ins, oversized raises) show the pusher's range instead, since that is what decides them.
  - Postflop it is the range the solver gives the bettor (or the only opponent): each hand green
    when the hero's made hand beats it now, red when it loses, brighter the more likely. Win % is
    the same equity as the details, vs all opponents at once; it is computed before the GTO step
    so the box also shows on GTO decisions.
  - `TrainerRangeViewTests` cover the open spot and the flop range.

### Fixed (live advice with other table themes)

- After switching PokerStars to a table theme with a green felt on a black background, the live
  advice never appeared: the big action buttons are a dark red gradient (~80,0,0 to ~160,20,0) and
  `LiveTableReader.IsButtonRed` only accepted the bright red of the old theme, so the button bar
  measured 14% (check/bet) or 34% (fold/call/raise) red against the 35% needed and the hero's turn
  was never seen. Both reds are accepted now (card backs still are not). A 45 s probe on the six
  open tables then saw 29 turns and read every button. `HeroTurnTests` cover both themes.

### Added (GTO on the turn and river, play money only)

- `Services/Solver/PostflopGtoSolver.cs`: heads-up GTO solver for one postflop street: the river,
  or the turn with every river card (chance node, 44 runouts per pair of hands). Vector CFR with
  discounting (DCFR, alpha 1.5, beta 0, average weighted by t^2) over both full ranges, with
  O(n) showdowns (hands sorted by strength with blocker correction). Tree: check or bet 33%,
  75% and all-in; raise to 3x then all-in (in the turn tree raises are all-in only and the rivers
  have 75% and all-in). The street's real actions are put into the tree with their real sizes
  and the answer is the strategy at the node they lead to.
- Measured: river 600 iterations in 0.3 s at 0.03% of the pot exploitability; turn 120
  iterations in about 5 s at 0.6% of the pot (optimized build). The classic polar river game
  gives its known equilibrium (bet all value, one bluff per two value bets, call 50%).
- "Decisión en vivo": on the turn and river against one opponent, the advice is the GTO play
  (both ranges at the start of the street from the APH charts and each player's line; pot and
  effective stack from the table). It shows the mix of the equilibrium and what each option
  wins. The push/fold Nash advice is also marked GTO. Following a GTO play shown live is never
  graded as a mistake.
- On the table, "Solver en la mesa (solo dinero ficticio)" always shows what to do, and the
  box title says where it comes from: "GTO" (flop, turn and river heads-up, push/fold), "TABLA
  APH · preflop" (rest of preflop) or "SOLVER · contra su rango" (multiway pots and very wide
  ranges). A first version showed only GTO with a grey note elsewhere; the user missed the advice
  in those spots. `GtoSolverTests` cover the polar game, river and turn exploitability and the
  live GTO advice.

### Added (GTO on the flop and flop library, play money only)

- `Services/Solver/FlopGtoSolver.cs`: heads-up GTO for the flop, as an approximate two-street
  game: flop bets 33% and 75% (raise to 3x; all-in only when it is at most 3.5 times the pot),
  turn bet 75% (all-in raise only up to 2 times the pot), and the river is dealt without betting
  (showdown with every river). Same DCFR as the turn and river solver. Showdowns use a precomputed
  equity matrix per turn card (one byte per pair of hands, the sum over the 44 rivers), so each
  one is a vectorized matrix-vector product: about 80 ms per iteration with the APH ranges for
  BTN against BB, 20 times faster than sorted showdowns. Measured exploitability: 1% of the pot
  after 50 iterations, 0.2% after 100, 0.04% after 200. The opponent's real flop action is put
  into the tree with its real size.
- "Decisión en vivo" on the flop against one opponent: in a standard single-raised pot (LJ, HJ,
  CO, BTN or SB open, the big blind calls, 100 bb, opponent without a stats-based range) the play
  comes instantly from the flop library if that flop is already solved and the opponent's sizes
  match the stored ones; otherwise the flop is solved live in about 5 seconds (the table shows
  "Calculando el equilibrio del flop…"). Very wide ranges (unraised pots) keep the training
  solver.
- `Services/Solver/FlopLibrary.cs`: the flop library. The 1,755 flops that differ by suits, for
  the five single-raised spots against the big blind with the APH chart ranges: 8,775 solves of
  100 iterations. Real time has a "Biblioteca GTO del flop" row with progress, time left and a
  button to build or stop it. The build runs in the background on four low-priority threads
  (about 6 hours on this PC), keeps Windows from sleeping while it works, saves each flop as soon
  as it is done (`%APPDATA%\APH\gto-flop`, about 6 KB per flop, 51 MB in total) and continues
  where it stopped. Flops are built in a fixed shuffled order with every spot per flop, so a
  partial library already covers every texture. A lookup maps suits (the same flop with other
  suits is the same entry) and scales the stored sizes to the real pot.
- `FlopGtoTests` cover convergence, the real bet in the tree, canonical flops, saving and
  finding a flop with other suits, size matching and the live flop advice (live and from the
  library).

### Added (real time: more on the table)

- Last hand recap: when a hand ends, for 9 seconds in the middle of the table: the preflop in one
  line ("Rival1 sube a 3 bb · Rival3 paga · 2 se retiran"), the board, the cards shown at
  showdown (yours first) with their hand, and who won how much. Can be turned off ("Repaso de la
  mano anterior"). `HandRecap` with `HandRecapTests`.
- "Calculando el equilibrio del …" while a GTO solve runs.
- The grade of your last hand (good, doubtful and mistakes, and the worst decision) can appear
  next to your box for a few seconds ("Nota de tu mano anterior", off by default). When the
  worst decision had the same action as the recommended one, it used to read "elegiste Call 6 bb;
  mejor: IGUALAR 6 BB" (and in English); it now gives the reason, and actions are in Spanish.
- When it is your turn but the reader can't recognize one of your cards or a board card, the box
  says so ("LECTOR DE CARTAS: No reconozco una carta del board (¿J♥?)…") instead of the advice
  disappearing with no explanation. On small 6-table layouts (about 620x430) a probe of the
  live reading found most turns read correctly but some cards unreadable (a J♥ on the board, a
  10♦ in hand); the reader learns them when each hand ends.
- The card reader adapts to a new PokerStars deck. After switching to the big-index white deck
  it never read the hero's two cards: for safety it only learned a card that already looked like
  the known templates of its rank and suit, and with the old deck learned, the new 6, 8, 9, 10,
  J, Q and diamond were too different (rank distance about 0.31, diamond 0.19) to ever be
  learned. Board cards (labelled exactly by the hand history, with no order doubt) that were seen
  for at least 2 seconds and don't confidently look like another card are now learned as a new
  look. Measured live on 4 tables: in about 2 minutes it learned every rank but the 6 (not yet
  dealt) and the diamond, and unreadable cards went from 117-151 per table in 2 minutes to 0.
- The solver runs on up to 6 play-money tables at once, each with its own card reader, so the
  advice appears on the table where it is your turn. A first version stopped at 4 tables and,
  when a table with the solver closed, never gave its place to another open table (the solver
  was only handed out when new tables were linked); with 6 tables open the user saw it on few of
  them. Real time now checks every 4 seconds and starts the solver on any open play-money table
  without it.
- Real time detects tables by itself when it opens, hides the empty slots (one free slot stays
  visible) and the header says how many tables are linked.
- Player notes: a "Nota" button per player opens a note with a color; the color is a dot on the
  player's HUD box and the note is shown under the name. Saved in `player-notes.json`.
- Session bar: hands played with the solver, bb won or lost, percentage of good decisions and how
  often you followed the solver.
- Review log: every hand graded live is saved with what was recommended on each turn (action,
  reason and source: GTO, APH chart or solver against the range), what the player did and the
  grade, one JSON line per hand in `%LOCALAPPDATA%\APH\trainer\revisiones\AAAAMMDD.jsonl`
  (`TrainerReviewLog`, `TrainerReviewLogTests`). Before, the recommendations only lived in memory
  and were lost when APH closed, so a session could not be reviewed afterwards.

### Changed (clearer reports: explanations, charts, big blinds)

- Hero profile: the lower half is now two tabs. "Resumen" keeps what was there; the new
  "Rentabilidad por tipo de mano" says how much each hand type makes when you choose to play it
  (13 types: high/mid/low pairs, AK-AQ, suited aces, weak offsuit aces, broadways, suited
  connectors...), in bb per hand WITHOUT all-in luck (what you won or lost because of the cards
  that came after going all-in is removed: `HandTypeProfit.AllInLuckBb`, equity at the all-in
  against the shown hands). Each row has its error margin, a sample label (very low / low /
  medium / good), the split by position (BTN/CO, early, blinds), a diverging bar chart and a
  "lectura rapida" in plain words (what is clearly winning or losing, how much luck there was).
  Rows with a clear result (beyond their margin) are bold. `HandTypeProfitTests`.
- Hero profile and main panel: every stat box explains on hover what it means, the 6-max
  reference range and your value.
- Main panel: a one-sentence summary under the player ("Juegas el 22% de las manos y subes el
  12% (perfil Tight). En 1.334 manos ganas +79,7 bb/100; sin la suerte de los all-in, +62,8
  bb/100") and the accumulated-gain chart is now hand by hand (it was one point per table, with
  repeated dates): solid line real, dashed line without all-in luck, a legend with both totals,
  a dot at the end of each table with its summary, and the bottom axis in hands.
- Gain report: in big blinds instead of chips (in play money chips depend on the blind level and
  said little): totals, per hand, best and worst cut, bars and tooltips.
- Sessions: besides the real-time PDF list, "Tus sesiones" built from the hand histories
  (continuous play with breaks under 30 minutes): date, time, duration, hands, tables, result,
  bb/100, result without luck, best and worst hand, and a bar chart per session
  (`SessionSummaryService`, `SessionSummaryTests`).
- Leak finder: a "Que te dicen estos datos" box with the clear leaks only (groups of 30+ hands
  losing 20+ bb: worst position, pot type and flop texture, with what to do) and a warning when
  the most expensive combo is just a few hands of bad luck; a one-line explanation under every
  chart; the near-empty "Manos para revisar" panel is compact so the charts show 3-4 bars.

### Changed (session PDF report)

- New page 2 "Como jugaste": the result in plain words with bb/100 and the result without all-in
  luck; every all-in with cards to come ("[As 4s] contra [Jh 3d] antes del flop: ibas con 68% y
  perdiste 155,7 bb, mala suerte de 210,9 bb"); how the live APH advice went (decisions with advice,
  % followed, mistakes, EXPLOTAR hands and their result) with the three most expensive mistakes;
  the best and worst hand types without luck with their sample; and a legend for the stat colors
  (very low to extreme, black under 30 hands, FvCB and W$SD reversed).
- Each table only counts the hands of that session: a table used in two sessions on the same day
  keeps both in one file and the whole file used to be added.
- Times in the user's local time (hand history times are UTC: "Inicio 10:16" next to "Generado
  05:37" was confusing).
- Closing notes in bb (chips in brackets); combos as "A4s" instead of "4A"; in play money the
  focus note no longer says to look for steals (94% of opens get called there): win with value.
- Sessions: "Generar informe PDF" creates the report of any played session from the hand
  histories (the selected one or the latest), with the stats of that session's hands
  (`RealTimeSessionReportService.SavePdfForSession`). The session table shows local times.

### Changed (other accounts archived)

- The hand history folder had 10 accounts and the dashboard mixed them into UwU_Fantasy_UwU's
  profile (46 tables, 1.974 hands, chart from September). At the user's request the 9 other
  accounts were moved to `Documents\Manos_PokerStar_archivo` (nothing deleted) and their paths in
  the APH backup (`aph.db`) updated so they are not restored; a copy of the database before the
  change is kept next to it.

### Fixed (obvious live mistakes on the 250/500 tables)

- Replaying the evening's 83 live advices against the hand histories: the logic was right, the
  wrong ones came from what was read on screen. AKo in the BB against a limp + 3 bb open + call
  got "NO IR, ~12%": the raiser's stack was misread (97 -> 70), so the open looked like a 30 bb
  "huge raise" with an AA/KK range. Preflop, what the raiser put in can no longer exceed what the
  call button says it costs (plus what you already have in) unless calling puts you all-in; the
  button reading wins and the detail says so. `TrainerObviousSpotsTests`.
- QTo (top pair) facing a turn all-in plus a call got "ALL-IN +58 bb": whoever calls someone
  else's all-in was weighted like a normal call (lots of weak hands). New range line "pagó un
  all-in" (air 3%, weak 20%, medium 65%, strong 100%; a bit wider for calling stations), used live
  and by the review.
- A plate whose action was not read ("-") gave that player the range of someone who only called
  preflop, even if he had just bet: QQ on 9-9-7 four-way facing a donk-bet got "SUBIR A 28,5"
  (+16 bb). If the player put chips in on this street, the one who opened the betting counts as
  bet (or raise) and the rest as call. That spot now says call (+9.8 bb vs raise +7.2).
- Flush and one-card-straight boards (`TrainerDecisionRules.BoardDanger`): with three or more cards
  of a suit and no flush, or four cards in a row and no straight, the solver against estimated
  ranges no longer raises and bets at most half the pot (the detail says what it preferred). AQ
  (two pair) on Qd Kd Ad against two callers got pot-sized bets on the flop and turn and the
  pot was lost to a set. The river all-in there was forced (22 bb into 266 bb with 32.8 left).
- Only windows of the PokerStars process count as tables: VS Code with `PokerStarsParser.cs` open
  was detected as a table.
- Live position: it is predicted from the previous hand's history (the button moves one active
  seat), but when that history arrives after the next hand has started, the old position was
  used: 7 of the first 62 logged reads were one or two hands behind (CO when HJ, BB when SB, HJ
  when BB). Now the button is moved once per hand dealt on screen since the last history.
- Positions also use what is seen on the table (`TrainerSession.ComputeLivePositions`): the button
  moves only among seats that are not "ausente" or empty now (a player who sat out between hands
  shifted every position: Ks7h in the BB was read "HJ"), and preflop "you can check" means BB and
  "call costs half a blind" without raises means SB. Kept for the rest of the hand.
  `LivePositionTests`.
- The dealer button is now looked for on the table (`LiveTableReader.DealerPlate`): a light disc of
  ~20 px with the red "D", told apart from heart cards (white further out) and avatars by size,
  shape and the felt around it, and assigned to the nearest plate. When seen, it sets the
  positions instead of guessing where it moved (PokerStars moved it backwards at Botha IV and the
  hero posted the SB twice in a row; read "BB"). Checked on two real captures (button on the hero,
  button on the left player). `DealerButtonTests`.
- "Collection was modified" error on the table HUD: the capsule walked a player's last hands
  while the history reader added one. `PlayerStats.LastHands` is now a locked copy.
- At the end of every hand, what was read on screen when the advice was given (hero cards, board
  of that street, position) is compared with the hand history (`LiveReadCheck`). Mismatches go to
  the review log (`LiveReadCheck`), the APH log and the table's status line ("Ojo: en la mano …
  se leyó mal (posición leída HJ, era BB). Manos con lectura mal: 1 de 40.").
  `LiveReadCheckTests`.
- Every live advice now stores what was read on screen (cards, board, position, pot, call, stack,
  buttons, plates) and the calculation in the review log (`LiveDetails`), so a strange advice can
  be traced to a misread or to the calculation. The 65s river all-in and the ace-high call on a
  four-heart board could not be traced because this was missing.
- Play-money aggression limit for every postflop path (`TrainerDecisionRules.PlayMoneyAggression`),
  GTO included: the board-danger cap only reached the training solver, so the heads-up GTO said
  "siempre all-in" with QJ (two pair) on Qs Jh 7d 8d 9c, where any ten makes a straight
  (Pandora V, -67.6 bb). Now, whatever GTO, the solver or the rules prefer: no raise or all-in
  without a strong hand (two pair using both cards, trips or better); one pair bets at most 75% of
  the pot (weak pair 55%, 35% on the river); no pair and no strong draw means check (no bluffs:
  in play money they get called); a draw alone bets at most half the pot. On a dangerous board no
  raise or all-in and at most 55% of the pot (35% on the river). All-in with top pair is allowed
  only when less than 60% of the pot is left behind. The detail and the reason say what GTO or the
  solver preferred. Same evening: A-Q high on Tc 4s 4h got "SUBIR A 51 BB" against a calling
  station (Mintaka IV), now call; a pair of fives got a river bluff-raise to 44 bb (Setebos V),
  now call.
- `BoardDanger` also covers a straight or flush already on the board ("Juegas la mesa" when your
  cards do not improve it: no betting at all; "La mesa ya hace escalera" when a higher one beats
  you) and a low flush with four of the suit on the board (below the king).
- Stack read without its comma ("116,2" read "1162"): APH believed 1,045 bb were already in on the
  flop and said "ALL-IN (1162 BB)" with seven-high (Aristaeus IV). Stacks never grow during a
  hand, so a reading 10 or 100 times the last one is dropped, a misread street-start stack is
  corrected with the next good reading, and what you put in on a street is never more than the pot.
  `TrainerObviousSpotsTests` (three hands of the evening replayed, comma loss both ways).
- Strong but not top hands no longer shove more than the pot: 62s (trip deuces with one card) on
  7h 2h 2c 8c Jd went all-in for 87.6 bb into 29.3 (GTO 74%) and only Q2 called (-90.6 bb). Trips
  with one card and two pair still bet and raise for value, but an all-in must not exceed the pot
  (no overbets); sets and straights, flushes or full houses made with your cards and above the
  board keep no limit.
- Smaller tables: the OCR now enlarges each crop in proportion to the table width (reference 692
  px; at 624 px nothing changes, at ~500 px it uses 4 and 6 instead of 3 and 4), so text reaches
  the recogniser at the same height. Harness mode `escala` compares what is read on a table shrunk
  to 85-70% (cards, turn, buttons, pot, plate stacks and actions, dealer button) with the real size.
  On the user's real 624x430 tables, checked against the hand histories: stacks 347 right and 11
  wrong, pot 77 right and 2 wrong; cards and buttons read on every turn.
- Hero stack read as 0.03-0.04 bb (and later 2, 3, 5, 8 or 9 bb) on the 624x430 tables: the OCR
  splits the stack figure ("132,5 BB" read "13 2, 5 BB"); the first piece was taken as chips
  (13/500 = 0.03 bb) or the last as the stack. The table said "IGUALAR ALL-IN (0,03 BB)" with T4o,
  and "NO IR" with JTo and K5s in the SB heads-up (a stack of 8 bb made the BB look like a 165 bb
  raise). `LiveTableReader.StackFromLine` joins the digit pieces right before "BB" (a long number,
  such as one in a player's name, stops the join), and `ParseAmount` prefers a figure with "BB".
  16 of the 18 wrong stacks since 22:20 were this split or a lost comma. More split shapes seen
  live and covered: "101 2 BB" (comma lost and a gap, read 2 bb: "IGUALAR ALL-IN (2 BB)" with
  Q-high at Roxane V) and "20Z1 BB" (Z for 2).
- Second guard (`TrainerSession.ImplausibleHeroStack`): with the previous hand already in the
  history, the hero's stack read on screen must be explained by that hand's ending stack minus
  what is in the pot now; if more than half is missing, the reading is ignored ("?") and logged.
  Same for a stack over five times that one (lost comma: "92,5" read 925 at Letaba IV); adding
  chips from a short stack is still allowed.
- GTO mixes that had not settled (few iterations on the turn) recommended the most frequent action
  even when another one earned clearly more: ATs at Roxane V got "IGUALAR 27,3 BB" from "call 48%
  (-1.36 bb) · fold 46% (0 bb)". Now another action with at least 25% frequency that earns more than
  0.5 bb (or 2% of the pot) more is recommended, and the detail says why
  (`TrainerLiveDecisionAdvisor.BetterGtoChoice`). Only towards less risk (fold or check before call,
  call before raise): A5s with a pair of fives on a river that completed the flush had "fold 58% ·
  call 42%", was switched to call by EV and paid a flush (-71 bb).
- Against a known maniac, no bet without a pair (draw only): his own plan says "let him bet, don't
  bluff him". KJs with the nut-ish flush draw on 3c 9s As semi-bluffed half the pot, the maniac
  shoved and the call was then forced by the price (he had AQ, -83 bb).
- A big blind posted by a new player is no longer counted as a limper: 22 in the HJ, first to act,
  saw a 2.5 bb pot, read "1 limper delante" and was told to limp behind. Limpers deduced from the
  pot are capped by the players who act before the hero (blinds not counted) when every seat's
  position is known.
- "Farol" per table (button in each table slot of Tiempo real, off by default, remembered by table
  name in `%LOCALAPPDATA%\APH\trainer\farol_mesas.json`, play money only). The play-money limit
  never bluffs, but the session showed players who do fold: to a flop bet, tight players (VPIP
  under 30) folded 85%, medium ones 60% and loose ones 45%. When the advice faces, heads-up and
  without a made hand, a player APH tags "nit" or "se retira mucho" (40+ hands), the table box says
  so in blue; with "Farol" on, a bet of up to half the pot is allowed and the solver uses that
  player's fold rate to decide if it pays. Never a bluff-raise, never against calling stations or
  maniacs (`TableBluffModes`, `LiveDecision.BluffNote`).
- A player who called an all-in is counted even when his stack was first read after his open:
  A10o 3-bet from the BTN, the SB shoved 34 bb and the CO called, but the CO's chips put in read 2
  bb short, so the price was worked out against the shove alone (40%) and said "IGUALAR 28,3" (-34
  bb). With "Igualar" on his plate and at least 80% of the bet in, he counts as a caller; and when
  that caller still has chips behind and the hero is not all-in, the equity is discounted for the
  betting left in the side pot.
- Raises are for value only in play money: a solver raise that wins less than half the time when
  called is dropped (QQ, nines full of queens on 9h 9d Ad 8h 9s, raised to 9 bb winning 36% when
  called: any ace beats it; now call). `TrainerLiveDecisionAdvisor.ValueRaisesOnly`.
- "Tu color es bajo" counts only the higher cards of the suit that are still unseen: KJo with the
  jack of diamonds on 5d Kd Qh 3d Qd is the second-best flush (only the ace beats it) and was
  being called low (river bet capped at 35%). Now low means two or more unseen higher cards.
- Impossible pot at the start of a street: QJ on the river of 6h Qh Ks Kc Js read the pot as 428.5
  bb (about 134 real), so the call price came out at 8% instead of 22% and a full house got paid
  (-66.9 bb). With no pot seen yet on the street, the pot cannot exceed the previous street's pot
  plus, for each player still holding cards (plus one), that street's top bet and this one's; if
  the reading is impossible and the lost-comma reading is below the minimum (previous pot plus the
  call), the minimum is used, so the price is demanding, never a gift. The same-street bound also
  uses the table's top bet (an all-in read from a stack): with the call button missing a digit
  (13,7 read 3,7), a right pot of 164 bb was "corrected" to 16.4.
- Session report stats across tables are now exact: each table's percentage was averaged by hands,
  so a table where the hero never faced a c-bet counted as "0% fold" and dragged the total down
  (the night report showed fold to c-bet 35% when it was 16 of 26, 62%; the last-6-tables report
  showed 14.9% for 1 of 5). Report rows carry the raw counters (`RealTimeStatCounts`) and tables are
  merged by summing them; CBet, 3-bet, AF, WTSD, W$SD and WWSF were affected the same way.
- The feedback traffic light no longer judges a stat with too few chances (`HeroStatCoach.
  MinOpportunities`: 15 c-bets made/faced or showdowns, 20 postflop actions, 30 3-bet chances or
  flops seen): fold to c-bet was "A mejorar" and the "next focus" from 5 hands.
- New opponent type "Trampero" (`TrainerVillainProfile.IsTrapper`, leak "trampero"): plays at least
  an average number of hands but almost never raises preflop, and is aggressive after the flop
  (VPIP at or above the population median, PFR at or below p15, AF at or above p55; never a maniac).
  kastensyster (VPIP 36, PFR 1.6, AF 2.6) limped or called with everything, then check-raised the
  turn and shoved the river with QQ (full house), shoved a flush and called off with two pair. His
  postflop bets and raises now weigh strong hands much more (`TrainerRangeEstimator.Factors`), his
  calls count as light, and the advice box shows the plan: "cuando sube o va all-in después del
  flop tiene mano: créele; si solo paga, apuéstale por valor".
- Live positions also count every plate that already acted this hand (a fold, call, post...), not
  only the previous history's players and the plates with cards: at a new table (Beatrix) a player
  who had just sat down posted the SB and folded; without him the hero's BB was read "SB" and J9o
  got the SB chart's "NO IR" instead of the BB defence.
- A misread hero stack at the start of a hand no longer invents a preflop raise: J9o on the BTN,
  folded to him with 1.5 bb in the pot, got "subida a 67,6 bb: no ir" because 66.6 bb counted as
  already put in. Preflop, what the hero put in is capped by what the pot holds above the blinds,
  and off-turn plate reads drop an impossible hero stack (`ImplausibleHeroStack`) before merging.
- Play-money no-call rule (`TrainerDecisionRules.NoCallReason`, `PlayMoneyLimit.NoCall`, applied to
  the solver, GTO and rules): facing a bet of 40% of the pot or more on the turn or river, with a
  hand that loses to many holdings, the advice is fold (unless the price is 15% or less or the
  bettor is a known maniac). "Loses to many" is measured over every two-card holding left
  (`BeatenShare`): river 8% or more; turn 15% or more with a good pair or better, and any weak
  pair or draw without the nut flush draw. It covers trips, two pair and full houses too (the user
  asked for it: "de ahí sigue dándola por buena"). The session's stack losses following the advice
  included A5o top pair and A5s fives on rivers that completed the flush and AJo with a jack-high
  flush draw on the turn (~-227 bb); sets on dry boards still call (77 on 5-7-2-4 loses to ~4%).
- Play-money no-re-raise rule (`TrainerDecisionRules.NoReRaiseReason`): when an opponent raises on
  the turn or river (over the hero's bet or someone else's), the advice only re-raises with a
  near-nut hand (under 2% of the holdings left beat it); otherwise it calls or folds. A raise there
  is almost always a made hand. Not applied when the hero is already committed (stack behind of
  60% of the pot or less). Found by replaying the big pots of 18/19-11 through the new advisor: it
  re-raised all-in 230 bb with a king-high flush on K-Q-Q with four diamonds (~7% beat it) against
  a calling station who had raised the turn and the river.
- Five solver improvements the user approved ("dale todas") after the 19-11 early-morning session:
  1. Strength within the strong class (`TrainerRangeEstimator.LineWeight`, `StrongPercentiles`):
     when an opponent bets or raises, each strong holding weighs by how high it sits among the
     strong holdings on that board (the weakest keeps 40%, or 20% for raises, the river and
     multiway). A deuce-high flush used to weigh the same as the ace-high one, and weak two pair as
     much as a set, so big bets were credited with many hands worse than the hero's. Used live, by
     the GTO street-start ranges and by the end-of-hand review. Calls are unchanged.
  2. "Lectura dudosa" line in yellow in the table box when this turn's reading was corrected or
     guessed (own stack ignored or taken from earlier in the hand, pot corrected, amount to call
     deduced from the raise button, opponents' chips unread, position unknown); also in the details
     (`LiveDecision.ReadWarning`, `TrainerLiveDecisionAdvisor.DoubtfulReads`).
  3. Multiway: bets and raises against two or more opponents polarise like the river in the range
     model; one pair bets at most 60% (weak pair 40%/25%) and there is no draw-only semi-bluff; the
     no-call rule is stricter (river 5%, turn 10% with a good pair).
  4. Following the live advice is never graded as the student's mistake: when the end-of-hand
     review (history ranges) disagrees, the decision is "dudosa" (Marginal) with the review's
     explanation, so the report and the watcher stop calling it an error.
  5. Pause notice in the table boxes and the Tiempo real status after 3 hours of continuous play
     (a gap over 30 minutes restarts the count) or 3 big pots (30 bb or more) played against the
     advice within an hour; at most once every 30 minutes (`PokerTableOverlayWindow.ShowNotice`).
- "Terminar sesión" in Tiempo real reports only the last continuous stretch of play (no gap over
  30 minutes between hands of all its tables), like Sesiones does. Each table used its whole day
  file, so a table played hours earlier brought those hands in (the 03:49 report started at
  23:14 with Letaba IV and Tanete V). `RealTimeSessionReportService.SavePdfForLastStretch`.
- Top pair no longer stacks off through a bet that is an all-in in disguise: AJo on Kc Ah 3h Ts 4d
  got "APOSTAR 55,5 BB" with 52.8 behind (the stack was unread on that turn) and AQ called
  (-72.7 bb). The play-money limit now treats any bet or raise that commits two thirds of the stack
  as all-in, like the advice box already does, and an unread hero stack falls back to the last one
  read in the same hand (if plausible).

### Added (feedback with a traffic light for your stats)

- `HeroStatCoach`: each of the hero's 10 stats gets a verdict, green "Bien", amber "Por
  ajustar" or red "A mejorar", always shown with its icon and word, plus what to do. The level
  (low/normal/high) comes from the colour scale; the verdict says whether that suits you. In play
  money (opponents call almost everything) tight, value play is rewarded and steals or bluffs are
  never suggested: a low CBet or a slightly low WWSF are fine, a high WTSD is fine while you win
  your showdowns, and what gets corrected is passivity with a hand, bluffing, loose calls at the
  end and a big VPIP-PFR gap. Real money has its own criteria.
- The session PDF: a verdict icon on each stat tile of the cover, "Lo más importante" from the
  red/amber items, and a "Retroalimentación" page (stat, value, level chip, verdict pill, what to
  do), then "Siguiente enfoque" and the session notes ("Lo que salió bien", "A vigilar",
  "Contexto"). The old notes and alerts had their own fixed numbers and could contradict the
  colours ("VPIP sano" next to "algo bajo"); stat notes now come only from the feedback.
- Hero profile: new "Retroalimentación" tab with the same rows and the next focus. The hero tags
  in the panel and the profile come from it too ("VPIP algo bajo", "WTSD alto"... coloured by
  verdict, tooltip with what to do), and the panel recalculates them when the play/cash mode is
  known. Villain tags ("Juega muchas manos", "Va mucho a showdown"...) now use the colour scale
  (above p85 / below p15) instead of fixed numbers. `HeroStatCoachTests`.
- The traffic light also grades the session's decisions (`HeroStatCoach.EvaluateDecisions`):
  mistakes marked by the review, advice followed and "costly mistakes", hands with a mistake that
  lost 50 bb or more WITHOUT all-in luck (a good all-in that lost to bad luck, like A4s against a
  junk shove, is not a costly mistake). Green with no costly mistakes, amber with 1-3 or under 90%
  advice followed or 3%+ mistakes, red with 4+, under 75% followed or 6%+. It is the first row of
  the "Retroalimentación" page, goes first in "Lo más importante" and the next focus when it is
  the worst item, and shows as a pill next to "Consejos de APH en vivo". "Los errores más caros"
  are now sorted without luck and show both figures ("-155.7 bb (sin suerte +55.2 bb)"). Before,
  the cover said "tus stats están bien" even with costly mistakes. The afternoon session: amber,
  one costly mistake (Q8s all-in, -50 bb without luck).

### Changed (one stat colour scale for all of APH)

- The stat colours had 7 hues with no clear order, the green "normal" sat between the 15th and
  30th percentile of the population (a player right at the median showed yellow or orange, "a bit
  high"), FvCB and W$SD ran backwards, and the session PDF used its own hand-picked cuts, so the
  same stat could have different colours in the HUD and in the report. Now there is one scale
  (`StatScale`) used by the table HUD, every window (panel, hero profile, analysis by table, table
  detail, Data Villains and villain detail, Tiempo real) and the PDF: blue = below normal (very low
  < p5, low < p15, a bit low < p30), green = normal (p30-p70, like most players), amber/orange/red
  = above normal (a bit high > p70, high > p85, very high > p95), grey = fewer than 30 hands. The
  colour never flips; whether high or low is good is said in words. Text on each colour is white or
  near-black for contrast (4.5:1 or more; before, black text on dark red was hard to read).
- Hovering any stat says what its colour means and what that says about the player, e.g. "VPIP
  46%: muy alto (más alto que 95 de cada 100 jugadores). Entra en muchas manos: juega suelto."
  The panel's 10 stat boxes also show the level under the number ("Normal", "Algo bajo"...). A
  colour legend sits in the panel, hero profile, analysis by table, Data Villains and Tiempo real,
  and the three "dictionary" windows explain the new scale. `StatScaleTests`.

### Changed (session PDF report redesigned)

- New look: a navy band on top of every page (APH, hero, date) and a footer with "Página N de M";
  no more giant "APH" watermark over the content; Spanish accents and ñ (WinAnsi fonts, before they
  were stripped); thousands separators; rounded cards; text measured with Helvetica metrics so
  numbers line up on the right and long lines wrap instead of being cut.
- Page 1 "Resumen de la sesión": the result in big (bb and chips), bb/100 and the result without
  all-in luck, a hand-by-hand chart of the session (real line and dashed "sin suerte" line), your
  10 stats as coloured tiles with their level and the legend, best/worst hand and the rivals that
  cost you most / paid you most, and "Lo más importante".
- "Cómo jugaste": the all-ins as a table (your hand, against, street, equity, result, luck), APH's
  live advice as three figures (with advice, followed, mistakes), the costliest mistakes and
  "Qué manos te dejaron dinero" as bars from zero, all on one page.
- "Rivales de la sesión" (top 20), "Mesa por mesa" (two tables per page, your row highlighted),
  "Evolución por mesa" (time axis, rounded ticks, full table names in the legend, bb/100 column)
  and "Notas de cierre" split into "Lo que salió bien", "A vigilar" and "Siguiente enfoque".
  8 pages instead of 11 for the afternoon session. The drawing code moved to
  `RealTimeSessionReportService.Pdf.cs`.

### Added (table HUD: players you already played with)

- The HUD capsule on the table only counted that table's hands, so a regular from another table
  sat down as "1 mano · Poca muestra". Now it adds what you already played with them at your
  other tables (`PlayerHistoryIndex`: every hand history in the table's folder except the table's
  own file, which the live reader already reads from the start, so no hand counts twice). Their
  stats, type and colours come from all those hands, the hand count reads "50 contigo" and, from
  30 hands on, the capsule gets a border and a pill in the sample colour (30+ sky blue, 100+ light
  green, 200+ green, 500+ yellow, 1000+ light purple, 5000+ purple, 10000+ white). Below 30 hands
  there is no colour yet (the stats don't say much). The folder is read in the background (about
  0,2 s for today's 8 tables) and again at most once a minute, only the files that changed, so
  your other open tables keep adding up during the session. The orange "explotar" blink and the
  white move-mode border still win over the sample colour. The players list in Tiempo real uses
  the same colours (the 30+ band is new there). `PlayerStats.Add`/`CombinedWith`,
  `PlayerHistoryIndexTests`.

### Added (exploit known opponents, play money only)

- Reserved play first, exploitation with data: against one opponent the live advice stays GTO
  until that player has at least 40 hands (the same sample APH needs for a stats range) and a
  clear leak (`TrainerVillainProfile.Leak`: maníaco, pagador, nit or "se retira mucho" to
  c-bets). Then the advice comes from the solver against their range and their way of playing
  (their fold to c-bet, calling station, maniac), the box title says "EXPLOTAR · pagador · 80
  manos" and a line under the advice says how to exploit them (`LeakPlan`, e.g. "Paga casi
  todo: apuesta por valor más fino y más grande, y no le hagas faroles."). Players without data or without a
  clear leak keep getting GTO. The review log saves the source as "Explotar (…)".
  In multiway pots (always the solver against their ranges, never GTO) the box also says
  "EXPLOTAR · name: pagador · 59 manos": facing a bet, only for the bettor; without a bet, for the
  calling station or maniac with most hands (bluff plans against a nit don't work with more
  players in the pot). The title is orange and, when the advice appears, it and the plan line
  gently blink (opacity down to 55%, three slow pulses, about 4 seconds) and then stay still. The exploited player's HUD capsule blinks at
  the same time with an orange border, to see at a glance who it is (same border width: the
  capsule does not change size). `TrainerExploitTests`.

### Fixed (pot misread without its comma; raising against an all-in)

- Real hand: Q♣T♦ with nothing on 6♠8♥2♠, a maniac shoved 82,9 bb into a 5 bb pot. The OCR read
  the pot as 879 instead of 87,9 and the box said "ALL-IN: gana +112 bb" (calling also "won"
  +109). Three guards now:
  - the pot is read with the three OCR variants, and a reading about ten times another one gives
    way to the smaller (a lost comma only inflates it; `LiveTableReader.PickPot`);
  - a pot larger than what was already in the pot on this street plus the current bet for each
    player still in the hand, or than all the chips at the table, is divided by ten, with a line
    in the details saying so (`LiveHandContext.PotSeen`, `TableChips`);
  - in the training solver an opponent who is already all-in never folds to a raise and puts in
    no more chips (`SolverOpponent.AllIn`); before, the raise got imaginary fold equity.
  `TrainerPotReadingTests`.

### Fixed (card reader: one wrong template left every diamond unread)

- At 15:49 a 7♦ (blue suit) was learned as "7c". Clubs are green in the 4-color deck, so there
  were no clubs of that color to compare with, the suit check came out "no data" and the image
  was accepted. From then on every diamond matched ♦ and ♣ equally (distance 0,000 to both), the
  reader left it as "?" and any hand with a diamond (own card or board) got no advice.
- Learning now rejects an image whose suit color is not that suit's color, and loading the
  templates removes any template whose color differs from almost all of its suit
  (`CardTemplateStore.RemoveColorOutliers`), so today's bad template goes away on restart.
  `CardTemplateStoreTests`.

### Fixed (card reader: own cards of the new deck)

- With the big-index deck some of the player's own cards (8, 2, sometimes 3) look different in
  the hand than on the board. The board had already taught those ranks, so the hand's look was
  rejected as "too different" and never learned: every hand with an 8 stayed without advice
  ("No reconozco una carta"). The player's cards now learn a new look too, but only when their
  order on screen is certain (different suit colors, or clear templates). Checked with real
  captures: the 8♦ that was "?" is read after one hand, and K♥ 6♥, A♥ 3♣, K♦ T♣ still read the
  same.

### Fixed (price of a preflop all-in that covers you)

- Found in the afternoon review log: with AA facing a 4bet shove that covered the player, the box
  said "NO IR: con ~83% no llegas al 100%". The chips each player put in come from "stack at the
  start minus stack now"; the OCR had read a stack without its comma (117,5 -> 1175), so the
  shover seemed to have put in more than 1000 bb and the pot you can win came out as 0. Now a pot
  smaller than what the shover matched (at least what you pay) is treated as a misread and the
  estimate from the screen pot is used.
- When the shover covers you and nobody else is in the pot, the part of their bet you cannot
  match is no longer counted in the pot (the "call all-in" button already shows only what you can
  pay): AKs against a 130 bb shove with 86,6 behind asked for 39% instead of ~48%.
  `TrainerPreflopPriceTests`.

### Fixed (multiway preflop: all-ins and squeezes)

- Real hand: three limps, the button went all-in for 33 bb, the small blind called, and the
  player had 33 in the big blind. The live box said "IGUALAR 32,3 BB: la tabla APH paga con 33 en
  BB contra BTN" (the heads-up chart against a 2–3 bb open); calling needed 31% equity against
  two all-in ranges. Three causes, all fixed:
  - The plate of a player who is all-in shows "All-in" under the name, where the stack goes. Only
    the first line was read, so that all-in was never seen. Any line now counts
    (`LiveTableReader.ReadPlates`).
  - A raise far from the chart sizes (open to 12 bb or more, 3bet to 30+, 4bet to 45+;
    `TrainerDecisionRules.IsOversizedRaise`) is now decided by price against the ranges of the
    raiser and of who already called, like an all-in, in the live advice and in the grading.
  - An open with someone who already called uses the squeeze chart (multiway) instead of the
    heads-up "vs Raise" chart, in the live advice and in the grading.
  `TrainerMultiwayTests`.

### Fixed (multiway pots, found in the session review)

- Limping behind limpers: the live advice and the grading said a hand was "playable in a cheap
  multiway pot" only by its strength against a random hand (top 35%). So weak offsuit aces and
  kings (A2o–A7o, K7o, Q9o) were advised as a limp, while 22, 33, T9s, 87s or 65s were folded.
  Now both use `TrainerDecisionRules.PlayableMultiway`: pairs, suited hands in the top 35%, suited
  connectors and one-gappers from 54s, and offsuit hands only with both cards T or higher. The fold
  explains why ("sin mismo palo… tu kicker suele perder contra otro as"). Limping anyway with a
  strong offsuit hand of that kind (A5o) is graded Marginal, since it costs little, not Mistake.
- Multiway bluff label: the grading called a bet a "farol multiway" whenever the equity against
  all opponents at once was under 55%, the heads-up value line. With 3–4 opponents that happens
  even with top pair or a full house (A7 on A-8-T against 4 players: 46%). The value line is now
  measured against the fair share (`ValueEquity(street, opponents)`, `FairShare`): 37.5% against
  three opponents on the flop. A bet with at least the fair share is a thin bet (Marginal), not a
  bluff, and the texts say "contra N rivales a la vez (la parte justa es X%)". Heads-up is
  unchanged. `TrainerMultiwayTests`.

### Added (solver on the table, play money only)

- Real time has "Solver en la mesa (solo dinero ficticio)" (on by default). On the first linked
  play-money No Limit Hold'em table it runs the Modo Entrenador live reading in the background,
  and on the hero's turn the table HUD shows a box above the hero's box: "SOLVER · dinero
  ficticio", the action in big type with its color (fold, check, call, bet/raise) and the reason.
  It disappears when the turn ends. The reading keeps the trainer gate (window title and hand
  history; real money and tournaments are blocked and not retried) and never clicks. It now runs
  on up to 6 tables (see "real time: more on the table").

### Changed (table HUD)

- Each box now sits at the player's real seat. The boxes used to follow the order of the player
  list, which was also corrupted: the parser read seats from the hand SUMMARY too ("Seat 1: X
  showed [..] and won"), duplicating players and inventing names. Seats are now read only from
  the hand header, with the table size ("6-max") and who is sitting out, and placed clockwise
  with the hero at the bottom, like PokerStars. Players who are away and have no hands are not
  shown.
- The numbers are visible: they used to be painted with the cell background color, so with fewer
  than 30 hands they had the same color as the box ("/ / /").
- New layout: name, player type (Fish, Nit, TAG, LAG, Maniac... with its color; "?" from 15 to
  29 hands, "Poca muestra" below), hands and last hand shown, then a mini table with each stat's
  name above its number and the stat color as background (from 30 hands). "–" when the stat has
  no sample yet.
- Boxes no longer cover names and stacks (above each seat, the hero's to the right of the seat).
  Real time has HUD options: Mínimo (type, hands, VPIP, PFR, 3B) or Completo, three sizes, hide
  my box, "Mover HUD" to drag the boxes (the table stops receiving clicks until "Listo") and
  reset positions. Saved in `%APPDATA%\APH\hud.json`.
- `HudSeatTests` cover the summary duplicates, seats, sitting-out players and the seat rotation.

### Fixed (real-time table detection)

- "Detectar mesas" did not link a table if it was pressed before the first hand ended: PokerStars
  creates the table's history file only then, and APH gave up. Real time now keeps watching every
  4 seconds after the first click, links those tables as soon as their file appears, and also
  links tables opened later; the status line says which tables are waiting.
- The history files are searched in the PokerStars folder from APH settings and in the folder
  PokerStars itself saves to (`SaveMyHandsPath` in its `user.ini`), instead of
  `Documents\manos poker` or the whole Documents folder (35 ms instead of 336 ms here).
  `TableDetectionTests` covers the "file appears later" case.

### Fixed (positions)

- Players "sitting out" were counted when assigning positions, so at tables with absent players
  every position could shift (the small blind showed up as the big blind). This affected every
  per-position view (hero profile, gain analysis, hand detail, Mis Tablas). A dead button is also
  handled. `PositionMapTests` cover both cases with a real hand.

### Fixed (appearance)

- Changing the appearance with APH open left the window background green (gradient, glow and
  watermark) until restart: the background is now rebuilt with the new palette.
- The large "APH" watermark sat behind the cards and only showed "PH"; it was removed. The spade
  in the sidebar logo is drawn as a shape and centered in its square.
- More backgrounds stayed green after changing the appearance (filter bars and tables in Table
  analysis, Data Villans, table detail, Hero profile, Leaks, Sessions, Mis Tablas and the
  real-time panels). WPF freezes the theme brushes, so a palette change creates new ones and
  anything that read them once kept the old colors. All theme colors in the views now use
  `DynamicResource` (100 references); the cached pages are rebuilt when Settings closes after a
  palette change, and the dashboard re-reads its stat colors. The hero rows (real time and hand
  detail), the replayer felt and the table-detail chart tooltip were fixed colors and now follow
  the theme. `ThemeTests` fails if a view reads a theme color with `StaticResource` again.

### Added (calibration with real hands)

- `APH.Calibration`: reads the public *Dataset of Poker Hand Histories* (University of Toronto,
  Zenodo doi:10.5281/zenodo.10796885, CC BY 4.0; 21.6 million real online hands from July 2009,
  NL25-NL1000, six sites) and writes `Hud.App/Data/population_baselines.json`: per-level
  percentiles of every HUD stat, preflop frequencies by position (open, 3bet, fold/call/4bet)
  and flop c-bet / fold vs c-bet.
- HUD colors use those population percentiles instead of hand-picked cuts (with the old cuts as
  fallback). Play money and micro stakes use the NL25-NL100 population, the closest real level.
- Player style labels (Fish, Maniac, LAG, TAG, Nit, Tight, passive...) were copied in four
  windows; now there is one classifier and each limit is a population percentile. With the
  current data the limits stay close to the old numbers, and they follow any recalibration.
- The trainer's villain types (calling station, nit, maniac, folds to c-bets, TAG, LAG, passive
  fish) use the same population percentiles.
- W$SD and WWSF in the calibration: the dataset leaves PokerStars, iPoker and Absolute winnings
  empty, so winners are worked out from the hand (last player left, or the shown cards with a
  7-card evaluator and side pots). Checked against sites that do record winnings: 99% agreement
  at showdown; all-ins where the winner's cards are hidden are left out.

### Added (tests)

- `HandReader.Tests`: 25 automated tests (headers, euros, antes, dead blinds, PFR, 3bet over
  opportunities, CBet by the last aggressor, WTSD with mucks, sitting-out players, amount
  parsing, stat coherence). Run with `dotnet test HandReader.Tests`.
- Calibration file checks: 7 ordered cuts for every HUD stat and level, believable population
  medians (a broken calibration with W$SD at 0 fails), percentile interpolation.

### Fixed (earlier in this release)

- PFR counts every preflop raise (opens, 3bets, 4bets), not only the first raiser of the hand.
- Hand results with antes or with "small & big blinds" posted are no longer overstated by the
  dead money.
- The dashboard no longer shows 0 hands and "No data" for play-money histories: it switches to
  PLAY mode by itself when there are no cash tables.
- The "BB final" arrow is green for any winning table, not only from +100 bb.
- Leak finder: hands in HJ are grouped as MP, so "Biggest leak" matches the positions table.
- Text without its own color (for example the Gain "Reading" titles) is no longer black on the
  dark background.

- A free check with the blind posted (also after a new player posts out of position) is never
  graded as a mistake or given a "Fold" reference.
- Paired boards no longer turn a single pair into "two pair" for range narrowing; board-made
  straights and flushes are labeled as such.
- Sizing notes use the right reference for 3bets and 4bets and skip all-ins.
- No live recommendation is shown while a board card cannot be read, so it is never given for
  the wrong street.
- Each live recommendation is matched to the graded decision of the same turn; a turn that was
  not read live no longer shifts the rest.
- A live raise is shown without an amount when the current bet cannot be read, instead of an
  invented size, and the call amount is never taken from the all-in raise button.
- A calibration that cannot be saved no longer closes APH.
- A blocked or closed table stops only its own session, without a modal dialog that would take
  the focus from PokerStars; a brief empty window title no longer blocks a table, and a table
  that is not No-Limit Hold'em 6-max stops with its own "NO 6-MAX" state.
- A blocked table is never followed again in the same trainer session ("Corregir todas" lists
  it instead, and "Iniciar en vivo" refuses it).
- The live reader no longer falls back to copying the screen when another window covers the
  table, so it can never read a different (possibly real-money) table on top of it.
- The play-money and No-Limit Hold'em checks on the window title ignore the account name, and
  any currency in the title blocks the table.
- A live table can be switched without stopping the others: the previous live table keeps being
  graded from where it was, no hand is graded twice and the table keeps its score.
- A live session waits until the table's existing hand history has been money-checked before it
  reads the screen, and a hand history with no recognizable hand headers blocks the table.
- A previous day's hand history of a same-named table no longer stops or permanently blocks
  today's table; blocks found in a hand history are re-checked by every new session.
- Three `vs 4bet` BB charts in the APH reference CSV were labeled against the wrong position.

- Table detection no longer crashes with `UnauthorizedAccessException` when the default
  hand-history folder is missing and `Documents` contains protected system links.
- Empty cells in the APH preflop CSV are read as fold instead of an empty action.
- The hero name is read from the newer PokerStars window title format
  ("Sesion iniciada con el nombre de usuario ..."), so the real-time HUD can center the hero again.

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

