using System.Globalization;
using System.IO;
using System.Text;

namespace Hud.App.Services
{
    public static partial class RealTimeSessionReportService
    {
        /// <summary>
        /// El informe de sesion en PDF (A4 apaisado), dibujado a mano con las fuentes estandar del PDF
        /// (Helvetica con codificacion WinAnsi: ahora con acentos y ñ). Cada pagina lleva una franja
        /// arriba (APH, heroe y fecha) y un pie con el numero de pagina; sin la marca de agua gigante de
        /// antes. Las stats usan la escala unica de APH (<see cref="StatScale"/>), la misma del HUD y de
        /// las ventanas, con el texto en blanco o negro segun el fondo y la leyenda en cada pagina con stats.
        /// Paginas: resumen, como jugaste, rivales, mesa por mesa, evolucion por mesa y notas de cierre.
        /// </summary>
        private static class StyledPdfWriter
        {
            private const double PageW = 842;
            private const double PageH = 595;
            private const double Margin = 36;
            private const double ContentTop = PageH - 62;
            private const double ContentBottom = 46;
            private const double ContentW = PageW - Margin * 2;

            private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-MX");

            private static readonly Rgb Ink = new(17, 24, 39);
            private static readonly Rgb Ink2 = new(75, 85, 99);
            private static readonly Rgb Muted = new(118, 127, 141);
            private static readonly Rgb Line = new(225, 229, 236);
            private static readonly Rgb Soft = new(246, 248, 251);
            private static readonly Rgb White = new(255, 255, 255);
            private static readonly Rgb Navy = new(19, 35, 60);
            private static readonly Rgb NavySoft = new(196, 207, 222);
            private static readonly Rgb Good = new(22, 135, 84);
            private static readonly Rgb GoodSoft = new(229, 245, 236);
            private static readonly Rgb Bad = new(196, 50, 60);
            private static readonly Rgb BadSoft = new(252, 233, 235);
            private static readonly Rgb Warn = new(176, 112, 20);
            private static readonly Rgb WarnSoft = new(253, 244, 224);
            private static readonly Rgb Accent = new(43, 103, 201);
            private static readonly Rgb AccentSoft = new(232, 240, 252);
            private static readonly Rgb NoSampleFill = new(229, 232, 237);
            private static readonly Rgb NoSampleInk = new(105, 114, 128);

            private static readonly Rgb[] SeriesColors =
            {
                new(43, 103, 201), new(15, 150, 130), new(224, 118, 46), new(124, 88, 214),
                new(206, 72, 128), new(120, 140, 30), new(150, 100, 60), new(90, 100, 115)
            };

            private static readonly (string Key, string Label)[] StatColumns =
            {
                ("VPIP", "VPIP"), ("PFR", "PFR"), ("THREEBET", "3Bet"), ("AF", "AF"), ("AFQ", "AFq"),
                ("CBF", "CBet"), ("FVCBF", "FvCB"), ("WTSD", "WTSD"), ("WSD", "W$SD"), ("WWSF", "WWSF")
            };

            public static void Write(string path, SessionReportModel model)
            {
                var doc = new Doc();
                DrawSummaryPage(doc, model);
                DrawCoachingPage(doc, model);
                DrawRivalsPage(doc, model);
                DrawTablesPages(doc, model);
                DrawEvolutionPage(doc, model);
                DrawClosingPage(doc, model);

                for (var i = 0; i < doc.Pages.Count; i++)
                    DrawChrome(doc.Pages[i], model, i + 1, doc.Pages.Count);
                WriteObjects(path, doc.Pages.Select(page => page.Content).ToList(), model);
            }

            // ------------------------------------------------------------------ paginas

            private static void DrawSummaryPage(Doc doc, SessionReportModel model)
            {
                doc.NewPage();
                var c = doc.C;
                var coach = model.Coaching;
                var y = doc.Y;
                PageTitle(c, ref y, "Resumen de la sesión", Subtitle(model));

                // Resultado y grafica de la sesion.
                const double cardH = 158;
                const double leftW = 252;
                var netBb = coach.Hands > 0 ? coach.NetBb : model.NetBb;
                var hands = coach.Hands > 0 ? coach.Hands : model.TotalHands;
                var per100 = hands == 0 ? 0 : netBb * 100 / hands;
                var adjusted = netBb - coach.LuckBb;
                Card(c, Margin, y - cardH, leftW, cardH);
                c.Text("RESULTADO", Margin + 16, y - 22, 7.5, Muted, bold: true);
                c.Text(Bb(netBb), Margin + 16, y - 58, 30, netBb >= 0 ? Good : Bad, bold: true);
                c.Text(Money(model.NetAmount, model.IsCash), Margin + 16, y - 76, 10, Ink2);
                c.Line(Margin + 16, y - 90, Margin + leftW - 16, y - 90, Line, 0.8);
                KeyValue(c, Margin + 16, y - 106, leftW - 32, "Cada 100 manos", Bb(per100), per100 >= 0 ? Good : Bad);
                KeyValue(c, Margin + 16, y - 124, leftW - 32, "Sin la suerte de los all-in", Bb(adjusted), adjusted >= 0 ? Good : Bad);
                var luckText = Math.Abs(coach.LuckBb) < 10
                    ? "La suerte en los all-in casi no influyó."
                    : coach.LuckBb > 0
                        ? $"La suerte en los all-in te sumó {Num(coach.LuckBb)} bb."
                        : $"La mala suerte en los all-in te quitó {Num(-coach.LuckBb)} bb.";
                c.Text(luckText, Margin + 16, y - 144, 7.5, Muted);

                var chartX = Margin + leftW + 14;
                var chartW = PageW - Margin - chartX;
                Card(c, chartX, y - cardH, chartW, cardH);
                c.Text("Cómo fue la sesión, mano a mano (bb acumuladas)", chartX + 14, y - 20, 9, Ink, bold: true);
                DrawSessionCurve(c, coach.Curve, chartX + 14, y - cardH + 14, chartW - 28, cardH - 44);
                y -= cardH + 18;

                // Stats del heroe con la escala de colores.
                SectionTitle(c, ref y, "Tus stats en la sesión", "Comparadas con jugadores normales de 6-max");
                var stats = model.HeroSummary.Stats;
                var verdicts = model.Feedback.ToDictionary(item => item.Key, item => item.Verdict);
                StatTiles(c, Margin, y, ContentW, 52, stats, 13, verdicts);
                y -= 62;
                Legend(c, Margin, y, withVerdicts: verdicts.Count > 0);
                y -= verdicts.Count > 0 ? 42 : 30;

                // Lo mas destacado.
                var cards = new List<(string Label, string Main, string Sub, Rgb Tone)>();
                if (model.BestHand is { } best)
                    cards.Add(("MEJOR MANO", $"{best.Combo}   {Bb(best.NetBb)}", $"{best.TableName} · {Time(best.Time)}", Good));
                if (model.WorstHand is { } worst)
                    cards.Add(("PEOR MANO", $"{worst.Combo}   {Bb(worst.NetBb)}", $"{worst.TableName} · {Time(worst.Time)}", Bad));
                foreach (var rival in model.RivalImpacts.Where(rival => rival.Label.Contains("rentable") || rival.Label.Contains("costó")))
                    cards.Add((rival.Label.ToUpper(Es), rival.Name, $"{rival.Hands} manos · {Bb(rival.NetBb)} aprox.", rival.NetBb >= 0 ? Good : Bad));
                var count = Math.Min(4, cards.Count);
                if (count > 0)
                {
                    var w = (ContentW - (count - 1) * 12) / count;
                    for (var i = 0; i < count; i++)
                    {
                        var x = Margin + i * (w + 12);
                        Card(c, x, y - 58, w, 58, accent: cards[i].Tone);
                        c.Text(cards[i].Label, x + 14, y - 16, 7, Muted, bold: true);
                        c.Text(Fit(cards[i].Main, w - 28, 12, true), x + 14, y - 34, 12, Ink, bold: true);
                        c.Text(Fit(cards[i].Sub, w - 28, 8, false), x + 14, y - 49, 8, Ink2);
                    }
                    y -= 72;
                }

                // Lo mas importante: lo que la retroalimentacion marca en rojo o ambar (o que todo va bien).
                var toFix = new List<(StatVerdict Verdict, string Text)>();
                if (model.Coaching.DecisionVerdict is { Verdict: not StatVerdict.Good } decisions)
                    toFix.Add((decisions.Verdict, $"{decisions.VerdictLabel}: {decisions.Sentence}"));
                toFix.AddRange(model.Feedback.Where(item => item.Verdict != StatVerdict.Good)
                    .Select(item => (item.Verdict, $"{item.VerdictLabel}: {item.Sentence}")));
                var worstVerdict = toFix.Count == 0 ? StatVerdict.Good : toFix.Max(item => item.Verdict);
                var text = toFix.Count == 0 ? model.Focus : string.Join("  ", toFix.Take(2).Select(item => item.Text));
                if (y - 40 > ContentBottom)
                    Callout(c, ref y, "Lo más importante", text,
                        worstVerdict switch { StatVerdict.Good => GoodSoft, StatVerdict.Adjust => WarnSoft, _ => BadSoft },
                        worstVerdict switch { StatVerdict.Good => Good, StatVerdict.Adjust => Warn, _ => Bad });
                doc.Y = y;
            }

