# Fortnite Chest Counter (español)

[English](README.md) · **Español**

Lee las repeticiones que Fortnite guarda en el PC al terminar cada partida y lleva la cuenta de
los cofres que abres, tus kills, el puesto y los minutos, en un panel que se abre en el navegador.
No usa IA ni toca el juego: solo lee los archivos `.replay` del disco.

## Arrancar

1. Descarga `FortniteChestCounter.exe` de la última versión en la página **Releases** del
   repositorio y ponlo en una carpeta tuya (Escritorio, Documentos…), no en Archivos de programa.
2. Ábrelo. La primera vez Windows puede avisar con «Windows protegió tu PC», porque el programa no
   está firmado: pulsa «Más información» y «Ejecutar de todas formas».
3. Se abre el panel en el navegador (`http://127.0.0.1:5380`). No hay ninguna ventana que dejar
   abierta: el programa se queda en un icono junto al reloj mientras juegas, y cada partida
   aparece sola al terminar. Doble clic en el icono vuelve a abrir el panel; con el botón derecho,
   «Salir». Si lo abres estando ya en marcha, te abre el panel de la copia que ya estaba.

   Windows 11 esconde los iconos nuevos detrás de la flecha **^** de al lado del reloj; sácalo de
   ahí para tenerlo a la vista.

Fortnite tiene que tener activada la grabación de repeticiones en sus ajustes.

## Actualizaciones

El programa comprueba en GitHub si hay versión nueva al arrancar y cada pocas horas. Si la hay,
aparece una barra con el botón «Actualizar»: descarga el `.exe` nuevo, sustituye al actual y se
reinicia solo; la página se reconecta. Tus partidas y ajustes están en
`%LOCALAPPDATA%\FortniteChestCounter` y no se tocan.

**Novedades**: el botón de la cabecera abre un cuadro con lo que ha entrado en cada versión, con
su fecha y un resumen por puntos. Mientras quede algo sin leer, el botón lleva un punto dorado.
Para anunciar una versión nueva se añade una entrada arriba de `ARR_NEWS`, en
`src/ChestTracker/ui/index.html`, con la fecha y una línea por cambio en los tres idiomas.

## Idioma

Español, inglés y francés, con los botones ES / EN / FR de la cabecera. Por defecto, el idioma del
navegador.

## Compilar desde el código

