using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Hud.App.Models
{
    public class HandStep
    {
        public int StepIndex { get; set; }
        public string Street { get; set; } = ""; // PREFLOP, FLOP, etc.
        public string ActionText { get; set; } = "";
        public string Actor { get; set; } = "";
        public double PotSize { get; set; }
        public double CurrentBet { get; set; }
        
        // Player states at this step
        public List<PlayerReplayState> Players { get; set; } = new();
        
        // APH reference info for the current actor
        public AphReferenceRecommendation AphReference { get; set; } = null!;
        
        // Cards visible at this step
        public List<string> BoardCards { get; set; } = new();
        public IReadOnlyList<Hud.App.Views.CardChipViewModel> BoardCardChips => Hud.App.Views.CardChipViewModel.FromCards(string.Join(" ", BoardCards));
    }

    public class PlayerReplayState
    {
        public string Name { get; set; } = "";
        public string Position { get; set; } = "";
        public int SeatNumber { get; set; }
        public int SeatIndex { get; set; }
        public double Stack { get; set; }
        public double Committed { get; set; }
        public bool IsActive { get; set; }
        public bool IsHero { get; set; }
        public string Cards { get; set; } = "";
        public string LastAction { get; set; } = "";
        public string CurrentAction { get; set; } = "";
        public bool IsCurrentActor { get; set; }
        public bool IsDealerButton { get; set; }
        public IReadOnlyList<Hud.App.Views.CardChipViewModel> CardChips => Hud.App.Views.CardChipViewModel.FromCards(Cards);
        public bool HasVisibleCards => CardChips.Count > 0;
        public bool ShowHiddenCards => !HasVisibleCards;
        public IReadOnlyList<Hud.App.Views.CardChipViewModel> HiddenCardChips { get; } =
            Enumerable.Range(0, 2)
                .Select(_ => new Hud.App.Views.CardChipViewModel(
                    "?",
                    '?',
                    new SolidColorBrush(Color.FromRgb(41, 47, 58)),
                    new SolidColorBrush(Color.FromRgb(198, 211, 226))))
                .ToList();
    }

    public class AphReferenceRecommendation
    {
        public double FoldPct { get; set; }
        public double CheckPct { get; set; }
        public double CallPct { get; set; }
        public double RaisePct { get; set; }
        public double Equity { get; set; }
        public string StrategyTip { get; set; } = "";
        public string ChosenAction { get; set; } = "";
        public string RecommendedAction { get; set; } = "";
        public string ConcordanceLabel { get; set; } = "";
        public string PotOddsLabel { get; set; } = "";
        public string EquityMarginLabel { get; set; } = "";
        public string CallEvLabel { get; set; } = "";
        public string RaiseEvLabel { get; set; } = "";
        public string FoldEquityLabel { get; set; } = "";
        public string VillainProfileLabel { get; set; } = "";
        public string VillainExploitLabel { get; set; } = "";
        public string SizingLabel { get; set; } = "";
        public string SprLabel { get; set; } = "";
        public string OddsInsight { get; set; } = "";
        public string ShowdownVerdict { get; set; } = "";
        public string HeroHandWinrateLabel { get; set; } = "";
        public bool ShowFold { get; set; } = true;
        public bool ShowCheck { get; set; }
        public bool ShowCall { get; set; } = true;
        public bool ShowRaise { get; set; } = true;
        public bool ShowOddsPanel => !string.IsNullOrWhiteSpace(PotOddsLabel) ||
                                     !string.IsNullOrWhiteSpace(EquityMarginLabel) ||
                                     !string.IsNullOrWhiteSpace(CallEvLabel) ||
                                     !string.IsNullOrWhiteSpace(RaiseEvLabel) ||
                                     !string.IsNullOrWhiteSpace(FoldEquityLabel) ||
                                     !string.IsNullOrWhiteSpace(VillainProfileLabel) ||
                                     !string.IsNullOrWhiteSpace(VillainExploitLabel) ||
                                     !string.IsNullOrWhiteSpace(SizingLabel) ||
                                     !string.IsNullOrWhiteSpace(SprLabel) ||
                                     !string.IsNullOrWhiteSpace(OddsInsight);
        public bool ShowShowdownReview => !string.IsNullOrWhiteSpace(ShowdownVerdict) ||
                                          !string.IsNullOrWhiteSpace(HeroHandWinrateLabel);
        
        // Remainder properties for Grid scaling
        public double EquityRemainder => Math.Max(0, 100 - Equity);
        public double FoldRemainder => Math.Max(0, 100 - FoldPct);
        public double CheckRemainder => Math.Max(0, 100 - CheckPct);
        public double CallRemainder => Math.Max(0, 100 - CallPct);
        public double RaiseRemainder => Math.Max(0, 100 - RaisePct);
        public string FoldLabel => $"{FoldPct:0.#}%";
        public string CheckLabel => $"{CheckPct:0.#}%";
        public string CallLabel => $"{CallPct:0.#}%";
        public string RaiseLabel => $"{RaisePct:0.#}%";

        // For UI binding
        public Brush FoldBrush => new SolidColorBrush(Color.FromRgb(231, 76, 60)); // Red
        public Brush CheckBrush => new SolidColorBrush(Color.FromRgb(149, 165, 166)); // Gray
        public Brush CallBrush => new SolidColorBrush(Color.FromRgb(46, 204, 113)); // Green
        public Brush RaiseBrush => new SolidColorBrush(Color.FromRgb(52, 152, 219)); // Blue
        public Brush ConcordanceBrush => ConcordanceLabel.Contains("Concordo", System.StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
            : ConcordanceLabel.Contains("No concordo", System.StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(226, 78, 91))
                : new SolidColorBrush(Color.FromRgb(227, 179, 65));
        public Brush EquityMarginBrush => EquityMarginLabel.Contains("+", System.StringComparison.OrdinalIgnoreCase) ||
                                          CallEvLabel.Contains("+", System.StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
            : EquityMarginLabel.Contains("-", System.StringComparison.OrdinalIgnoreCase) ||
              CallEvLabel.Contains("-", System.StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(226, 78, 91))
                : new SolidColorBrush(Color.FromRgb(227, 179, 65));
        public Brush ShowdownVerdictBrush => ShowdownVerdict.Contains("perdi", System.StringComparison.OrdinalIgnoreCase) ||
                                             ShowdownVerdict.Contains("cost", System.StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.FromRgb(226, 78, 91))
            : ShowdownVerdict.Contains("gan", System.StringComparison.OrdinalIgnoreCase) ||
              ShowdownVerdict.Contains("rentable", System.StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(227, 179, 65));
    }
}