            private static void DrawCoachingPage(Doc doc, SessionReportModel model)
            {
                doc.NewPage();
                var c = doc.C;
                var coach = model.Coaching;
                var y = doc.Y;
                PageTitle(c, ref y, "Cómo jugaste", "La suerte en los all-in, los consejos de APH en vivo y qué manos te dejaron dinero.");

                var per100 = coach.Hands == 0 ? 0 : coach.NetBb * 100 / coach.Hands;
                var adjusted = coach.NetBb - coach.LuckBb;
                var claro = $"Jugaste {coach.Hands} manos y {(coach.NetBb >= 0 ? "ganaste" : "perdiste")} {Num(Math.Abs(coach.NetBb))} bb ({Bb(per100)} cada 100 manos). " +
                            $"Sin la suerte de los all-in habrían sido {Bb(adjusted)}: " +
                            (Math.Abs(coach.LuckBb) < 10 ? "la suerte casi no influyó." : coach.LuckBb > 0 ? $"la suerte te dio {Num(coach.LuckBb)} bb." : $"la mala suerte te quitó {Num(-coach.LuckBb)} bb.") +
                            $" Entraste en el {coach.Vpip:0}% de las manos y subiste en el {coach.Pfr:0}%.";
                Callout(c, ref y, "En claro", claro, AccentSoft, Accent);

                // Columna izquierda: all-in. Derecha: consejos.
                var top = y;
                var leftW = ContentW * 0.6;
                var rightX = Margin + leftW + 16;
                var rightW = PageW - Margin - rightX;

                SectionTitle(c, ref y, "Suerte en los all-in", "lo que salió después de meterlo todo", width: leftW);
                if (coach.AllIns.Count == 0)
                {
                    c.Text("No hubo all-in con cartas por salir en esta sesión.", Margin, y - 10, 9, Ink2);
                    y -= 22;
                }
                else
                {
                    var columns = new (string Header, double Width, bool Right)[]
                    {
                        ("Tu mano", 58, false), ("Contra", 118, false), ("Calle", 50, false),
                        ("Ibas con", 50, true), ("Resultado", 62, true), ("Suerte", leftW - 338, true)
                    };
                    TableHeader(c, Margin, y, columns.Select(col => (col.Header, col.Width, col.Right)).ToArray());
                    y -= 18;
                    foreach (var allIn in coach.AllIns)
                    {
                        var cells = new[]
                        {
                            Cell(allIn.Cards, Ink, bold: true), Cell(Fit(allIn.Against, 112, 8, false), Ink2), Cell(allIn.Street, Ink2),
                            Cell($"{allIn.Equity * 100:0}%", Ink), Cell(Bb(allIn.NetBb), allIn.NetBb >= 0 ? Good : Bad, bold: true),
                            Cell(Bb(allIn.LuckBb), allIn.LuckBb >= 0 ? Good : Bad, bold: true, fill: allIn.LuckBb >= 0 ? GoodSoft : BadSoft)
                        };
                        TableRow(c, Margin, y, columns.Select(col => (col.Width, col.Right)).ToArray(), cells, 16);
                        y -= 16;
                    }
                    c.Text("Suerte = lo que ganaste o perdiste de más respecto a lo que te tocaba con tu equity.", Margin, y - 10, 7, Muted);
                    y -= 20;
                }
                var leftBottom = y;

                var ry = top;
                SectionTitle(c, ref ry, "Consejos de APH en vivo", null, x: rightX, width: rightW);
                if (coach.Decisions == 0)
                {
                    foreach (var line in Wrap("No hay registro de consejos para estas manos (la sesión no tenía el solver en la mesa, o es de antes de que APH los guardara).", rightW, 8.5, false))
                    {
                        c.Text(line, rightX, ry - 10, 8.5, Ink2);
                        ry -= 12;
                    }
                }
                else
                {
                    var advicePct = coach.WithAdvice * 100.0 / Math.Max(1, coach.Decisions);
                    var followedPct = coach.Followed * 100.0 / Math.Max(1, coach.WithAdvice);
                    var kpis = new (string Label, string Value, Rgb Tone)[]
                    {
                        ("Con consejo", $"{advicePct:0}%", Ink),
                        ("Lo seguiste", $"{followedPct:0}%", followedPct >= 85 ? Good : Warn),
                        ("Errores", coach.Mistakes.ToString(Es), coach.Mistakes == 0 ? Good : Bad)
                    };
                    var kw = (rightW - 16) / 3;
                    for (var i = 0; i < kpis.Length; i++)
                    {
                        var x = rightX + i * (kw + 8);
                        Card(c, x, ry - 50, kw, 50, fill: Soft);
                        c.Text(kpis[i].Label, x + 10, ry - 15, 7.5, Muted, bold: true);
                        c.Text(kpis[i].Value, x + 10, ry - 38, 17, kpis[i].Tone, bold: true);
                    }
                    ry -= 62;
                    var detail = $"APH te aconsejó en {coach.WithAdvice} de tus {coach.Decisions} decisiones y seguiste {coach.Followed}.";
                    if (coach.ExploitHands > 0)
                        detail += $" El aviso EXPLOTAR salió en {coach.ExploitHands} manos, que suman {Bb(coach.ExploitNetBb)}.";
                    foreach (var line in Wrap(detail, rightW, 8.5, false))
                    {
                        c.Text(line, rightX, ry - 8, 8.5, Ink2);
                        ry -= 12;
                    }
                    if (coach.DecisionVerdict is { } verdict)
                    {
                        c.Text("Tus decisiones:", rightX, ry - 12, 8.5, Ink, bold: true);
                        VerdictPill(c, rightX + PdfCanvas.Width("Tus decisiones:", 8.5, true) + 8, ry - 9, verdict.Verdict);
                        ry -= 26;
                    }
                    ry -= 6;
                }

                // Fila de abajo: errores a la izquierda y tipos de mano a la derecha.
                y = Math.Min(leftBottom, ry) - 6;
                var rowTop = y;
                if (coach.MistakeLines.Count > 0)
                {
                    SectionTitle(c, ref y, "Los errores más caros", "sin contar la suerte de los all-in", width: leftW);
                    foreach (var mistake in coach.MistakeLines)
                    {
                        var head = $"{mistake.At:HH:mm} · {mistake.Table} · [{mistake.Cards}] · {Street(mistake.Street)} · {Bb(mistake.NetBb)}" +
                                   (Math.Abs(mistake.LuckBb) >= 1 ? $" (sin suerte {Bb(mistake.AdjustedBb)})" : "");
                        var what = $"Hiciste {mistake.Chosen}" + (string.IsNullOrWhiteSpace(mistake.Advice) ? "." : $"; APH decía {mistake.Advice}.");
                        var lines = Wrap(Capitalize(mistake.Why), leftW - 16, 8, false).Take(2).ToList();
                        var h = 30 + lines.Count * 11;
                        if (y - h < ContentBottom)
                            break;
                        c.FillRect(Margin, y - h, 3, h, Bad);
                        c.Text(Fit(head, leftW - 16, 7.5, true), Margin + 12, y - 10, 7.5, Muted, bold: true);
                        c.Text(Fit(what, leftW - 16, 9, true), Margin + 12, y - 23, 9, Ink, bold: true);
                        var ly = y - 35;
                        foreach (var line in lines)
                        {
                            c.Text(line, Margin + 12, ly, 8, Ink2);
                            ly -= 11;
                        }
                        y -= h + 8;
                    }
                }

                // Tipos de mano: barra desde el centro (cero) hacia la derecha si ganan y a la izquierda si pierden.
                var types = coach.HandTypes.Where(row => row.Hands > 0).OrderByDescending(row => row.AdjustedPerHand).ToList();
                var shown = types.Take(3).Concat(types.Skip(Math.Max(3, types.Count - 3))).Distinct().ToList();
                var ty = rowTop;
                if (shown.Count > 0)
                {
                    SectionTitle(c, ref ty, "Qué manos te dejaron dinero", null, x: rightX, width: rightW);
                    c.Text("bb por mano sin la suerte de los all-in. Con menos de 30 manos es solo una pista.", rightX, ty + 2, 7, Muted);
                    ty -= 8;
                    var max = Math.Max(1, shown.Max(row => Math.Abs(row.AdjustedPerHand)));
                    var zero = rightX + rightW / 2;
                    foreach (var row in shown)
                    {
                        if (ty - 26 < ContentBottom)
                            break;
                        var tone = row.AdjustedPerHand >= 0 ? Good : Bad;
                        var value = $"{row.AdjustedPerHand.ToString("+0.0;-0.0;0.0", Es)} bb/mano";
                        var valueW = PdfCanvas.Width(value, 8, true);
                        var handsText = row.Hands == 1 ? "1 mano" : $"{row.Hands} manos";
                        c.Text(Fit(row.Name, rightW - valueW - 70, 8, false), rightX, ty - 9, 8, Ink);
                        c.TextRight(handsText, rightX + rightW - valueW - 8, ty - 9, 7, Muted);
                        c.TextRight(value, rightX + rightW, ty - 9, 8, tone, bold: true);
                        c.FillRoundRect(rightX, ty - 18, rightW, 5, 2.5, Soft);
                        var len = Math.Max(2, Math.Abs(row.AdjustedPerHand) / max * (rightW / 2));
                        c.FillRoundRect(row.AdjustedPerHand >= 0 ? zero : zero - len, ty - 18, len, 5, 2.5, tone);
                        c.Line(zero, ty - 20, zero, ty - 11, new Rgb(160, 168, 180), 0.8);
                        ty -= 26;
                    }
                }
                y = Math.Min(y, ty);
                doc.Y = y;
            }

