# Plan para la RTX 3060

Notas del 20 de noviembre de 2026: qué hacer cuando la RTX 3060 esté instalada y qué ganaría APH
con ella. Los tiempos marcados como **estimado** no se han medido; los **medido** salen de pruebas de
ese día.

## Las dos computadoras

| | PC de juego (la de hoy) | PC de cálculo (la que recibe la 3060) |
|---|---|---|
| Procesador | AMD Ryzen 5 5600G, 6 núcleos / 12 hilos | AMD Ryzen 7 3700X, 8 núcleos / 16 hilos, ~3.9 GHz |
| Gráfica | AMD Radeon RX 6600 XT | NVIDIA GTX 970 4 GB → **RTX 3060 12 GB** |
| Memoria | 32 GB | 32 GB DDR4 (4 × 8 GB) a 2666 MHz |
| Placa | — | ASUS ROG STRIX B450-F GAMING II (BIOS 0301, agosto 2020) |
| Disco | — | SSD Kingston A400 480 GB (SATA), 331 GB libres |
| Sistema | Windows 11 | Windows 10 Pro 64 bits (19045) |

Idea de reparto: la PC de juego sigue jugando con APH. La PC de cálculo entrena modelos y construye
bibliotecas de noche; lo que produce (archivos de modelo y de biblioteca) se copia a la PC de juego.

El procesador 3700X rinde en paralelo un 15 a 20 % más que el 5600G (**estimado**, por pruebas
públicas). El cambio grande lo pone la 3060.

## 1. Instalar la tarjeta

1. Apagar la PC, desconectarla y quitar la GTX 970.
2. Poner la RTX 3060 en la ranura PCIe x16 de arriba (la misma de la 970).
3. Corriente: la 3060 pide un conector PCIe de 8 pines y unos 170 W. Con una fuente de 550 W o más
   va sobrada. Revisar el modelo de la fuente antes de conectar.
4. Encender e instalar el controlador más reciente de NVIDIA (Game Ready o Studio). Como la 970
   también era NVIDIA, basta con instalar encima; si algo falla, limpiar con DDU y reinstalar.
5. Comprobar en una terminal: `nvidia-smi` debe mostrar «NVIDIA GeForce RTX 3060» con 12 GB.
6. BIOS (opcional, gratis): activar el perfil DOCP de la memoria si los módulos admiten más de
   2666 MHz. Da algo más de velocidad al procesador en el solver. Actualizar la BIOS (es de 2020) es
   opcional; hacerlo solo con la versión oficial de ASUS y sin cortar la luz.

## 2. Preparar el software en la PC de cálculo

1. Instalar Git y el SDK de .NET 8.
2. Clonar el repositorio de APH y compilar (`dotnet build`) para comprobar que todo funciona.
3. Copiar la carpeta de datos `A:\APH\Datos\phh-dataset` (16 GB; está fuera de git a propósito).
   Es el conjunto HandHQ 2009 de dinero real (licencia CC BY 4.0): unos 8.3 millones de manos de
   PokerStars y 3.1 millones de PartyPoker, entre otras salas.
4. Copiar también los historiales de dinero ficticio (`Documentos\Manos_PokerStar\UwU_Fantasy_UwU`)
   para afinar el modelo con la población de las mesas donde se juega.
5. Instalar Python 3.12 y PyTorch con CUDA. Comprobar que `torch.cuda.is_available()` da `True`.
6. Para usar el modelo dentro de APH: exportarlo a ONNX y cargarlo con ONNX Runtime. En la PC de
   juego corre en el procesador, sin necesitar la gráfica y sin retrasar el consejo.

## 3. Qué hacer, en orden

### 3.1 Biblioteca de flops actual (se puede hacer ya, sin la 3060)

Hoy la biblioteca está **vacía**: nunca se construyó, así que todos los flops se calculan en vivo.
Con ella, los flops de bote subido (BTN, CO, SB, HJ o LJ contra la ciega grande, 100 bb) salen al
instante.

- Cómo: ventana de Tiempo real → sección «Biblioteca GTO del flop» → botón para empezar. Se puede
  parar cuando se quiera; lo hecho queda guardado.
- Tamaño: 5 posiciones × 1,755 flops = 8,775 flops, 100 vueltas cada uno.
- Tiempo: unas 3 horas con la PC de juego libre; de 1.5 a 3 horas en el 3700X (**estimado**).
- Los archivos quedan en `%APPDATA%\APH\gto-flop` (un `.bin` por posición). Si se construye en la
  PC de cálculo, copiar esa carpeta a la PC de juego.

