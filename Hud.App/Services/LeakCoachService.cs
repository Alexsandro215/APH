using System;
using System.Collections.Generic;
using System.Linq;
using Hud.App.Views;

namespace Hud.App.Services
{
    public static class LeakCoachService
    {
        public static (string Diagnostic, string ProTip) GetAnalysis(LeakSpotRow hand)
        {
            if (hand == null) return ("", "");

            var pos = hand.Position.ToUpperInvariant();
            var action = hand.Action.ToUpperInvariant();
            var potType = hand.PotType.ToUpperInvariant();
            var combo = hand.Combo;
            var board = hand.BoardTexture.ToUpperInvariant();
            var net = hand.NetBb;

            // 1. Preflop Leaks
            if (board == "PREFLOP")
            {
                if (action.Contains("FOLD") && net < -1)
                    return ("Hiciste fold en las ciegas después de haber invertido dinero. Si la apuesta era pequeña, podrías haber defendido por el precio.", "Calcula tus 'Pot Odds'. Si necesitas ganar el 15% de las veces y tu mano tiene un 30% contra el rango del villano, es un call obligatorio.");

                if (pos == "UTG" || pos == "MP")
                    return ($"Abrir {combo} desde {pos} es muy arriesgado. Estas posiciones requieren un rango mucho más sólido (Top 12-15%).", "Desde posiciones tempranas, prefiere manos que no sean fácilmente dominadas como AJ+, KQ+ y parejas medias.");

                if (potType.Contains("3BET") && action.Contains("CALL"))
                    return ("Hacer call a un 3-bet fuera de posición con esta mano suele ser una pérdida de dinero a largo plazo.", "En botes 3-bet, si no tienes una mano premium, es mejor aplicar la regla de '4-bet o fold' para no jugar botes grandes sin iniciativa.");
            }

            // 2. Postflop Texture Leaks
            if (board == "MONOCOLOR" && !action.Contains("FOLD"))
                return ("Los boards monocolor son extremadamente peligrosos. Si no tienes una carta alta del palo o el color completado, la precaución debe ser máxima.", "En estos boards, el rango del villano se vuelve muy polarizado. No sobre-estimes una pareja alta aquí.");

            if (board == "COORDINADO" && action.Contains("CHECK"))
                return ("Hiciste check en un board muy conectado. Estás dando cartas gratis a proyectos que pueden superarte.", "En boards coordinados, debes apostar fuerte para 'cobrar' a los proyectos de escalera o color mientras todavía tienes la mejor mano.");

            // 3. Size Leaks
            if (net <= -50)
                return ("Esta mano representa una fuga masiva de fichas. Probablemente hubo un error de 'stack-off' con una mano marginal.", "Recuerda: Botes grandes son para manos grandes. Si vas a meter más de 50bb, asegúrate de tener al menos Dobles Parejas fuertes o Set.");

            // 4. Position specific
            if (pos == "BTN" && action.Contains("FOLD"))
                return ("Desde el Botón deberías estar presionando mucho más. Hacer fold aquí con manos mediocres es regalar la ventaja de posición.", "El botón es la posición más rentable. Tu rango de apertura aquí debería ser cercano al 45-50%.");

            // Default fallback based on hand reason or generic
            if (!string.IsNullOrEmpty(hand.Reason))
                return (hand.Reason, "Revisa la secuencia de la mano abajo para identificar el momento exacto donde el bote se salió de control.");

            return ("Análisis general: Esta mano muestra una varianza estándar en botes de este tipo.", "Sigue analizando tus manos para encontrar patrones repetitivos en tus pérdidas.");
        }
    }
}