            private static void DrawRivalsPage(Doc doc, SessionReportModel model)
            {
                doc.NewPage();
                var c = doc.C;
                var y = doc.Y;
                PageTitle(c, ref y, "Rivales de la sesión", "Los jugadores con más manos en esta sesión. Cada stat tiene el color de la escala de APH.");
                var rows = model.TopPlayers.Take(20).ToList();
                PlayersTable(c, ref y, rows, model.Hero);
                y -= 10;
                Legend(c, Margin, y);
                y -= 30;
                doc.Y = y;
            }

            private static void DrawTablesPages(Doc doc, SessionReportModel model)
            {
                doc.NewPage();
                var c = doc.C;
                var y = doc.Y;
                PageTitle(c, ref y, "Mesa por mesa", "Tu resultado y tus stats en cada mesa, y los rivales con más manos en ella.");
                foreach (var table in model.Tables)
                {
                    var players = table.Players.Take(8).ToList();
                    if (table.Players.FirstOrDefault(player => SamePlayer(player.Name, model.Hero)) is { } heroRow && !players.Contains(heroRow))
                        players[^1] = heroRow;
                    var need = 48 + 18 + players.Count * 16 + 16;
                    if (y - need < ContentBottom)
                    {
                        doc.NewPage();
                        c = doc.C;
                        y = doc.Y;
                    }

                    var tone = table.NetBb >= 0 ? Good : Bad;
                    c.FillRoundRect(Margin, y - 38, ContentW, 38, 6, Soft);
                    c.FillRect(Margin, y - 38, 4, 38, tone);
                    c.Text(Fit(table.Table.TableName, ContentW - 240, 13, true), Margin + 14, y - 16, 13, Ink, bold: true);
                    var hands = table.HeroStats?.Hands ?? 0;
                    var per100 = hands == 0 ? 0 : table.NetBb * 100 / hands;
                    c.Text($"{Time(table.Table.FirstHandTime)} – {Time(table.Table.LastHandTime)} · {hands} manos · {table.Players.Count} jugadores · {Bb(per100)} cada 100 manos",
                        Margin + 14, y - 30, 8, Ink2);
                    c.TextRight(Bb(table.NetBb), PageW - Margin - 14, y - 18, 14, tone, bold: true);
                    c.TextRight(Money(table.NetAmount, table.IsCash), PageW - Margin - 14, y - 30, 8, Ink2);
                    y -= 48;

                    PlayersTable(c, ref y, players, model.Hero);
                    y -= 16;
                }
                Legend(c, Margin, Math.Max(y, ContentBottom + 26));
                doc.Y = y;
            }

