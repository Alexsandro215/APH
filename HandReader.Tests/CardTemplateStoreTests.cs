using System.Text.Json;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Plantillas de cartas: una con el color de otro palo no puede dejar un palo entero en "?".</summary>
public class CardTemplateStoreTests
{
    private static CardTemplateStore.CardTemplate Template(string card, InkColor color, byte suitInk) => new()
    {
        Card = card,
        Full = new byte[CardFeatures.FullW * CardFeatures.FullH * 3],
        Rank = Enumerable.Repeat((byte)120, CardFeatures.RankW * CardFeatures.RankH).ToArray(),
        Suit = Enumerable.Repeat(suitInk, CardFeatures.SuitW * CardFeatures.SuitH * 3).ToArray(),
        SuitColor = color
    };

    /// <summary>
    /// Lo que paso en la mesa: un 7 de diamantes (azul) se aprendio como 7 de treboles. Con esa
    /// plantilla el palo de diamante empataba con el trebol y cada carta de diamantes quedaba en "?".
    /// Al cargar, la plantilla de trebol azul se quita.
    /// </summary>
    [Fact]
    public void Al_cargar_quita_la_plantilla_de_un_palo_con_el_color_de_otro()
    {
        var file = Path.Combine(Path.GetTempPath(), "aph-plantillas-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var templates = new List<CardTemplateStore.CardTemplate>
            {
                Template("2c", InkColor.Green, 10), Template("5c", InkColor.Green, 10), Template("9c", InkColor.Green, 10),
                Template("Kc", InkColor.Green, 10), Template("Ac", InkColor.Green, 10),
                Template("7c", InkColor.Blue, 200), // el diamante mal etiquetado
                Template("4d", InkColor.Blue, 200), Template("Qd", InkColor.Blue, 200), Template("8d", InkColor.Blue, 200),
                Template("Td", InkColor.Blue, 200),
            };
            File.WriteAllText(file, JsonSerializer.Serialize(templates));

            var store = CardTemplateStore.Load(file);

            Assert.Equal(InkColor.Green, store.ExpectedColor('c', fourColorHint: true));
            Assert.Equal(9, store.LearnedCards); // el "7c" azul ya no esta
            Assert.Equal(0, store.RemoveColorOutliers());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