### 3.2 Modelo de los rivales (lo que más vale con la 3060)

En dinero ficticio la gente no juega GTO: paga de más y se retira distinto según el tamaño de la
apuesta. Aquí está el dinero.

- Qué predice:
  - qué hace cada tipo de rival ante cada apuesta (retirarse, pagar o subir), según la calle, el
    tamaño, la posición, la textura del tablero y sus estadísticas aunque tenga pocas manos;
  - qué manos suele tener cuando actúa así (aprendido de las manos que llegan a showdown).
- Para qué: corregir el error medido. APH calcula que los rivales se retiran un 18 % de las veces
  cuando en realidad es un 33 %; en el river, 14 % contra 50 %. Con eso mejoran los tamaños de
  apuesta y el saber cuándo apostar por valor.
- Cómo:
  1. Entrenar primero con HandHQ (dinero real).
  2. Afinar con las manos de ficticio.
  3. Comparar con el modelo actual del «ritmo de la sala» usando manos que no vio al entrenar.
     Referencia medida el 20/11/2026: logloss 0.68 y Brier 0.231.
  4. Quedarse con el nuevo modelo solo si gana.
- Tiempo (**estimado**): alrededor de 1 hora preparando los datos (procesador, 16 hilos) y de 2 a
  4 horas entrenando en la 3060. Una tarde.
- Reglas que no cambian:
  - El modelo de ficticio solo se usa para el consejo en vivo en mesas de dinero ficticio.
  - En dinero real solo sirve para revisar después de jugar; nunca en vivo.
  - APH nunca hace clic por el jugador.
  - La protección de dinero ficticio de `TrainerSession.cs` no se toca.

### 3.3 Biblioteca grande con el solver en la gráfica (después)

- Pasar el solver del flop a la 3060 (CUDA, por ejemplo con ILGPU desde C#).
- Con eso, precalcular muchos más spots: botes de 3-bet, todas las parejas de posiciones y otras
  profundidades de pila.
- Tiempo: una o dos noches (**estimado**). Es el número más incierto, porque ese solver todavía no
  existe.

### 3.4 Solver completo en la gráfica (lo último)

- Árbol más completo: más tamaños de apuesta y apuestas en el river. Hoy, en el flop en vivo, el
  river se reparte sin apuestas (aproximado).
- Flop resuelto en menos de un segundo (**estimado**).
- Es lo que más mejora la precisión GTO, pero lo más largo de programar. En dinero ficticio rinde
  menos que explotar a los rivales (3.2).

## Lo que ya se hizo el 20/11/2026 (referencia)

- **Solver en paralelo (medido).** El solver del flop y el del turn reparten cada carta posible
  entre los hilos del procesador. Medido en la PC de juego con 6 mesas abiertas:

  | Spot | Vueltas por segundo con 1 hilo | Con 10 hilos |
  |---|---|---|
  | BTN contra BB, Ks 7d 2c | 11 | 47 |
  | CO contra BB, Jc 7h Qc | 22 | 83 |
  | SB contra BB, 9h 8h 6c | 10 | 40 |

  La estrategia sale idéntica a la de un hilo.
- **Espera del consejo en vivo.** El flop pasó de 5 a 2 segundos y el turn de 4.5 a 2. Aun así salen
  más vueltas que antes.
- **Carga del Panel (medido, 5,063 manos en 66 mesas).**

  | Paso | Tiempo |
  |---|---|
  | Historial de villanos | 1.4 s |
  | Análisis por mesa | 0.8 s |
  | Estadísticas de todos los jugadores | 0.7 s |
  | Gráfica mano a mano | 0.66 s |
  | Leer los archivos | 0.05 s |

  Varias partes vuelven a leer las mismas manos; se puede acelerar a la mitad o menos
  (pendiente, no necesita la gráfica).

## Lista corta

- [ ] Construir la biblioteca de flops actual (una noche, con o sin la 3060).
- [ ] Quitar la GTX 970 y poner la RTX 3060 (conector de 8 pines).
- [ ] Instalar el controlador de NVIDIA y comprobar con `nvidia-smi`.
- [ ] Activar DOCP en la BIOS (opcional).
- [ ] Instalar Git, .NET 8, Python 3.12 y PyTorch con CUDA en la PC de cálculo.
- [ ] Copiar `Datos\phh-dataset` y los historiales de ficticio.
- [ ] Entrenar el modelo de rivales y compararlo con el actual; usarlo solo si gana.
- [ ] Después: solver del flop en la gráfica y biblioteca grande.