            private static void DrawEvolutionPage(Doc doc, SessionReportModel model)
            {
                doc.NewPage();
                var c = doc.C;
                var y = doc.Y;
                PageTitle(c, ref y, "Evolución por mesa", "Bb acumuladas de cada mesa a lo largo de la sesión (en tu hora local).");

                const double chartH = 210;
                Card(c, Margin, y - chartH, ContentW, chartH);
                var series = model.Tables
                    .Select((table, index) => (Table: table, Color: SeriesColors[index % SeriesColors.Length],
                        Points: CumulativeByTime(table).ToList()))
                    .Where(item => item.Points.Count > 1)
                    .ToList();
                var first = model.FirstHand ?? DateTime.MinValue;
                var last = model.LastHand ?? first.AddMinutes(1);
                var span = Math.Max(1, (last - first).TotalSeconds);
                var plotX = Margin + 58;
                var plotY = y - chartH + 30;
                var plotW = ContentW - 76;
                var plotH = chartH - 48;
                var values = series.SelectMany(item => item.Points.Select(point => point.Bb)).Append(0).ToList();
                var (min, max, ticks) = NiceScale(values.Min(), values.Max(), 5);
                foreach (var tick in ticks)
                {
                    var ty = Map(tick, min, max, plotY, plotH);
                    c.Line(plotX, ty, plotX + plotW, ty, tick == 0 ? new Rgb(150, 158, 170) : Line, tick == 0 ? 0.9 : 0.5);
                    c.TextRight($"{Num(tick)} bb", plotX - 6, ty - 3, 7, Muted);
                }
                // Horas en el eje X.
                for (var i = 0; i <= 4; i++)
                {
                    var at = first.AddSeconds(span * i / 4);
                    var tx = plotX + plotW * i / 4;
                    c.TextCenter(Time(at), tx, plotY - 14, 7, Muted);
                }
                foreach (var item in series)
                {
                    var points = item.Points.Select(point => (plotX + Math.Clamp((point.At - first).TotalSeconds / span, 0, 1) * plotW,
                        Map(point.Bb, min, max, plotY, plotH))).ToList();
                    c.Polyline(points, item.Color, 1.7);
                }
                y -= chartH + 10;

                // Leyenda con el nombre completo y el resultado.
                var lx = Margin;
                foreach (var item in series)
                {
                    var label = $"{item.Table.Table.TableName}  {Bb(item.Table.NetBb)}";
                    var w = PdfCanvas.Width(label, 8, false) + 30;
                    if (lx + w > PageW - Margin)
                    {
                        lx = Margin;
                        y -= 14;
                    }
                    c.FillRoundRect(lx, y - 8, 14, 4, 2, item.Color);
                    c.Text(label, lx + 19, y - 9, 8, Ink);
                    lx += w;
                }
                y -= 26;

                SectionTitle(c, ref y, "Resumen de mesas", null);
                var columns = new (string Header, double Width, bool Right)[]
                {
                    ("Mesa", 196, false), ("Manos", 54, true), ("Inicio", 56, true), ("Última", 56, true), ("Resultado", 104, true),
                    ("bb", 70, true), ("bb/100", 64, true), ("VPIP", 56, true), ("PFR", 56, true), ("3Bet", ContentW - 768 + 56, true)
                };
                TableHeader(c, Margin, y, columns.Select(col => (col.Header, col.Width, col.Right)).ToArray());
                y -= 18;
                foreach (var table in model.Tables)
                {
                    if (y - 16 < ContentBottom)
                        break;
                    var hero = table.HeroStats;
                    var hands = hero?.Hands ?? 0;
                    var tone = table.NetBb >= 0 ? Good : Bad;
                    var cells = new[]
                    {
                        Cell(table.Table.TableName, Ink, bold: true), Cell(hands.ToString("N0", Es), Ink2),
                        Cell(Time(table.Table.FirstHandTime), Ink2), Cell(Time(table.Table.LastHandTime), Ink2),
                        Cell(Money(table.NetAmount, table.IsCash), tone), Cell(Bb(table.NetBb), tone, bold: true),
                        Cell(hands == 0 ? "–" : (table.NetBb * 100 / hands).ToString("+0.0;-0.0;0.0", Es), tone),
                        StatCell("VPIP", hero?.VPIP ?? 0, hands), StatCell("PFR", hero?.PFR ?? 0, hands), StatCell("THREEBET", hero?.ThreeBet ?? 0, hands)
                    };
                    TableRow(c, Margin, y, columns.Select(col => (col.Width, col.Right)).ToArray(), cells, 17);
                    y -= 17;
                }
                doc.Y = y;
            }

            /// <summary>
            /// Retroalimentacion: cada stat con su nivel (color de la escala) y su veredicto con semaforo
            /// (verde bien, ambar por ajustar, rojo a mejorar) y que hacer; luego las notas de la sesion y
            /// el siguiente enfoque.
            /// </summary>
            private static void DrawClosingPage(Doc doc, SessionReportModel model)
            {
                doc.NewPage();
                var c = doc.C;
                var y = doc.Y;
                PageTitle(c, ref y, "Retroalimentación", model.IsCash
                    ? "Tus decisiones y cada stat con su veredicto: verde bien, ámbar por ajustar, rojo a mejorar."
                    : "Tus decisiones y cada stat con su veredicto para dinero ficticio (aquí te pagan casi todo): verde bien, ámbar por ajustar, rojo a mejorar.");

                if (model.Feedback.Count == 0)
                {
                    c.Text("Con menos de 30 manos todavía no hay retroalimentación de tus stats.", Margin, y - 10, 9, Ink2);
                    y -= 24;
                }
                else
                {
                    var columns = new (string Header, double Width, bool Right)[]
                    {
                        ("Stat", 62, false), ("Tu valor", 62, true), ("Nivel", 104, false), ("Veredicto", 112, false), ("Qué hacer", ContentW - 340, false)
                    };
                    TableHeader(c, Margin, y, columns);
                    y -= 18;
                    if (model.Coaching.DecisionVerdict is { } decisions)
                    {
                        var lines = Wrap(decisions.Advice, columns[4].Width - 12, 8.5, false).Take(3).ToList();
                        var h = Math.Max(28, 7 + lines.Count * 11);
                        var mid = y - h / 2;
                        c.FillRect(Margin, y - h, ContentW, h, Soft);
                        c.Line(Margin, y - h, Margin + ContentW, y - h, Line, 0.5);
                        c.Text("Decisiones", Margin + 6, mid - 3, 9, Ink, bold: true);
                        var errors = decisions.Mistakes == 1 ? "1 error" : $"{decisions.Mistakes} errores";
                        c.TextRight(errors, Margin + 62 + 62 - 6, mid - 3, 9, Ink, bold: true);
                        var followed = $"seguiste el {decisions.FollowedPct:0}%";
                        var chipW = PdfCanvas.Width(followed, 7.5, true) + 14;
                        c.FillRoundRect(Margin + 124 + 6, mid - 7, chipW, 14, 3, NoSampleFill);
                        c.TextCenter(followed, Margin + 124 + 6 + chipW / 2, mid - 3, 7.5, Ink, bold: true);
                        VerdictPill(c, Margin + 228 + 6, mid, decisions.Verdict);
                        var dy = mid + (lines.Count - 1) * 5.5 - 3;
                        foreach (var line in lines)
                        {
                            c.Text(line, Margin + 340 + 6, dy, 8.5, Ink);
                            dy -= 11;
                        }
                        y -= h;
                    }
                    foreach (var item in model.Feedback)
                    {
                        var lines = Wrap(item.Advice, columns[4].Width - 12, 8.5, false).Take(2).ToList();
                        var h = Math.Max(20, 9 + lines.Count * 11);
                        var mid = y - h / 2;
                        c.Line(Margin, y - h, Margin + ContentW, y - h, Line, 0.5);
                        c.Text(item.Name, Margin + 6, mid - 3, 9, Ink, bold: true);
                        c.TextRight(item.ValueLabel, Margin + 62 + 62 - 6, mid - 3, 9, Ink, bold: true);

                        var (fill, ink) = Colors(item.Level);
                        var levelW = PdfCanvas.Width(item.LevelLabel, 7.5, true) + 14;
                        c.FillRoundRect(Margin + 124 + 6, mid - 7, levelW, 14, 3, fill);
                        c.TextCenter(item.LevelLabel, Margin + 124 + 6 + levelW / 2, mid - 3, 7.5, ink, bold: true);

                        VerdictPill(c, Margin + 228 + 6, mid, item.Verdict);

                        var ly = mid + (lines.Count - 1) * 5.5 - 3;
                        foreach (var line in lines)
                        {
                            c.Text(line, Margin + 340 + 6, ly, 8.5, Ink);
                            ly -= 11;
                        }
                        y -= h;
                    }
                    y -= 16;
                }

                // Lo mas importante primero: el siguiente enfoque, justo debajo de la tabla.
                Callout(c, ref y, "Siguiente enfoque", model.Focus, WarnSoft, Warn);

                var good = model.ClosingNotes.Where(note => note.Tone == NoteTone.Good).Select(note => note.Text).ToList();
                var watch = model.ClosingNotes.Where(note => note.Tone == NoteTone.Watch).Select(note => note.Text).ToList();
                var info = model.ClosingNotes.Where(note => note.Tone == NoteTone.Info).Select(note => note.Text).ToList();
                var need = 22 + Math.Max(good.Count, watch.Count) * 29.5;
                if (y - need < ContentBottom)
                {
                    doc.NewPage();
                    c = doc.C;
                    y = doc.Y;
                }
                var colW = (ContentW - 18) / 2;
                var top = y;
                var leftY = NoteColumn(c, Margin, top, colW, "Lo que salió bien en la sesión", good, Good, GoodSoft);
                var rightY = NoteColumn(c, Margin + colW + 18, top, colW, "A vigilar", watch, Bad, BadSoft);
                // El contexto va debajo de la columna mas corta, para no gastar otra fila.
                if (info.Count > 0)
                {
                    if (rightY >= leftY)
                        rightY = NoteColumn(c, Margin + colW + 18, rightY - 4, colW, "Contexto", info, Muted, Soft);
                    else
                        leftY = NoteColumn(c, Margin, leftY - 4, colW, "Contexto", info, Muted, Soft);
                }
                y = Math.Min(leftY, rightY) - 8;
                doc.Y = y;
            }