Con el **SDK de .NET 10** (https://dotnet.microsoft.com/download/dotnet/10.0): `start.cmd`
compila y arranca; `publish.cmd` genera `dist\FortniteChestCounter.exe`, un único archivo que no
necesita .NET instalado. Una copia compilada a mano no se actualiza sola.

## Qué cuenta

| Dato | De dónde sale |
|---|---|
| Cofres, cofres especiales, cofres de espíritu | Cada contenedor guarda qué jugador lo abre; se cuentan los tuyos, una sola vez cada uno |
| Kills | El recuento oficial de la partida. La línea de tiempo muestra derribos y eliminaciones por separado |
| Puesto | El de tu equipo, con el total de jugadores |
| Minutos de partida | Hasta tu última eliminación, o la partida entera si sobrevives |
| Minuto de cada cofre y kill | Tiempo desde que empieza la repetición |

Reglas del recuento:

- Un cofre cuenta tanto si lo abres con la tecla como si lo rompes (el botín cae igual). El panel
  dice cuántos de cada forma, y la lista «Cofres de la partida, uno a uno» da el minuto de cada uno
  para comprobarlo en el visor de repeticiones de Fortnite.
- Cada cofre cuenta una vez por partida, aunque te alejes y vuelvas a pasar por delante.
- **Cofres rellenados**: algunos cofres ya abiertos se vuelven a llenar durante la partida (se ve
  sobre todo a partir del minuto 10). Si uno vuelve a aparecer cerrado y lo abres, cuenta otra vez.
- **Roto de rebote**: si rompes lo que sostiene un cofre (un techo, un contenedor colgado) o cae
  junto a lo que estabas rompiendo, el juego no apunta quién lo rompió. Ese cofre suelta el botín
  en el mismo instante: si aparece a menos de 10 m de ti y eres el jugador más cercano, cuenta
  como tuyo. En la lista sale como «Roto de rebote».
- Un cofre que rompe la tormenta o alguien lejos de ti no se atribuye a nadie.
- **Cofre especial (azul)**: se reconoce por el nivel de botín que Fortnite le asigna, no por el
  nombre de la clase, porque un cofre normal del mapa puede salir especial. En Ajustes aparece
  como una clase aparte («… (botín nivel 2)») y puedes cambiarle la categoría.

Además: cofres por minuto, minuto del primer cofre, cajas de munición y otros contenedores,
daño hecho y recibido, precisión, asistencias, reanimaciones y distancia recorrida.

Paneles de equipo:

- **Tu squad en la partida**: kills, derribos, veces derribado, minuto de eliminación,
  plataforma y si dio las gracias al conductor, para cada compañero. El daño solo lo guarda
  Fortnite para quien graba la repetición.
- **Amigos con los que más juegas**: partidas juntos, sus kills por partida, puesto medio y
  mejor puesto, y tu media de cofres en esas partidas. Si hay compañeros repetidos, los de una
  sola partida no se muestran.

## El panel

- **Editar panel**: arrastra los bloques para moverlos, cambia su ancho con − y +, ocúltalos, y
  recupera los ocultos desde la barra de arriba. En la tabla puedes elegir columnas y sacar una
  partida de las estadísticas («No contar»). Todo se guarda solo.
- **Todas / Última sesión / Hoy / 7 días**: qué partidas entran en los totales y gráficos.
  Una sesión se corta cuando pasan más de tres horas entre dos partidas.
- Pulsa una fila de la tabla o una barra de un gráfico para ver esa partida arriba.
- **Minimizar**: cada bloque se puede plegar a su título.
- **Tabla de partidas**: filtra por modo y por puesto, elige cuántas filas ver (10, 25, 50 o todas)
  y qué columnas. Los filtros de la tabla no cambian los totales ni los gráficos.

## Pestaña En vivo

Sigue la partida que estás jugando.

- **Mientras juegas**: «EN CURSO», el reloj desde que empezó la partida, la cuenta atrás hasta el
  minuto 10 y tu ritmo habitual a esas alturas (cuántos cofres sueles llevar, tu mejor partida, tu
  récord). El reloj sale de la hora de inicio que Fortnite escribe en la repetición.
- **Al terminar**: el programa lee el registro de Fortnite (`FortniteGame.log`, en
  `%LOCALAPPDATA%\FortniteGame\Saved\Logs`) y sabe al momento que la partida ha acabado:
  «PARTIDA FINALIZADA», o «FINALIZADA · BUSCANDO PARTIDA» si has pulsado «Volver a jugar».
- **El recuento** llega cuando Fortnite cierra la repetición: al volver a la sala, o al empezar la
  siguiente partida si pulsaste «Volver a jugar» (hasta entonces el juego sigue grabando).
- **El resumen se queda** hasta 30 segundos después de que empiece la siguiente partida, con una
  cuenta atrás, mientras cargas y vas en el autobús. Después pasa a la partida nueva.
- Si no encuentra el registro, todo funciona igual salvo el aviso de fin de partida y de búsqueda.
  Opción `--log <ruta>` para indicar otro archivo.
- **Colores**: cada cifra se compara con tu propia media. Verde con ▲ si la mejoras, rojo con ▼ si
  te quedas por debajo, dorado si igualas o bates tu récord o quedas primero. En el gráfico, los
  minutos por encima de tu media salen en verde.
- **Tesoro nuevo**: probabilidad de que la partida te haya dado alguno de los tesoros que aún no
  has tachado en la pestaña Tesoros. Los Gizmos no cuentan.
- **Por qué el contador no sube en directo**: Fortnite cifra la repetición y no escribe la clave
  en el archivo hasta que la partida termina. Sin la clave no se puede leer nada de lo que pasa
  dentro. Si algún día la clave estuviera desde el principio, el programa lo detecta solo y la
  pestaña mostraría cofres y bajas durante la partida. En `chestcounter.log` queda apuntado el caso
  de cada partida («Partida en curso … solo reloj»).

## Avisos

Lo que hace el programa en segundo plano se ve siempre: una barra dorada arriba y una tarjeta
abajo a la izquierda con «Leyendo repeticiones: 3 de 8» mientras lee, y avisos breves abajo a la
derecha (partida leída con sus cofres, partida en curso, finalizada, buscando, errores). Se
cierran solos.

## Pestaña Tesoros

Un cartón de bingo con los tesoros que puede dar un cofre esta temporada (Treasure Hunters) y las
probabilidades por cofre que publicó Epic el 7 de noviembre de 2026.

- Cada casilla muestra la probabilidad por cofre y la de que ya te haya tocado con los cofres que
  llevas: los de la semana para los tesoros semanales, los de la temporada para el resto.
- Los dos porcentajes de arriba (que ya te hubiera tocado alguno, y que te lo dé el próximo cofre)
  cuentan solo los tesoros sin tachar, y nunca los Gizmos.
- «Lo tengo» tacha la casilla y la deja en gris. Los Gizmos, que pueden repetirse, llevan contador.
- Las casillas semanales se guardan por semana; con «Semana anterior» ves las pasadas.
- «Editar tesoros»: nombre y foto del objeto de la semana (pega una captura con Ctrl+V, suelta una
  imagen, elige un archivo o pon una dirección), probabilidades, casillas nuevas, fechas de la
  temporada, día y hora del cambio de semana, cofres que el contador no vio y las Horas del tesoro
  con sus multiplicadores.
- Epic no publica ni los objetos de cada semana ni la hora del cambio (por defecto, domingo a las
  14:00 UTC), ni dice si todos los tipos de cofre cuentan igual.

## Ajustes

- **Carpeta de repeticiones**: por defecto `%LOCALAPPDATA%\FortniteGame\Saved\Demos`.
- **Tu ID de Epic**: se rellena solo con la primera repetición.
- **Qué cuenta como cada cofre**: Fortnite da a cada contenedor un nombre interno. El programa
  los reparte así, y puedes cambiar cualquiera desde el desplegable:

  | El nombre contiene | Cuenta como |
  |---|---|
  | `Relic`, `Sprite` o `Spirit` | Cofre de espíritu |
  | `RareChest` | Cofre especial |
  | `Ammo` | Caja de munición |
  | `Chest` (el resto) | Cofre |
  | otra cosa | Otros contenedores |

  Los tipos nuevos de una temporada aparecen en esa lista en cuanto alguien abre uno cerca de ti.

- **Leer repeticiones** (ninguna opción borra partidas):

  | Botón | Qué hace |
  |---|---|
  | Buscar nuevas ahora | Lee solo las que no están guardadas o han cambiado |
  | Reintentar las que fallaron | Además reintenta las que dieron error |
  | Volver a leer todas | Lee de nuevo todas las de la carpeta y sustituye sus datos |

- **Borrar datos** (pide confirmación; no toca los archivos de Fortnite):

  | Botón | Qué hace |
  |---|---|
  | Borrar las partidas | Borra las partidas guardadas y lee la carpeta desde cero. Se pierden las partidas cuya repetición ya no existe |
  | Borrar todo | Lo anterior más el ID de Epic, la clasificación cambiada, las partidas «No contar», la disposición del panel y la pestaña Tesoros |

## A tener en cuenta

- La partida se cuenta **al terminar**, en cuanto Fortnite cierra la repetición. Si el juego se
  cierra a mitad de partida, la repetición queda cifrada sin clave y no se puede leer (saldrá en
  los avisos).
- Las partidas leídas se guardan en `%LOCALAPPDATA%\FortniteChestCounter` y siguen ahí aunque Fortnite
  borre la repetición. Si el programa no estaba abierto y la repetición ya se borró, esa partida
  se pierde.
- De tus compañeros, tu repetición guarda al personaje toda la partida (por eso puedes seguir su
  cámara en el visor), pero los cofres y el botín solo a unos 300 m de ti. Por eso el panel del
  squad no da cofres de los compañeros: su recuento completo solo está en su propia repetición.
- Tras una actualización grande de Fortnite puede que las repeticiones nuevas no se lean
  (aparecerá un aviso arriba). Se arregla actualizando la biblioteca de `vendor/`; ver
  `vendor/FortniteReplayDecompressor/ORIGEN.md`.

## Si alguna repetición no se lee

Cada repetición se lee por separado, empezando por la más reciente, con un límite de 90 segundos.
Si una falla o se cuelga, las demás se leen igual. El aviso de arriba («Ver por qué») y
`%LOCALAPPDATA%\FortniteChestCounter\chestcounter.log` dicen qué archivo falló y el motivo.
`start.cmd --dump "ruta\al\archivo.replay"` prueba una sola repetición a mano.

## Opciones de línea de órdenes

```
start.cmd --port 5400            otro puerto
start.cmd --demos "D:\Replays"   otra carpeta de repeticiones
start.cmd --no-browser           no abrir el navegador
start.cmd --dump partida.replay  volcar lo que se extrae de una repetición, en JSON
```

## Código

```
src/ChestTracker/
  ReplayParser.cs    lector de repeticiones: contenedores, jugadores, posiciones, cabecera
  Summarizer.cs      clasificación de cofres y resumen de cada partida
  ReplayWatcher.cs   vigila la carpeta, sigue la partida en curso y lee lo nuevo
  ParseProcess.cs    lee cada repetición en un proceso aparte, con límite de tiempo
  Store.cs           ajustes y partidas en JSON
  Program.cs         servidor local y API
  TrayIcon.cs        el icono de al lado del reloj: panel, lectura y salida
  Instance.cs        una sola copia: la segunda abre el panel de la primera y se cierra
  ui/index.html      el panel (un solo archivo, va dentro del ejecutable)
vendor/FortniteReplayDecompressor/   biblioteca que descifra y descomprime el .replay (MIT)
```

Para retocar el panel sin recompilar: define la variable de entorno `FCC_UI` con la ruta de
`ui\index.html` y recarga la página.
