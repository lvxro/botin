# Botín

Contador de cofres de Fortnite. **Una sola app**: el contador, las estadísticas y el overlay del juego viven en el mismo programa. No hay que abrir el overlay, el contador y el reporte por separado.

## Qué hace

- Lee los replays que Fortnite guarda al terminar cada partida y suma los cofres que abriste, más los **tesoros** de la búsqueda del tesoro.
- **Overlay** sobre el juego: total, sesión, última partida (o partida en curso) y tesoros. Al terminar cada partida, el total cuenta hacia arriba y aparece un resumen: cofres, tesoros, puesto y si fue récord.
- **Ventana principal** con todo junto:
  - total, sesión, última partida y tesoros, con tendencia contra tu promedio (▲/▼);
  - gráfico de cofres **por partida** o **por día**, y **tiempo de juego** por día (sesión más larga, hoy y últimos 7 días);
  - **¿Cuándo saqueás?**: cofres por minuto desde que aterrizás, sumando todas tus partidas, con un consejo según tu forma de jugar;
  - **¿Dónde saqueás?**: mapa de calor de tus cofres sobre el mapa de la temporada, los lugares donde más saqueás y en cuáles te conviene caer (cuántos cofres terminás abriendo según dónde caés);
  - **chance acumulada del token de la tienda pirata** con los cofres de la temporada (1 en 137.500 por cofre, según Epic);
  - récord, tipos de cofre e historial. Al hacer clic en una partida se abre su **línea de tiempo** (cada cofre en su minuto y de qué cofre salió cada tesoro) y **tu recorrido en el mapa**.
- Queda en la **bandeja** (al lado del reloj) y puede **iniciar con Windows**.
- Opcional: **abrir y cerrar junto con Fortnite**. Cuando cerrás el juego, Botín cuenta tu última partida y se cierra; vuelve a abrirse solo con Fortnite.
- Si tenías **Fortnite Chest Counter**, trae tu historial solo y te ofrece desinstalarlo (va a la papelera).

## Uso

1. En Fortnite: *Configuración → Juego → Repeticiones → Grabar repeticiones: Sí*.
2. Para ver el overlay encima del juego: *Configuración → Video → Modo de ventana: Pantalla completa en ventana*.
3. Abrí `Botin.exe`. Listo.

| Atajo | Qué hace |
|---|---|
| **Ctrl+Shift+O** | Muestra u oculta el overlay |
| **Ctrl+Shift+L** | Desbloquea el overlay para arrastrarlo; otra vez para fijarlo (o doble clic) |

En **Ajustes** se cambian el tamaño, la opacidad del fondo y los datos que muestra el overlay. También está el **modo compacto**, que muestra solo el total y despliega el resto al terminar cada partida.

## Es seguro

Solo lee los archivos `.replay` de `%LOCALAPPDATA%\FortniteGame\Saved\Demos`. No modifica el juego y no lee su memoria.

Se conecta a internet solo para dos cosas, y ninguna manda datos tuyos:

- Bajar el **mapa de la temporada** (imagen y nombres de los lugares) de [fortnite-api.com](https://fortnite-api.com), una API pública. Se apaga en *Ajustes → Bajar el mapa de la temporada*.
- Fijarse en las [Releases de GitHub](https://github.com/lvxro/botin/releases) si hay una **versión nueva**. Si hay, aparece un aviso y con un clic se baja, reemplaza a `Botin.exe` y se vuelve a abrir; tus datos quedan como están. No se instala nada sin que toques *Actualizar*. Se apaga en *Ajustes → Avisarme si hay una versión nueva*.

La ubicación de cada cofre sale de tu posición en el replay en el momento de abrirlo. Las partidas cuyo replay ya no está en la PC no tienen ubicación.

Tus datos quedan en `%APPDATA%\Botin` (`data.json` es tu historial completo).

## Compilar

Necesitás el SDK de .NET 10. Ejecutá `build.bat` y vas a tener `dist\Botin.exe` (un solo archivo, sin instalar nada).

Para publicar una versión: subí `<Version>` en `src/Botin/Botin.csproj`, compilá y creá una Release con el tag `vX.Y.Z` (por ejemplo `v1.2.0`) adjuntando `dist\Botin.exe` con ese nombre. Las copias de Botín abiertas la detectan solas.

## Créditos

El lector de replays (carpeta `lib`) y la lógica de detección de cofres y tesoros (`src/Botin/Engine/ReplayAnalyzer.cs`) vienen de [Fortnite Chest Counter](https://github.com/LED-N/Fortnite-ChestCounter) (LED-N, licencia MIT, ver `LICENSE-ChestCounter`), que a su vez usa [FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) (Shiqan, licencia MIT, ver `lib/LICENSE-FortniteReplayDecompressor`). La interfaz, el overlay y el resto de la app son nuevos.