            /// <summary>Pastilla del veredicto: icono dibujado (palomita, ! o tache) y la palabra.</summary>
            private static void VerdictPill(PdfCanvas c, double x, double mid, StatVerdict verdict)
            {
                var color = HeroStatCoach.ColorOf(verdict);
                var tone = new Rgb(color.R, color.G, color.B);
                var label = HeroStatCoach.LabelOf(verdict);
                var w = PdfCanvas.Width(label, 8, true) + 30;
                c.FillRoundRect(x, mid - 8, w, 16, 8, tone);
                VerdictIcon(c, x + 9, mid, 5, verdict, White, tone);
                c.Text(label, x + 19, mid - 3, 8, White, bold: true);
            }

            /// <summary>Icono del veredicto en un circulo: palomita (bien), ! (por ajustar) o tache (a mejorar).</summary>
            private static void VerdictIcon(PdfCanvas c, double cx, double cy, double r, StatVerdict verdict, Rgb circle, Rgb glyph)
            {
                c.FillCircle(cx, cy, r, circle);
                switch (verdict)
                {
                    case StatVerdict.Good:
                        c.Polyline(new[] { (cx - r * 0.5, cy), (cx - r * 0.1, cy - r * 0.42), (cx + r * 0.55, cy + r * 0.45) }, glyph, Math.Max(1, r * 0.3));
                        break;
                    case StatVerdict.Adjust:
                        c.Line(cx, cy + r * 0.55, cx, cy - r * 0.05, glyph, Math.Max(1, r * 0.3));
                        c.FillCircle(cx, cy - r * 0.45, r * 0.16, glyph);
                        break;
                    default:
                        c.Line(cx - r * 0.42, cy - r * 0.42, cx + r * 0.42, cy + r * 0.42, glyph, Math.Max(1, r * 0.3));
                        c.Line(cx - r * 0.42, cy + r * 0.42, cx + r * 0.42, cy - r * 0.42, glyph, Math.Max(1, r * 0.3));
                        break;
                }
            }

            // ------------------------------------------------------------------ piezas

            private static void DrawChrome(PdfCanvas c, SessionReportModel model, int page, int pages)
            {
                c.FillRect(0, PageH - 40, PageW, 40, Navy);
                c.Text("APH", Margin, PageH - 25, 13, White, bold: true);
                c.Text("Informe de sesión", Margin + 36, PageH - 25, 10, NavySoft);
                var date = model.FirstHand is { } first ? Local(first).ToString("d 'de' MMMM 'de' yyyy", Es) : "";
                c.TextRight($"{model.Hero}   ·   {date}", PageW - Margin, PageH - 25, 9, NavySoft);

                c.Line(Margin, 32, PageW - Margin, 32, Line, 0.8);
                c.Text($"Analyzer Poker Hands · generado el {model.GeneratedAt.ToString("d 'de' MMMM 'de' yyyy, HH:mm", Es)}", Margin, 20, 7, Muted);
                c.TextRight($"Página {page} de {pages}", PageW - Margin, 20, 7, Muted);
            }

            private static void PageTitle(PdfCanvas c, ref double y, string title, string subtitle)
            {
                c.Text(title, Margin, y - 16, 21, Ink, bold: true);
                c.Text(Fit(subtitle, ContentW, 9.5, false), Margin, y - 33, 9.5, Ink2);
                y -= 50;
            }

            private static string Subtitle(SessionReportModel model)
            {
                var parts = new List<string>();
                if (model.FirstHand is { } first)
                {
                    var day = Local(first).ToString("dddd d 'de' MMMM", Es);
                    parts.Add(char.ToUpper(day[0], Es) + day[1..]);
                    parts.Add($"{Time(model.FirstHand)} – {Time(model.LastHand)}");
                }
                if (model.Duration > TimeSpan.Zero)
                    parts.Add(Duration(model.Duration));
                parts.Add(model.RegisteredTables == 1 ? "1 mesa" : $"{model.RegisteredTables} mesas");
                parts.Add($"{model.TotalHands.ToString("N0", Es)} manos");
                return string.Join("  ·  ", parts);
            }

            private static void SectionTitle(PdfCanvas c, ref double y, string title, string? hint, double x = Margin, double width = ContentW)
            {
                c.FillRect(x, y - 13, 3, 13, Accent);
                c.Text(title, x + 9, y - 11, 11, Ink, bold: true);
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var titleW = PdfCanvas.Width(title, 11, true);
                    c.Text(Fit(hint!, width - titleW - 24, 8, false), x + 9 + titleW + 10, y - 11, 8, Muted);
                }
                y -= 22;
            }

            private static void Card(PdfCanvas c, double x, double y, double w, double h, Rgb? fill = null, Rgb? accent = null)
            {
                c.FillRoundRect(x, y, w, h, 7, fill ?? White);
                c.StrokeRoundRect(x, y, w, h, 7, Line, 0.9);
                if (accent is { } tone)
                    c.FillRoundRect(x, y + h - 3, w, 3, 1.5, tone);
            }

            private static void KeyValue(PdfCanvas c, double x, double y, double w, string key, string value, Rgb tone)
            {
                c.Text(key, x, y, 8.5, Ink2);
                c.TextRight(value, x + w, y, 9.5, tone, bold: true);
            }

            private static void Callout(PdfCanvas c, ref double y, string title, string text, Rgb fill, Rgb tone)
            {
                var lines = Wrap(text, ContentW - 30, 9, false).ToList();
                var h = 26 + lines.Count * 12;
                c.FillRoundRect(Margin, y - h, ContentW, h, 6, fill);
                c.FillRect(Margin, y - h, 4, h, tone);
                c.Text(title, Margin + 16, y - 15, 9, tone, bold: true);
                var ly = y - 29;
                foreach (var line in lines)
                {
                    c.Text(line, Margin + 16, ly, 9, Ink);
                    ly -= 12;
                }
                y -= h + 14;
            }

            private static double NoteColumn(PdfCanvas c, double x, double y, double w, string title, IReadOnlyList<string> notes, Rgb tone, Rgb fill)
            {
                if (notes.Count == 0)
                    return y;
                c.FillRect(x, y - 13, 3, 13, tone);
                c.Text(title, x + 9, y - 11, 11, Ink, bold: true);
                y -= 22;
                foreach (var note in notes)
                {
                    var lines = Wrap(note, w - 30, 8.5, false).ToList();
                    var h = 12 + lines.Count * 11.5;
                    c.FillRoundRect(x, y - h, w, h, 5, fill);
                    c.FillCircle(x + 11, y - 11, 2.6, tone);
                    var ly = y - 14;
                    foreach (var line in lines)
                    {
                        c.Text(line, x + 20, ly, 8.5, Ink);
                        ly -= 11.5;
                    }
                    y -= h + 6;
                }
                return y;
            }

