# Botín

Contador de cofres de Fortnite. **Una sola app**: el contador, las estadísticas y el overlay del juego viven en el mismo programa. No hay que abrir el overlay, el contador y el reporte por separado.

## Qué hace

- Lee los replays que Fortnite guarda al terminar cada partida y suma los cofres que abriste, más los **tesoros** de la búsqueda del tesoro.
- **Overlay** sobre el juego: total, sesión, última partida (o partida en curso) y tesoros. Al terminar cada partida, el total cuenta hacia arriba y aparece un resumen: cofres, tesoros, puesto y si fue récord.
- **Ventana principal** con todo junto:
  - total, sesión, última partida y tesoros, con tendencia contra tu promedio (▲/▼);
  - gráfico de cofres **por partida** o **por día**;
  - **¿Cuándo saqueás?**: cofres por minuto desde que aterrizás, sumando todas tus partidas, con un consejo según tu forma de jugar;
  - récord, tipos de cofre e historial. Al hacer clic en una partida se abre su **línea de tiempo**: cada cofre en su minuto y de qué cofre salió cada tesoro.
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

Solo lee los archivos `.replay` de `%LOCALAPPDATA%\FortniteGame\Saved\Demos`. No modifica el juego, no lee su memoria y no se conecta a internet.

Tus datos quedan en `%APPDATA%\Botin` (`data.json` es tu historial completo).

## Compilar

Necesitás el SDK de .NET 10. Ejecutá `build.bat` y vas a tener `dist\Botin.exe` (un solo archivo, sin instalar nada).

## Créditos

El lector de replays (carpeta `lib`) y la lógica de detección de cofres y tesoros (`src/Botin/Engine/ReplayAnalyzer.cs`) vienen de [Fortnite Chest Counter](https://github.com/LED-N/Fortnite-ChestCounter) (LED-N, licencia MIT, ver `LICENSE-ChestCounter`), que a su vez usa [FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) (Shiqan, licencia MIT, ver `lib/LICENSE-FortniteReplayDecompressor`). La interfaz, el overlay y el resto de la app son nuevos.
