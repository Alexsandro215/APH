using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Cada mano corregida en vivo se guarda con la recomendacion que se mostro.</summary>
public class TrainerReviewLogTests
{
    [Fact]
    public void Guarda_y_lee_la_recomendacion_en_vivo_de_cada_decision()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aph-revision-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            TrainerReviewLog.UseFolder(folder);
            var day = new DateTime(2026, 11, 18, 5, 30, 0);
            var decision = new TrainerDecision
            {
                Street = "TURN",
                Situation = "Te apuestan",
                Chosen = "Raise a 82 bb",
                Recommended = "Call",
                Grade = TrainerGrade.Mistake,
                Explanation = "Subir aqui solo lo pagan manos mejores.",
                LiveRecommendation = "IGUALAR 41 BB",
                LiveSource = "GTO",
                LiveReason = "GTO con QQ: siempre igualar.",
                FollowedLive = false
            };
            TrainerReviewLog.Append(new TrainerHandReport
            {
                HandId = "262750373416",
                TableName = "Lysithea V",
                HeroCards = "Qc Qh",
                Board = "5h 4h Kc 8h 5c",
                Position = "HJ",
                NetBb = -105.7,
                ProcessedAt = day,
                Decisions = new[] { decision }
            });

            var entry = Assert.Single(TrainerReviewLog.Read(day));
            Assert.Equal("Lysithea V", entry.Table);
            Assert.Equal(-105.7, entry.NetBb, 3);
            var saved = Assert.Single(entry.Decisions);
            Assert.Equal("IGUALAR 41 BB", saved.LiveRecommendation);
            Assert.Equal("GTO", saved.LiveSource);
            Assert.Equal("Mistake", saved.Grade);
            Assert.False(saved.FollowedLive);
        }
        finally
        {
            TrainerReviewLog.UseFolder(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