            /// <summary>Una fila de cajas con el color de la escala: nombre, valor y nivel ("Normal", "Muy alto"...).</summary>
            private static void StatTiles(PdfCanvas c, double x, double y, double w, double h, RealTimePlayerReportRow stats, double valueSize,
                IReadOnlyDictionary<string, StatVerdict>? verdicts = null)
            {
                const double gap = 6;
                var tileW = (w - gap * (StatColumns.Length - 1)) / StatColumns.Length;
                for (var i = 0; i < StatColumns.Length; i++)
                {
                    var (key, label) = StatColumns[i];
                    var value = StatValue(stats, key);
                    var level = StatScale.Level(key, value, stats.Hands);
                    var (fill, ink) = Colors(level);
                    var tx = x + i * (tileW + gap);
                    c.FillRoundRect(tx, y - h, tileW, h, 5, fill);
                    c.TextCenter(label, tx + tileW / 2, y - (h > 40 ? 13 : 10), 7, ink, bold: true);
                    c.TextCenter(StatScale.Format(key, value), tx + tileW / 2, y - (h > 40 ? 31 : 23), valueSize, ink, bold: true);
                    c.TextCenter(level == StatLevel.NoSample ? "pocas manos" : StatScale.LabelOf(level), tx + tileW / 2, y - h + (h > 40 ? 8 : 5), h > 40 ? 7 : 6, ink);
                    if (verdicts is not null && verdicts.TryGetValue(key, out var verdict))
                    {
                        var color = HeroStatCoach.ColorOf(verdict);
                        VerdictIcon(c, tx + tileW - 9, y - 9, 6.5, verdict, White, new Rgb(color.R, color.G, color.B));
                    }
                }
            }

            private static void Legend(PdfCanvas c, double x, double y, bool withVerdicts = false)
            {
                c.Text("Cómo leer los colores:", x, y - 9, 7.5, Ink2, bold: true);
                var lx = x + PdfCanvas.Width("Cómo leer los colores:", 7.5, true) + 8;
                foreach (var level in StatScale.Scale.Append(StatLevel.NoSample))
                {
                    var (fill, ink) = Colors(level);
                    var label = level == StatLevel.NoSample ? "menos de 30 manos" : StatScale.LabelOf(level);
                    var w = PdfCanvas.Width(label, 7, true) + 14;
                    c.FillRoundRect(lx, y - 13, w, 13, 3, fill);
                    c.TextCenter(label, lx + w / 2, y - 9.5, 7, ink, bold: true);
                    lx += w + 4;
                }
                c.Text("Azul = menos que lo normal · verde = normal · naranja y rojo = más que lo normal, comparado con jugadores normales de 6-max. " +
                       "Si eso es bueno o malo depende de la stat.", x, y - 24, 7, Muted);
                if (!withVerdicts)
                    return;
                c.Text("Retroalimentación (icono de cada caja):", x, y - 37, 7.5, Ink2, bold: true);
                var vx = x + PdfCanvas.Width("Retroalimentación (icono de cada caja):", 7.5, true) + 10;
                foreach (var verdict in new[] { StatVerdict.Good, StatVerdict.Adjust, StatVerdict.Bad })
                {
                    var color = HeroStatCoach.ColorOf(verdict);
                    VerdictIcon(c, vx + 5, y - 34.5, 5, verdict, new Rgb(color.R, color.G, color.B), White);
                    var label = HeroStatCoach.LabelOf(verdict);
                    c.Text(label, vx + 13, y - 37, 7.5, Ink);
                    vx += PdfCanvas.Width(label, 7.5, false) + 28;
                }
                c.Text("· el detalle y qué hacer, en la página de Retroalimentación.", vx - 10, y - 37, 7, Muted);
            }

            private static void PlayersTable(PdfCanvas c, ref double y, IReadOnlyList<RealTimePlayerReportRow> players, string hero)
            {
                var statW = (ContentW - 190 - 50) / StatColumns.Length;
                var columns = new List<(string Header, double Width, bool Right)> { ("Jugador", 190, false), ("Manos", 50, true) };
                columns.AddRange(StatColumns.Select(col => (col.Label, statW, true)));
                TableHeader(c, Margin, y, columns.ToArray());
                y -= 18;
                foreach (var player in players)
                {
                    if (y - 16 < ContentBottom)
                        break;
                    var isHero = SamePlayer(player.Name, hero);
                    var cells = new List<CellSpec>
                    {
                        Cell(Fit(isHero ? player.Name + " (tú)" : player.Name, 182, 8.5, true), isHero ? Accent : Ink, bold: true),
                        Cell(player.Hands.ToString("N0", Es), Ink2)
                    };
                    cells.AddRange(StatColumns.Select(col => StatCell(col.Key, StatValue(player, col.Key), player.Hands)));
                    TableRow(c, Margin, y, columns.Select(col => (col.Width, col.Right)).ToArray(), cells, 16, isHero ? AccentSoft : null);
                    y -= 16;
                }
            }

            private static void TableHeader(PdfCanvas c, double x, double y, (string Header, double Width, bool Right)[] columns)
            {
                var total = columns.Sum(col => col.Width);
                c.FillRect(x, y - 16, total, 16, Soft);
                c.Line(x, y - 16, x + total, y - 16, Line, 0.8);
                var cx = x;
                foreach (var (header, width, right) in columns)
                {
                    if (right)
                        c.TextRight(header, cx + width - 6, y - 11, 7.5, Ink2, bold: true);
                    else
                        c.Text(header, cx + 6, y - 11, 7.5, Ink2, bold: true);
                    cx += width;
                }
            }

            private readonly record struct CellSpec(string Text, Rgb Ink, bool Bold, Rgb? Fill);

            private static CellSpec Cell(string text, Rgb ink, bool bold = false, Rgb? fill = null) => new(text, ink, bold, fill);

            private static CellSpec StatCell(string key, double value, int hands)
            {
                var level = StatScale.Level(key, value, hands);
                var (fill, ink) = Colors(level);
                return new CellSpec(hands == 0 ? "–" : StatScale.Format(key, value), ink, level != StatLevel.NoSample, fill);
            }

            private static void TableRow(PdfCanvas c, double x, double y, (double Width, bool Right)[] columns, IReadOnlyList<CellSpec> cells, double h, Rgb? rowFill = null)
            {
                var total = columns.Sum(col => col.Width);
                if (rowFill is { } fillRow)
                    c.FillRect(x, y - h, total, h, fillRow);
                c.Line(x, y - h, x + total, y - h, Line, 0.5);
                var cx = x;
                for (var i = 0; i < columns.Length && i < cells.Count; i++)
                {
                    var (width, right) = columns[i];
                    var cell = cells[i];
                    if (cell.Fill is { } fill)
                        c.FillRect(cx + 1, y - h + 1.5, width - 2, h - 3, fill);
                    var baseline = y - h / 2 - 3;
                    if (right)
                        c.TextRight(cell.Text, cx + width - 6, baseline, 8, cell.Ink, cell.Bold);
                    else
                        c.Text(cell.Text, cx + 6, baseline, 8, cell.Ink, cell.Bold);
                    cx += width;
                }
            }

            /// <summary>Grafica de la portada: bb acumuladas mano a mano (real) y sin la suerte de los all-in (discontinua).</summary>
            private static void DrawSessionCurve(PdfCanvas c, IReadOnlyList<CurvePoint> curve, double x, double y, double w, double h)
            {
                if (curve.Count < 2)
                {
                    c.TextCenter("Sin manos suficientes para la gráfica.", x + w / 2, y + h / 2, 8.5, Muted);
                    return;
                }

                var real = new List<double> { 0 };
                var adjusted = new List<double> { 0 };
                foreach (var point in curve)
                {
                    real.Add(real[^1] + point.NetBb);
                    adjusted.Add(adjusted[^1] + point.NetBb - point.LuckBb);
                }

                // Leyenda arriba a la derecha.
                var legendY = y + h + 12;
                c.TextRight("Sin la suerte de los all-in", x + w, legendY, 7.5, Ink2);
                var lw = PdfCanvas.Width("Sin la suerte de los all-in", 7.5, false);
                c.DashedLine(x + w - lw - 24, legendY + 3, x + w - lw - 6, legendY + 3, Muted, 1.3);
                var rx = x + w - lw - 34;
                c.TextRight("Real", rx, legendY, 7.5, Ink2);
                var rw = PdfCanvas.Width("Real", 7.5, false);
                c.Line(rx - rw - 22, legendY + 3, rx - rw - 5, legendY + 3, Accent, 1.8);

                var plotX = x + 44;
                var plotW = w - 44;
                var plotY = y + 12;
                var plotH = h - 16;
                var (min, max, ticks) = NiceScale(Math.Min(real.Min(), adjusted.Min()), Math.Max(real.Max(), adjusted.Max()), 4);
                foreach (var tick in ticks)
                {
                    var ty = Map(tick, min, max, plotY, plotH);
                    c.Line(plotX, ty, plotX + plotW, ty, tick == 0 ? new Rgb(150, 158, 170) : Line, tick == 0 ? 0.9 : 0.5);
                    c.TextRight($"{Num(tick)}", plotX - 6, ty - 3, 7, Muted);
                }
                c.Text("mano 1", plotX, y, 7, Muted);
                c.TextRight($"mano {curve.Count.ToString("N0", Es)}", plotX + plotW, y, 7, Muted);

                List<(double X, double Y)> Points(IReadOnlyList<double> values) =>
                    values.Select((value, index) => (plotX + plotW * index / (values.Count - 1), Map(value, min, max, plotY, plotH))).ToList();

                c.DashedPolyline(Points(adjusted), Muted, 1.2);
                c.Polyline(Points(real), Accent, 1.8);
                var end = Points(real)[^1];
                c.FillCircle(end.X, end.Y, 2.6, real[^1] >= 0 ? Good : Bad);
            }

