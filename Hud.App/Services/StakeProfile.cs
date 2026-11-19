namespace Hud.App.Services
{
    public enum StakeProfile { Low, Mid, High }

    public static class StakeProfileRules
    {
        /// <summary>
        /// Nivel de la mesa a partir de la ciega grande. Sin moneda (dinero ficticio o fichas de
        /// torneo) es nivel bajo: antes una mesa de dinero ficticio 100/200 se tomaba por NL500+
        /// (200 >= 2.00) y los alumnos se coloreaban con los cortes de profesionales muy tight.
        /// </summary>
        public static StakeProfile FromBigBlind(double bigBlind, string? blindsText)
        {
            if (!string.IsNullOrWhiteSpace(blindsText) && !PokerAmountParser.HasCurrency(blindsText))
                return StakeProfile.Low;

            if (bigBlind <= 0.10) return StakeProfile.Low;   // NL2-NL10
            if (bigBlind >= 2.00) return StakeProfile.High;  // NL200+
            return StakeProfile.Mid;                         // NL25-NL100
        }
    }
}