            private static IEnumerable<(DateTime At, double Bb)> CumulativeByTime(TableReportModel table)
            {
                var hands = table.HandImpacts.Where(hand => hand.Time.HasValue).OrderBy(hand => hand.Time).ToList();
                if (hands.Count == 0)
                    yield break;
                yield return (hands[0].Time!.Value, 0);
                var total = 0.0;
                foreach (var hand in hands)
                {
                    total += hand.NetBb;
                    yield return (hand.Time!.Value, total);
                }
            }

            // ------------------------------------------------------------------ utilidades

            private static double StatValue(RealTimePlayerReportRow row, string key) => key switch
            {
                "VPIP" => row.VPIP,
                "PFR" => row.PFR,
                "THREEBET" => row.ThreeBet,
                "AF" => row.AF,
                "AFQ" => row.AFq,
                "CBF" => row.CBet,
                "FVCBF" => row.FoldVsCBet,
                "WTSD" => row.WTSD,
                "WSD" => row.WSD,
                _ => row.WWSF
            };

            private static (Rgb Fill, Rgb Ink) Colors(StatLevel level)
            {
                if (StatScale.ColorOf(level) is not { } color)
                    return (NoSampleFill, NoSampleInk);
                var ink = StatScale.InkOf(level);
                return (new Rgb(color.R, color.G, color.B), new Rgb(ink.R, ink.G, ink.B));
            }

            private static string Capitalize(string text) =>
                string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], Es) + text[1..];

            private static string Street(string street) => street switch
            {
                "PREFLOP" => "preflop",
                "FLOP" => "flop",
                "TURN" => "turn",
                "RIVER" => "river",
                _ => street.ToLower(Es)
            };

            private static (double Min, double Max, List<double> Ticks) NiceScale(double low, double high, int count)
            {
                low = Math.Min(0, low);
                high = Math.Max(0, high);
                if (high - low < 1)
                {
                    high += 1;
                    low -= 1;
                }
                var raw = (high - low) / count;
                var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
                var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(s => s >= raw);
                var min = Math.Floor(low / step) * step;
                var max = Math.Ceiling(high / step) * step;
                var ticks = new List<double>();
                for (var t = min; t <= max + step / 2; t += step)
                    ticks.Add(Math.Round(t, 6));
                return (min, max, ticks);
            }

            private static double Map(double value, double min, double max, double y, double h) => y + (value - min) / (max - min) * h;

            private static string Num(double value) => value.ToString("#,0.#", Es);

            private static string Bb(double value) => value.ToString("+#,0.#;-#,0.#;0", Es) + " bb";

            private static string Money(double amount, bool isCash) =>
                isCash
                    ? amount.ToString("+$#,0.00;-$#,0.00;$0", Es)
                    : amount.ToString("+#,0;-#,0;0", Es) + " fichas";

            private static string Duration(TimeSpan duration) =>
                duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes} min" : $"{Math.Max(1, duration.Minutes)} min";

            // Las horas del historial de PokerStars vienen en UTC: se muestran en la hora local.
            private static DateTime Local(DateTime value) =>
                value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();

            private static string Time(DateTime? value) => value.HasValue ? Local(value.Value).ToString("HH:mm", CultureInfo.InvariantCulture) : "–";

            private static string Fit(string text, double width, double size, bool bold)
            {
                text = PdfCanvas.Sanitize(text);
                if (PdfCanvas.Width(text, size, bold) <= width)
                    return text;
                while (text.Length > 1 && PdfCanvas.Width(text + "…", size, bold) > width)
                    text = text[..^1];
                return text.TrimEnd() + "…";
            }

            private static IEnumerable<string> Wrap(string text, double width, double size, bool bold)
            {
                var line = "";
                foreach (var word in PdfCanvas.Sanitize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && PdfCanvas.Width(candidate, size, bold) > width)
                    {
                        yield return line;
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }
                if (line.Length > 0)
                    yield return line;
            }

            private static void WriteObjects(string path, IReadOnlyList<string> pageContents, SessionReportModel model)
            {
                var objects = new List<string>
                {
                    "<< /Type /Catalog /Pages 2 0 R >>",
                    "__PAGES__",
                    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
                    $"<< /Title {PdfCanvas.Literal("Informe de sesión APH - " + model.Hero)} /Author {PdfCanvas.Literal(model.Hero)} /Creator (APH Analyzer Poker Hands) /Producer (APH) >>"
                };

                var pageIds = new List<int>();
                foreach (var content in pageContents)
                {
                    var pageId = objects.Count + 1;
                    var contentId = objects.Count + 2;
                    pageIds.Add(pageId);
                    objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW.ToString(CultureInfo.InvariantCulture)} {PageH.ToString(CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
                    objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
                }

                objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>";

                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, Encoding.ASCII);
                writer.WriteLine("%PDF-1.4");
                writer.WriteLine("%APH");
                writer.Flush();

                var offsets = new List<long> { 0 };
                for (var i = 0; i < objects.Count; i++)
                {
                    offsets.Add(stream.Position);
                    writer.WriteLine($"{i + 1} 0 obj");
                    writer.WriteLine(objects[i]);
                    writer.WriteLine("endobj");
                    writer.Flush();
                }

                var xref = stream.Position;
                writer.WriteLine("xref");
                writer.WriteLine($"0 {objects.Count + 1}");
                writer.WriteLine("0000000000 65535 f ");
                foreach (var offset in offsets.Skip(1))
                    writer.WriteLine($"{offset:0000000000} 00000 n ");
                writer.WriteLine("trailer");
                writer.WriteLine($"<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>");
                writer.WriteLine("startxref");
                writer.WriteLine(xref.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("%%EOF");
            }

            private sealed class Doc
            {
                public readonly List<PdfCanvas> Pages = new();
                public PdfCanvas C = null!;
                public double Y;

                public void NewPage()
                {
                    C = new PdfCanvas();
                    C.FillRect(0, 0, PageW, PageH, new Rgb(255, 255, 255));
                    Pages.Add(C);
                    Y = ContentTop;
                }
            }
        }

        /// <summary>Un color 0-255.</summary>
        private readonly record struct Rgb(double R, double G, double B);

        /// <summary>
        /// Lo que se dibuja en una pagina, en operadores de PDF. El texto va en WinAnsi (acentos, ñ, ¿ ¡,
        /// guiones y comillas tipograficas); lo que no existe en esa codificacion se cambia por algo
        /// parecido. <see cref="Width"/> mide con las metricas de Helvetica para alinear y cortar lineas.
        /// </summary>
        private sealed class PdfCanvas
        {
            private readonly StringBuilder _builder = new();

            public string Content => _builder.ToString();

            public void FillRect(double x, double y, double w, double h, Rgb color)
            {
                Fill(color);
                _builder.Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(w)).Append(' ').Append(N(h)).AppendLine(" re f");
            }

            public void FillRoundRect(double x, double y, double w, double h, double r, Rgb color)
            {
                Fill(color);
                RoundPath(x, y, w, h, r);
                _builder.AppendLine("f");
            }

            public void StrokeRoundRect(double x, double y, double w, double h, double r, Rgb color, double width)
            {
                Stroke(color, width);
                RoundPath(x, y, w, h, r);
                _builder.AppendLine("S");
            }

            public void FillCircle(double cx, double cy, double r, Rgb color) => FillRoundRect(cx - r, cy - r, r * 2, r * 2, r, color);

            public void Line(double x1, double y1, double x2, double y2, Rgb color, double width = 1)
            {
                Stroke(color, width);
                _builder.AppendLine($"{N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S");
            }

            public void DashedLine(double x1, double y1, double x2, double y2, Rgb color, double width = 1)
            {
                _builder.AppendLine("[3 2] 0 d");
                Line(x1, y1, x2, y2, color, width);
                _builder.AppendLine("[] 0 d");
            }

            public void Polyline(IReadOnlyList<(double X, double Y)> points, Rgb color, double width)
            {
                if (points.Count < 2)
                    return;
                Stroke(color, width);
                _builder.AppendLine("1 j 1 J");
                _builder.AppendLine($"{N(points[0].X)} {N(points[0].Y)} m");
                foreach (var point in points.Skip(1))
                    _builder.AppendLine($"{N(point.X)} {N(point.Y)} l");
                _builder.AppendLine("S");
                _builder.AppendLine("0 j 0 J");
            }

            public void DashedPolyline(IReadOnlyList<(double X, double Y)> points, Rgb color, double width)
            {
                _builder.AppendLine("[3 2] 0 d");
                Polyline(points, color, width);
                _builder.AppendLine("[] 0 d");
            }

            public void Text(string text, double x, double y, double size, Rgb color, bool bold = false)
            {
                _builder.AppendLine("BT");
                Fill(color);
                _builder.AppendLine($"/{(bold ? "F2" : "F1")} {N(size)} Tf");
                _builder.AppendLine($"{N(x)} {N(y)} Td");
                _builder.Append(Literal(text)).AppendLine(" Tj");
                _builder.AppendLine("ET");
            }

            public void TextRight(string text, double right, double y, double size, Rgb color, bool bold = false) =>
                Text(text, right - Width(text, size, bold), y, size, color, bold);

            public void TextCenter(string text, double center, double y, double size, Rgb color, bool bold = false) =>
                Text(text, center - Width(text, size, bold) / 2, y, size, color, bold);

            private void Fill(Rgb color) => _builder.AppendLine($"{N(color.R / 255)} {N(color.G / 255)} {N(color.B / 255)} rg");

            private void Stroke(Rgb color, double width) =>
                _builder.AppendLine($"{N(color.R / 255)} {N(color.G / 255)} {N(color.B / 255)} RG {N(width)} w");

            private void RoundPath(double x, double y, double w, double h, double r)
            {
                r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
                var k = 0.5523 * r;
                _builder.AppendLine($"{N(x + r)} {N(y)} m");
                _builder.AppendLine($"{N(x + w - r)} {N(y)} l");
                _builder.AppendLine($"{N(x + w - r + k)} {N(y)} {N(x + w)} {N(y + r - k)} {N(x + w)} {N(y + r)} c");
                _builder.AppendLine($"{N(x + w)} {N(y + h - r)} l");
                _builder.AppendLine($"{N(x + w)} {N(y + h - r + k)} {N(x + w - r + k)} {N(y + h)} {N(x + w - r)} {N(y + h)} c");
                _builder.AppendLine($"{N(x + r)} {N(y + h)} l");
                _builder.AppendLine($"{N(x + r - k)} {N(y + h)} {N(x)} {N(y + h - r + k)} {N(x)} {N(y + h - r)} c");
                _builder.AppendLine($"{N(x)} {N(y + r)} l");
                _builder.AppendLine($"{N(x)} {N(y + r - k)} {N(x + r - k)} {N(y)} {N(x + r)} {N(y)} c");
                _builder.AppendLine("h");
            }

            private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

            /// <summary>Texto como cadena de PDF en WinAnsi: los caracteres fuera de ASCII van en octal.</summary>
            public static string Literal(string text)
            {
                var builder = new StringBuilder("(");
                foreach (var ch in Sanitize(text))
                {
                    var code = WinAnsi(ch);
                    switch (code)
                    {
                        case '(':
                        case ')':
                        case '\\':
                            builder.Append('\\').Append((char)code);
                            break;
                        case > 126:
                            builder.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
                            break;
                        default:
                            builder.Append((char)code);
                            break;
                    }
                }
                return builder.Append(')').ToString();
            }

            /// <summary>Quita saltos de linea y cambia lo que WinAnsi no tiene por algo parecido.</summary>
            public static string Sanitize(string text)
            {
                var builder = new StringBuilder(text.Length);
                foreach (var ch in text)
                {
                    if (WinAnsi(ch) >= 0)
                    {
                        builder.Append(ch);
                        continue;
                    }
                    var replacement = ch switch
                    {
                        '▲' => "+",
                        '▼' => "-",
                        '♠' => "s",
                        '♥' => "h",
                        '♦' => "d",
                        '♣' => "c",
                        '−' => "-",
                        '→' => "->",
                        '≈' => "~",
                        '≤' => "<=",
                        '≥' => ">=",
                        _ when char.IsWhiteSpace(ch) => " ",
                        _ => null
                    };
                    if (replacement is null)
                    {
                        var basic = ch.ToString().Normalize(NormalizationForm.FormD)[0];
                        replacement = basic is >= ' ' and <= '~' ? basic.ToString() : "?";
                    }
                    builder.Append(replacement);
                }
                return builder.ToString();
            }

            private static int WinAnsi(char ch) => ch switch
            {
                >= ' ' and <= '~' => ch,
                >= ' ' and <= 'ÿ' => ch,
                '€' => 0x80,
                '‚' => 0x82,
                '„' => 0x84,
                '…' => 0x85,
                '‘' => 0x91,
                '’' => 0x92,
                '“' => 0x93,
                '”' => 0x94,
                '•' => 0x95,
                '–' => 0x96,
                '—' => 0x97,
                '™' => 0x99,
                _ => -1
            };

            private static readonly short[] Regular =
            {
                278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
                556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
                1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
                667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
                333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
                556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
            };

            private static readonly short[] Bold =
            {
                278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
                556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
                975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
                667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
                333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
                611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
            };

            /// <summary>Ancho del texto en puntos con las metricas de Helvetica (las letras con acento miden como su base).</summary>
            public static double Width(string text, double size, bool bold)
            {
                var table = bold ? Bold : Regular;
                double units = 0;
                foreach (var ch in Sanitize(text))
                {
                    var code = WinAnsi(ch);
                    units += code switch
                    {
                        >= 32 and <= 126 => table[code - 32],
                        0x85 or 0x97 => 1000,
                        0x96 or 0x80 => 556,
                        0x95 => 350,
                        0x91 or 0x92 => bold ? 278 : 222,
                        0x93 or 0x94 => bold ? 500 : 333,
                        0xB7 or 0xA0 => 278,
                        0xBF => 611,
                        0xA1 => 333,
                        0xB0 => 400,
                        _ => BaseWidth(ch, table)
                    };
                }
                return units * size / 1000;
            }

            private static double BaseWidth(char ch, short[] table)
            {
                var basic = ch.ToString().Normalize(NormalizationForm.FormD)[0];
                return basic is >= ' ' and <= '~' ? table[basic - 32] : 556;
            }
        }
    }
}
