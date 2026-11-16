# Mejoras pendientes

Lista de trabajo del proyecto, de lo más importante a lo más pequeño. Cada punto dice **qué**,
**por qué** y **cómo**, para poder retomarlo sin volver a investigarlo.

Lo marcado como *comprobado el 14/11/2026* se verificó contra el código o contra la API real ese
día; lo demás es diseño sin probar.

## Para qué existe esta app

Contar cofres es la excusa. Lo que de verdad hace es **dar motivos para abrir más cofres**:
cifras, ritmo, qué sitio renta más y cuánta suerte estás teniendo de verdad. Toda mejora debería
medirse contra eso. Por eso la pestaña Tesoros no es un adorno: es el único sitio donde abrir un
cofre tiene una recompensa visible al momento.

---

## Estado de lo acordado

| # | Punto | Estado |
|---|---|---|
| 1 | Guardar dónde está cada cofre, y releer lo que quede en la carpeta | **Hecho** · 100 % de las aperturas propias posicionadas |
| 2 | Copia de seguridad (guardar, CSV y restaurar) | **Hecho** |
| 3 | Pestaña Mapa | **Primera versión hecha** · calor, zonas y reparto |
| 4 | Comparativa de mejora por días/semanas/meses | **Hecho** |
| 5 | `matches.json` partido por partida | Pendiente |
| 6 | Tests del parser | **Hecho** · referencia de una repetición real, con el diff en palabras |
| 7 | Imágenes y nombres desde la API (2.1) | **Hecho** · buscador sobre los cuatro catálogos |
| 8 | Semanas cortadas al final del evento | **Hecho** · cinco semanas, la última acaba el 5 de diciembre |
| 9 | Tesoros oficiales de cada semana por defecto | **Hecho** · semanas 1 y 2, editables, con «Volver al oficial» |
| 10 | Escuadrones: crear, entrar, gestionar hasta 4 y publicar | **Hecho** · carpeta compartida detectada sola |

---

## 1. Compartir los datos del squad

La más difícil y la más valiosa.

**El problema.** Tu repetición solo graba los cofres abiertos a unos 300 m de ti. Por eso el panel
del squad enseña las kills de tus compañeros pero no sus cofres: esa información no existe en tu
archivo. Está en el suyo. Si ellos también usan el programa, entre todos tenéis la partida
completa y nadie la ve entera.

**La pieza que lo hace posible.** `MatchRecord.SessionId` es el identificador de la partida que
escribe Fortnite, y **es el mismo para los cuatro**. Sirve de clave para cruzar los datos sin
ambigüedad. Ya se guarda; hoy no se usa para nada.

**Qué se intercambia.** No el `MatchRecord` entero (unos 50 KB), sino una *tarjeta de partida* de
unos cientos de bytes por jugador: id de Epic, nick, `SessionId`, hora de inicio, cofres por
categoría, kills, puesto, minutos y minuto del primer cofre.

**Por qué no se comparte el backup.** Era tentador: ocupa poco y la fontanería ya existe. Pero
`ImportMatches` indexa por `objMatch.File`, el nombre del `.replay`, y de una misma partida cada
jugador tiene un archivo con nombre distinto. Importar el backup de un compañero no fusionaría
nada: metería sus partidas como **tuyas** (136 pasarían a más de 500, con sus cofres contados como
propios). Hace falta una ruta de fusión aparte de todos modos, así que la ventaja de reaprovechar
desaparece y quedan solo los inconvenientes: 952 KB hoy y ~5,5 MB a fin de temporada (~7 KB
comprimidos por partida), frente a ~250 bytes de una tarjeta; y de cada partida arrastra los 80
jugadores con id, nick, plataforma y nivel (unos 11.000 desconocidos en una semana), todas las
eliminaciones del lobby y la x/y de cada cofre. *Comprobado el 14/11/2026.*

**La arquitectura: una carpeta compartida, un escritor por archivo.** Cada uno escribe solo
`<su-id-de-epic>.json` y no toca el de nadie. Ese invariante es lo que permite no tener servidor,
ni cuentas, ni bloqueos, y esquiva lo único que OneDrive y Drive hacen mal: dos personas editando
el mismo archivo. El cruce se hace por `SessionId`, que es idéntico en los cuatro.

**Por qué no hay «URL y contraseña».** Se valoró y no se puede: el problema no es la contraseña,
es **escribir**. Cada miembro tiene que escribir su propio `<id>.json`, y en Drive, OneDrive y
Dropbox **no existe la escritura anónima** — escribir siempre exige estar autenticado. Una URL
pública, con o sin contraseña, da como mucho lectura, así que el grupo tendría a cuatro personas
mirando y a ninguna publicando. (Y Google Drive no ofrece contraseñas de enlace en absoluto.)
Lo que sí daría URL + token es un repo privado o un jsonbin, y eso es el plan C con otro nombre.
El enlace de compartir hace de URL, y la lista de permisos del proveedor hace de contraseña: va
por persona, se revoca de una en una y no hay un secreto suelto en un chat.

**Qué se automatiza.** El objetivo es que sean dos clics para quien crea el grupo y uno para el
resto, sin que nadie escriba una ruta:

- Las raíces de nube se detectan solas: `%OneDrive%` y sus variantes, Dropbox desde
  `%APPDATA%\Dropbox\info.json`, y Google Drive por la etiqueta del volumen.
- Un marcador `grupo.json` hace el grupo encontrable: se busca `grupo.json` hasta dos niveles bajo
  cada raíz, con tope de 600 carpetas. **Cuando OneDrive comparte una carpeta, aparece en la raíz
  del invitado con el mismo nombre**, así que la ruta es predecible sin comunicarla.
- Las propias repeticiones confirman quién es quién: los compañeros de equipo de las últimas 40
  partidas se cruzan con los miembros del grupo, para decir «has jugado con 2 de ellos hace poco».
  Y al revés: avisa de a quién juegas y aún no has invitado.

**Lo que no se automatiza:** compartir la carpeta en OneDrive, y que el invitado acepte el enlace.
Exige la UI del proveedor o su API con OAuth, que es el plan C. Aceptar es justo lo que pone la
carpeta en el disco del invitado, y hasta que no está en el disco el programa no la puede ver.
Por eso la pantalla de entrar **pide el enlace pero no lee datos de él**: solo lo abre en el
navegador, y después busca en el disco.

**Pestaña propia, no en Ajustes** (`renderSquad`, contenedor `#elSquad`). Estaba enterrada en el
cajón de ajustes y es lo primero que alguien necesita encontrar.

**La espera es el problema, no la búsqueda** — *aprendido probándolo con una persona real el
15/11/2026*. Aceptar el enlace **no baja la carpeta al instante**: OneDrive tardó unos **dos
minutos**. Durante esa espera el botón «búscalo» no decía nada, así que parecía roto cuando en
realidad funcionaba. Arreglado en tres sitios:

- `GET /api/squad?fresh=true` salta la caché de 8 s. Sin eso, pulsar el botón podía devolver un
  «no está» guardado de antes aunque la carpeta ya hubiera llegado. Ese sí era un bug.
- Mientras se espera, el programa **relee el disco solo cada 5 s** y la carpeta aparece sin pulsar
  nada. Antes había que adivinar cuándo reintentar.
- Un panel dice **en qué nubes está mirando**, cuántos segundos lleva y que la nube puede tardar un
  par de minutos. Si no detecta ninguna nube, lo dice en vez de buscar en silencio.

**La pantalla, en cuatro vistas** (`objApp.strSquadView`):

- **Elección** — «¿Te han invitado?» y «¿Lo montas tú?», los dos botones **al mismo peso**. Esto
  no es estética: antes el botón grande era «Crear», y los tres invitados que aún no habían
  aceptado el enlace lo habrían pulsado, creando cuatro grupos distintos que no se ven entre sí.
- **Invitado** — pega el enlace → se abre en el navegador → «Ya lo he aceptado, búscalo».
- **Creador** — pasos numerados, crear la carpeta y compartirla con permiso de edición.

**Estado: pasos 1 y 2 hechos** (`Squad.cs`, `SquadPublisher.cs`, `/api/squad*`, sección
«Escuadrones» en los ajustes). Crear, encontrar, entrar, salir y publicar funcionan, con **hasta
4 escuadrones a la vez**. Verificado con nubes falsas y Chrome headless el 14/11/2026: detección,
lectura, archivo intruso ignorado, normalización de rutas, publicación de 152 partidas reales en
dos carpetas, el tope de 4, salir sin borrar nada, enlaces inválidos rechazados y los tres
idiomas.

**Varios escuadrones.** `SquadPaths` es una lista; `SquadPath`, el ajuste antiguo de una sola
carpeta, se funde en ella al arrancar para no echar a nadie de su grupo al actualizar. El
publicador escribe tu misma ficha en cada carpeta y lleva la cuenta por carpeta, así que una que
esté sin conexión no impide publicar en las demás, y solo da la revisión por hecha cuando han
entrado todas.

**Cómo publica (`SquadPublisher`).** Vigila `Store.Revision` cada 5 s en vez de colgarse de cada
`SaveMatch`: así una relectura en bloque de 200 repeticiones cuesta **una** escritura y no 200.
Reescribe la lista **entera** cada vez en lugar de añadir al final, y eso es lo que la hace
auto-reparable: una partida leída tarde, una pasada perdida con la carpeta sin conexión o un
archivo que vuelve dañado se arreglan solos en la siguiente escritura, sin guardar estado de qué
se envió. Una firma (número de tarjetas, cofres totales y la última partida) evita reescribir
cuando la revisión se movió por otra cosa. Medido: **308 bytes por partida**, 45 KB con 148.

**Pasos 3 y 4 hechos** (`SquadReader.cs`). Lee las fichas de todas las carpetas cada 6 s a un
diccionario `sessionId -> jugador -> ficha`, en un almacén aparte que **nunca** toca
`_dicMatches`: lo que comparte el squad es dato de solo lectura de otros PCs y se enseña **al
lado** de tus cifras, nunca sumado a ellas. `/api/revision` devuelve la suma de las dos
revisiones, así que la página se refresca también cuando publica un compañero. Verificado con
fichas de los 4 compañeros reales sobre 152 partidas: el cruce por `sessionId` acierta.

Dónde se ve: columna **Cofres** en *Tu squad en la partida*, columna **Sus cofres contigo** en
*Amigos*, y la tarjeta **Cofres del squad en esta partida** en *En vivo* al terminar, con barras
y la corona animada.

**Tres reglas de la corona, decididas sobre el uso real:**

- Se corona **en cuanto publique un compañero**, no cuando publiquen todos: esperar a los cuatro
  puede ser esperar para siempre, y la tarjeta se quedaba muda.
- Una **estimación nunca gana**. Si alguien no comparte, se recurre a lo que tu propia repetición
  le vio abrir cerca de ti (unos 300 m): es un **suelo**, no un total, y se marca `≥` y «estimado».
  Coronar un suelo sería inventárselo.
- Una estimación de **cero** no es una estimación: es que no se le vio nunca. Se muestra «Sin
  datos», sin barra y sin `≥0`.

El suelo por cercanía sale de `MatchRecord.Openings`, que graba a cualquiera que abra cerca de ti,
no solo a ti. Se usa **únicamente** cuando no hay ficha compartida.
- **C — Servidor propio.** Sincronización de verdad, pero trae hosting, cuentas, privacidad y
  mantenimiento permanente. Solo si esto llega a tener usuarios que no se conozcan entre ellos.

**Decisiones tomadas que conviene no deshacer:**

- Un archivo del grupo solo se cree si su nombre es un id de Epic y el contenido declara ese mismo
  id. Eso filtra las copias de conflicto que OneDrive deja con el nombre del equipo pegado detrás.
- El estado «ok» se deduce de que tu propio archivo esté en la carpeta, sin sondear el disco: la
  sección se refresca cada pocos segundos y sondear significaría escribir en una carpeta
  sincronizada, con el ruido llegando a los cuatro. Solo se sondea si tu archivo no está.
- `ResetSettings` **conserva** `SquadPath`: el grupo es estado compartido con otras personas, no
  una preferencia de este PC. Salir tiene su propio botón.
- Un compañero cuenta como «de tu squad» solo si aparece en **3 o más** de las últimas 40
  partidas. Medido sobre datos reales: el squad fijo sale en 29-39 de 40, y cada desconocido que
  mete un relleno sale exactamente en 1. Sin el umbral, el aviso de a quién falta invitar listaba
  nueve nombres, y un random dentro de un grupo ajeno lo habría avalado como «el tuyo».
- Las tarjetas usan las categorías del propio jugador (`ClassOverrides`), para que lo que comparte
  cuadre con lo que ve en su panel. Las partidas excluidas de sus estadísticas no se comparten.
- La sección de ajustes enumera los nueve campos que salen del PC, y **también lo que no sale**.
  Si se añade un campo a `SquadCard`, hay que añadirlo ahí y en los tres idiomas.
- Cada escuadrón se titula **por quién está dentro**, no por la carpeta: todas se llaman
  `FortniteChestCounter`, así que con dos escuadrones el path no distingue nada y los nombres sí.
- Toda ruta que llega de la página pasa por `TidyPath` antes de compararse, guardarse o
  mostrarse. Sin eso, la misma carpeta escrita con la otra barra cuenta como un escuadrón
  distinto, y salir de uno no encontraba cuál era.
- Salir no borra nada. Tu archivo se queda y el escaneo vuelve a encontrar el grupo, así que
  volver a entrar es un clic.

**Privacidad, innegociable.** Compartir tiene que ser opt-in y decir exactamente qué sale de tu
PC. Nunca por defecto, nunca sin que se vea.

**Qué desbloquea:**

- Cofres reales de cada compañero en el panel del squad, donde hoy hay un guion.
- Quién farmea más del grupo, por partida y en el total.
- Comparativas entre usuarios, que es lo que convierte el registro en pique.
- **Muestra agregada para los porcentajes.** Con los cofres de cuatro personas durante una
  temporada hay volumen suficiente para contrastar las probabilidades publicadas por Epic con
  las observadas de verdad. Esto enlaza directo con «suerte real» de la sección siguiente.

---

### 1.1 Qué hay en el registro de Fortnite — *comprobado el 14/11/2026*

Repasado el `FortniteGame.log` real (621.480 líneas) buscando algo que mejore **En vivo**:

- **Útil y sin usar: `LogParty`.** `New party member state for [CHEESCA_97] Id [MCP:973e5...c656d]`
  da **quién está en tu grupo ahora mismo**, con el nick en claro y el id de Epic abreviado
  (5 primeros y 5 últimos). Contra los 4 ids de tu escuadrón eso es inequívoco. Permitiría que
  En vivo diga con quién juegas antes de empezar y quién de ellos comparte datos. **Pendiente.**
- **No existe: la muerte de un compañero en vivo.** Buscados `DBNO`, `Downed`, `Revive`,
  `Eliminat`, `PlayersLeft` y los marcadores de mundo: todo es ruido de assets, de UI o de
  marcadores que colocas tú. El estado del squad dentro de la partida **no se escribe al log**.
  Solo llega con la repetición, al terminar. No merece la pena volver a buscarlo.
- Ya se usan: inicio/fin de grabación, fin de partida y búsqueda de partida.

### 1.2 El minuto óptimo de salida — *hecho*

El «minuto 10» era una regla del pulgar. Ahora se calcula con el **teorema del valor marginal**:
una partida es un parche que se agota y conviene saltar a otro cuando su rendimiento cae por
debajo de la media de todo el ciclo, reinicio incluido. Se maximiza `cofres(T) / (T + C)`, donde
`C` es el hueco real entre partidas, medido de tus propios `startedAt` + `durationSec` (mediana
de los huecos menores de 10 min; los mayores son descansos, no cola).

Con 152 partidas sale `C = 0,20 min` y **minuto óptimo 8**. Hace falta un mínimo de 8 partidas;
por debajo se usa el 10 de siempre. Al llegar al minuto suena un aviso de dos notas generadas con
WebAudio (sin archivo de audio, funciona sin conexión) y sale un toast. Una vez por partida.

## 2. Tesoros

El corazón de la app. Hoy es la parte con más mantenimiento manual y la que menos engancha.

### 2.1 Imágenes y títulos automáticos — **hecho**

**Cómo funcionan los tesoros, que es lo que hay que tener claro.** Cada semana hay **un solo**
objeto por casilla: una skin dorada, una skin de vehículo, un pico *o* una mochila, una pista de
improvisación, una pantalla de carga; y el token pirata, que no cambia de semana. No son los
cosméticos que salen en la tienda: **no se ven ni se compran en la tienda**, son objetos únicos
que esa semana solo se consiguen por probabilidad dentro de los cofres. Esto descarta de raíz
usar las novedades de la tienda para adivinarlos (ver «Pendientes menores»).

*Comprobado el 14/11/2026.* Antes pegabas una captura a mano. Ya no hace falta: `fortnite-api.com`
sirve el icono oficial y el nombre ya traducido. El objeto lo ves en el juego, escribes su nombre
y el programa trae el resto.

**Cómo quedó.** En «Editar tesoros», cada casilla tiene una caja de búsqueda. Al elegir un
resultado se guarda el nombre oficial y, acto seguido, se **descarga el icono y se encoge dentro
de los datos** como una captura pegada: así la foto sigue estando sin internet. Si la descarga
falla queda la dirección, que funciona mientras haya red. Pegar una captura, soltar un archivo o
escribir una dirección siguen estando, porque un objeto recién salido puede tardar en aparecer en
la API.

**No vale con buscar solo en battle royale, y esto casi se cuela.** `/v2/cosmetics/br/search/all`
cubre trajes, picos, mochilas, envoltorios y pantallas de carga, pero **no los coches ni las
pistas de improvisación**, que viven en catálogos aparte y no tienen buscador propio. Buscar
`Cadillac` en `br` devuelve **404**, y «CADILLAC CT5» era justo el tesoro de vehículo de una
semana real. Así que:

| Catálogo | Cómo se busca | Tamaño |
|---|---|---|
| `/v2/cosmetics/br/search/all` | en el servidor, con `matchMethod=contains` | — (bajarlo entero serían 20 MB) |
| `/v2/cosmetics/cars` | se baja entero una vez y se filtra aquí | ~1 MB, 1.858 objetos |
| `/v2/cosmetics/tracks` | igual | ~250 KB, 764 |
| `/v2/cosmetics/instruments` | igual | ~190 KB, 322 |

Los tres catálogos se piden **una vez por ejecución**, al entrar en «Editar tesoros», así que
cuando escribes ya suelen estar. Comprobado: `Cadillac CT5` → «Cadillac CT5-V Blackwing»
(carrocería y rueda), `Shut Up and Dance` → la pista con su carátula, y `cazatesoros` en
minúsculas encuentra «CAZATESOROS» en mayúsculas porque los nombres se comparan sin tildes ni
mayúsculas.

Ojo con las formas distintas en que responde la API: los objetos de battle royale traen
`images.icon`; los coches y los instrumentos solo `small` y `large`; y una pista de improvisación
**no tiene `name`**, tiene `title`, `artist` y `albumArt`. `readCosmetic` las aplana a una sola
forma. Dos coches con el mismo nombre no son un duplicado: son la carrocería y la rueda del mismo
modelo, con imágenes distintas.

Sin `searchLanguage` igual al idioma en que escribes, un nombre en español no encuentra nada: ese
parámetro es obligatorio, no un adorno. La API responde con `access-control-allow-origin: *`, así
que el navegador la llama directamente y no hace falta pasar por el programa.

### 2.2 Objetos de la semana y Horas del tesoro

**Los objetos de la semana se buscan solos desde el 16/11/2026.** No hay API que los liste, pero
sí hay una que tiene los objetos, y Epic los marca en su identificador interno:

| Casilla | Cómo se reconoce |
|---|---|
| Pico o mochilero | sufijo `Reward`: `Backpack_FledGravelReward` |
| Atuendo dorado | el mismo nombre en clave sin sufijo: `Character_FledGravel` |
| Pantalla de carga | `thw`, de *treasure-hunt week*: `loadingscreen_delilahthw3` |
| Vehículo | `TreasureHunt` en la calcomanía: `CarSkin_EagleTreasureHunt01` |
| Pista | no lleva marca: es la única pista añadida el día que abre la semana |

Dos rutas que se cubren la una a la otra: `/v2/cosmetics/new` (70 KB, lo que trajo el último
parche) y, para la pantalla de carga, una consulta por identificador, que funciona aunque el
parche ya no sea el último. **Con `?language=es` la API devuelve el nombre oficial ya traducido**,
que es toda la diferencia entre esto y una lista de nombres en clave: «Que la fortuna te
acompañe» sale tal cual.

Probado contra la API real el 16/11/2026, semana a semana: la 3 sale **entera y correcta**, las
1 y 2 dan su pantalla de carga (sus objetos ya no están en el último parche) y las 4 y 5 no dan
nada, que es lo que tienen que dar hasta que Epic las publique. Vaciando a mano los datos de la
semana 3 en una copia de la página, las cinco casillas se rellenaron solas con su nombre español
y su icono.

**La ventana va pegada al inicio de la semana, no a la semana entera**, y esto costó un fallo que
conviene no repetir: una semana termina justo cuando empieza la siguiente, así que una ventana
que cubría la semana completa se llevaba los tesoros de la siguiente. La semana 2 salía con los
cinco objetos de la 3, todos correctos y todos en la semana equivocada. Estrecha y a veces vacía
es mejor que ancha y a veces equivocada: una casilla vacía pide que la rellenes, una casilla con
lo que no es, no.

Reglas que conviene no deshacer:

- Lo que encuentra va a `objFound`, **aparte** de `objItems`, que es lo que escribes tú. Orden de
  preferencia: lo tuyo gana a lo escrito a mano en el programa, y eso gana a lo encontrado.
- La excepción es una entrada `bAnnounced`: el nombre que Epic anunció antes de que el objeto
  existiera, sin foto. Eso es un hueco tapado, así que lo encontrado sí la sustituye.
- La pista no lleva marca ninguna. Si hay dos candidatas el mismo día, **no elige**: deja la
  casilla como está.
- La calcomanía dorada del coche devuelve una imagen de relleno, así que la foto se saca del
  coche de debajo (`CarBody_<nombre>`), igual que se hacía a mano.
- El aviso de casillas sin tesoro sale en la pestaña con un botón para buscarlas al momento, y
  distingue la semana que no ha empezado todavía de la que ya empezó y sigue sin publicarse.

**Las Horas del tesoro siguen a mano**, y de momento se quedan así por decisión del 16/11/2026:
no se anuncian siempre, y una franja prevista por patrón (sábado 19:00 UTC, domingo 02:00 UTC)
sería inventarse el dato más vistoso de la pestaña. Cuando haya una nueva, se apunta y sale en
una release.

**Los tesoros oficiales vienen puestos de fábrica** (`OBJ_WEEK_ITEMS`), con la clave
`<inicio de la semana en UTC>|<casilla>`, igual que `objItems`. Lo que escribas en la pestaña gana
campo a campo, y «Volver al oficial» deshace el cambio de esa casilla. Añadir una semana nueva es
añadir cinco líneas ahí.

| Casilla | Semana 1 (1–8 nov) | Semana 2 (8–15 nov) |
|---|---|---|
| Atuendo dorado | A$AP Rocky de oro macizo | Lil Uzi Vert de oro macizo |
| Vehículo | Mercedes-AMG G 63 «G-Wagon» | Cadillac CT5-V Blackwing dorado |
| Pico o mochilero | Guanteletes de A$AP Rocky | Lil' Uzi Crew |
| Pista de improvisación | NO TRESPASSING | London |
| Pantalla de carga | Paraíso del saqueo | So Kawaii |
| Tienda pirata | 2026 y 2027, toda la temporada | igual |

**Cómo se encontraron los iconos, que tiene truco.** Los tesoros son exclusivos y la API los tiene
con nombres que no son los del juego: el atuendo dorado de A$AP Rocky figura como «Rugahand de oro
macizo» (`Character_SpillStrike`, nombre en clave) y «Lil' Uzi Crew» figura como «Oro y diamante»
(`Backpack_RumbaShowyReward`). Buscar por nombre no vale. Lo que sí vale es el **identificador
interno de Epic**, que marca los tesoros:

- sufijo `Reward` — `Pickaxe_SpillStrikeReward`, `Backpack_RumbaShowyReward`
- `thw` de *treasure-hunt week* — `loadingscreen_delilahthw` (semana 1) y `…thw2` (semana 2)
- `TreasureHunt01` en los coches — `CarSkin_BurnishedRavenTreasureHunt01`
- y la familia del nombre en clave se repite dentro de la semana: todo lo dorado de A$AP Rocky es
  `SpillStrike`, todo lo de Lil Uzi Vert es `RumbaShowy`

Encontrado eso, **se comprobó a ojo** cada icono contra la recompensa tal como la enseña el juego.
Siete de ocho coincidían exactamente. La octava, la piel dorada del Cadillac
(`CarSkin_BurnishedRavenTreasureHunt01`), devuelve una imagen de relleno con el logo de Fortnite,
así que esa casilla lleva la foto del coche. El acceso a la Tienda pirata no es un cosmético y no
está en la API: esa casilla se queda sin imagen oficial.

Para rastrear nuevos tesoros por identificador hay que bajarse el catálogo entero de battle royale
(`/v2/cosmetics/br`, **16 MB, 16.848 objetos**) y buscar por `id`, porque el buscador de la API
solo mira el nombre. Es una consulta de investigación, no algo que haga el programa.

**Los Gizmos son cinco objetos, no uno.** La semana 2 reparte Localizador de Sprites, Localizador
de la suerte, Extractor portátil, Acelerador de extracción y Fuego artificial dorado. La casilla
está oculta (ver «Pendientes menores»), así que esto queda apuntado por si se le da otro uso.

**Las franjas son un instante mundial, no una hora española.** Esto era lo fácil de equivocar. Epic
anuncia una ventana y cada web la escribe en su propia zona: la prensa estadounidense la da en ET,
la británica en GMT. Lo que es igual para todos es el instante. Por eso se guardan en UTC y la
pestaña las pinta con el reloj del PC que mira, con la zona escrita al lado
(`Intl.DateTimeFormat().resolvedOptions().timeZone`) y el UTC debajo para poder contrastarlo con lo
que publicó Epic. Un jugador en Madrid y otro en Los Ángeles ven horas distintas y el mismo momento.

**Apuntadas hasta ahora** (14/11/2026): 7 nov 19:00–21:00 UTC, 8 nov 02:00–04:00 UTC, 14 nov
19:00–21:00 UTC y 15 nov 02:00–04:00 UTC. Las horas del 14 son las que dio Epic; **los
multiplicadores de esas dos no los confirmó Epic**, los dio la prensa, y el ×2 de la segunda es el
patrón de la semana anterior y no un dato. Se pueden corregir en «Editar tesoros», y la pestaña lo
avisa en su nota al pie.

Las franjas nuevas que trae una versión se **mezclan una sola vez** con las que ya tenías
(`INT_TREASURE_VERSION` en `normalizeTreasure`, comparando por hora de inicio). Así una franja que
borraste a propósito no vuelve en cada arranque.

### 2.3 Que enganche — **hecho el 16/11/2026**

Lo que se hizo, con datos reales (199 partidas, 5.613 cofres de temporada):

- **Cuántos cofres te faltan.** Cada casilla dice «Te faltan unos 6.916 cofres para los 7.813 que
  cuesta de media», o «Ya has pasado de los 568». Es la misma cifra que el porcentaje, en la
  única unidad que el lector abre de verdad.
- **La barra.** Era un hilo de 6 px que con el 0,0015 % del atuendo no se veía. Ahora son 16 px,
  con degradado, brillo y una marca en la mitad para tener contra qué medir. Suelo del 1,5 % del
  ancho: los imposibles se ven como una astilla, que es honesto por los dos lados —hay algo, y
  es casi nada.
- **Suerte real.** «6,5 esperados con tus cofres; llevas 0: te está costando». La esperanza es la
  suma de la probabilidad de cada casilla sobre sus propios cofres, que es justo lo comparable
  con la cuenta: en una casilla que solo puede caer una vez, la probabilidad de tenerla **es** el
  número esperado. Las semanas que no han empezado no suman: no tienen cofres detrás y contarlas
  arrastraría la esperanza a cero y lo llamaría mala suerte. Con menos de 0,3 esperados no se
  juzga, se dice que es pronto.
- **Resumen de temporada.** «0 de 26 tesoros conseguidos».
- **Ritmo hasta el final.** Al ritmo de la temporada (cofres por día desde que empezó), cuántos
  quedan hasta el 5 de diciembre y hasta el domingo, y en qué dejaría eso cada casilla que falta:
  `96,3 % → 99,9 %`, `10,8 % → 31,7 %`, `0,057 % → 0,19 %`. Las semanales se proyectan al final
  de su semana y las de temporada al final del evento, porque es cuando cada una deja de poder
  caer. **Las Horas del tesoro se quedan fuera de la proyección**: las que vienen no están
  anunciadas, y contarlas sería inventarse la mejor parte de la respuesta. Y nunca se escribe
  100 %: es una probabilidad, la última décima no se cierra.

Lo que no se hizo, y por qué:

- **Avisar del multiplicador en la pestaña**: ya hay un distintivo en *En vivo* durante la Hora
  del tesoro, y repetirlo no añade nada.
- **El quesito por tesoro**: la barra grande hace el mismo trabajo y cabe en la tarjeta.

Ideas originales, por si se retoman:

- **Quesito por tesoro.** Un gráfico circular por objeto sin tachar: qué parte de la probabilidad
  ya has «consumido» con los cofres que llevas y cuánta queda. Ver el trozo crecer cofre a cofre
  es el gancho. Con 1 entre 1.562.500 para el atuendo dorado el trozo será invisible, y eso
  también cuenta una verdad: enseña a las claras lo imposible que es.
- **Probabilidad acumulada bien visible.** Ya se calcula; falta que se vea como una barra que
  sube, no como un porcentaje perdido en una casilla.
- **Cuántos cofres te faltan.** Para cada tesoro, `1/p` cofres esperados y cuántos llevas. «Te
  quedan unos 340 cofres para el pico» se entiende mucho mejor que «0,29 %».
- **Suerte real: observado contra esperado.** Lo que de verdad pide la app. Con tus cofres de la
  temporada, cuántos Gizmos *deberías* llevar y cuántos llevas. «Esperados 2,3, tienes 4: vas con
  suerte» es la frase que hace volver a la pestaña. Gana muchísimo con los datos del squad
  (punto 1), que multiplican la muestra.
- **Ritmo hasta el final de la temporada.** La temporada de cofres del tesoro acaba el **5 de
  diciembre de 2026**. Con tu media de cofres por día: cuántos te dará tiempo a abrir y qué
  probabilidad acumulada tendrás al terminar. Y, si vas corto, cuántos al día harían falta.
  *La mitad de abajo ya está*: las semanas se cortan al evento, son cinco, se numeran («Semana 2
  de 5») y la última acaba el 5 de diciembre en vez de correr siete días enteros. El reparto sale
  de `getEventWeeks`, que arranca en el primer cambio de semana desde la fecha de inicio —no a
  medianoche, o la primera semana duraría quince horas— y recorta la última contra la fecha de
  fin. La clave con la que se guardan las casillas tachadas sigue siendo el inicio **sin recortar**,
  para no perder lo ya marcado.
- **Qué es más óptimo.** Cuando esté el mapa (punto 3), cruzar zona con cofres por minuto
  responde «dónde caer para abrir más», que es la pregunta de quien farmea.

---

## 3. Pestaña Mapa — **primera versión hecha**

Ya está: mapa de calor de tus cofres sobre el mapa de la temporada, lista de zonas con cuántos
cofres abres en cada una, y el reparto por porcentaje. Se puede ver todo tu histórico o una sola
partida.

**La calibración, que era el riesgo, está medida** — y costó dos intentos. La imagen no cubre un
cuadrado centrado en el origen del mundo: la isla está descentrada y los dos ejes no llevan
exactamente la misma escala.

```
izquierda = (x - 80) / 2952 + 0,5        arriba = (y - 14) / 3024 + 0,5
```

Es decir, medio ancho 1.476 m y medio alto 1.512 m, con el centro de la imagen en (80, 14) m.
Sacado ajustando la densidad de 5.384 cofres de posición conocida contra la silueta de tierra, y
confirmado a ojo sitio por sitio sobre la imagen que lleva los nombres impresos.

**Tres formas de equivocarse, todas probadas:**

- *La Y invertida.* Es lo habitual en estos mapas y aquí es falso: el mundo crece hacia el sur,
  igual que la imagen. Con la Y invertida los puntos se reparten de forma plausible y en sitios
  equivocados.
- *Suponer 1.350 m centrados.* Daba un resultado que parecía bueno y dejaba los cofres decenas de
  metros fuera: en Plunder Port el calor caía en mar abierto y en Buggy's Beachhead en el desierto
  vacío. Un error de 40 m es un edificio entero.
- *Maximizar «cuántos cofres caen en tierra».* Está sesgado: ampliar la escala comprime todo hacia
  el centro, que es donde está la isla, así que el número sube siempre. Lo mismo vale, en menor
  medida, para la correlación con la silueta.

Lo que sí sirve: ajustar y **mirarlo**. Un sitio con nombre tiene que caer sobre sus edificios, y
eso se ve en un recorte a resolución nativa. Si en una temporada nueva el mapa deja de cuadrar,
hay que repetir esa medida; los números están en un solo sitio, `OBJ_MAP_FRAME`.

Lo que falta:

- **Tu ruta de la partida**: el replay ya guarda el recorrido del jugador, pero no se vuelca al
  disco. Haría falta guardar unos pocos puntos por partida, no la traza entera.
- **Dónde caes contra cuántos cofres sacas**, que es la pregunta de quien farmea.
- **Los círculos de tormenta**, que el replay ya trae (seis, con su radio).
- **Cofres por minuto por zona**, para responder qué sitio renta más y no solo dónde vas.

## 4. Copia automática {#copia-automática} — **hecho**

El programa guarda una copia al día en `<carpeta de datos>/copias/cofres-AAAA-MM-DD.json.gz`,
conserva las diez últimas y borra las viejas. Comprimida ocupa un octavo: 6,6 MB de partidas son
853 KB de copia.

Cuándo escribe: cuando cambian las partidas, con un hueco mínimo de 30 minutos para que una tarde
de partidas no sea una copia por partida, y saltándose ese hueco cada cinco partidas nuevas para
que la primera lectura de una carpeta entera no deje una copia con una sola partida dentro.

**Si al arrancar no están las partidas, vuelven solas** de la copia más reciente, y queda dicho en
`chestcounter.log`. Borrarlas a propósito no se deshace: un borrado deja una lista vacía, que se
lee perfectamente, y solo se recupera cuando el archivo falta o está roto.

En Ajustes se ven las copias con su fecha y tamaño, con un botón para recuperar cualquiera y otro
para guardar una al momento.

**Lo que esto NO cubre:** las copias viven en la carpeta de datos. Protegen de una escritura mala,
de un borrado accidental y de un archivo roto, pero no de perder el disco. Para eso sigue haciendo
falta llevarse de vez en cuando una copia manual fuera del PC.

## 5. `matches.json` partido por partida

Hoy es un único archivo de 6,6 MB que se reescribe **entero** cada vez que termina una partida.
A tu ritmo no molesta, pero crece lineal: con 500 partidas serían unos 25 MB reescritos cada vez.

Qué hacer: un archivo por partida, o añadir al final en vez de reescribir. Hay que mantener la
escritura a archivo temporal y renombrado que ya existe, que es lo que evita quedarse con medio
archivo si se corta la luz.

---

## 6. Tests del parser — **hecho el 16/11/2026**

`dotnet test src/ChestTracker.Tests/ChestTracker.Tests.csproj`. Lee las repeticiones que haya en
`tests/fixtures`, las pasa por el lector de verdad (`ParseProcess.ParseWithLargeStack`, pila
grande incluida) y compara el resultado contra la lectura que se dio por buena. Instrucciones
completas en [tests/README.md](tests/README.md).

**Lo que hace que sirva es el fallo**, no el acierto. No dice «los dos archivos son distintos»:
recorre los dos árboles a la vez y nombra cada sitio donde dejan de coincidir, con la ruta por la
que se busca. Probado a propósito manipulando la referencia:

```
parserVersion: 8 -> 7
players[3].kills: 99 -> null
openings[12].category: "municion" -> ya no está
openings[40].y: no estaba -> 547
eliminations: 138 elementos -> 139
```

La referencia de hoy es una partida de 567 s con 81 aperturas de 10 clases distintas, 80
jugadores y 139 bajas: 10 MB de repetición, 65 KB de lectura, 1 segundo de test. Se eligió por
variedad, no por tamaño; la primera que se probó duró 46 segundos y no tenía ningún cofre, que
como referencia de un contador de cofres no vale de nada.

**Las repeticiones no están en el repositorio y no pueden estarlo:** cada una lleva dentro el id
de Epic y el nick de las ochenta y tantas personas que jugaron esa partida, y su lectura también.
`tests/fixtures/.gitignore` deja fuera `*.replay` y `*.expected.json`. Por eso, cuando no hay
ninguna, el test **no falla**: lo dice por escrito en su salida y sigue. En CI se ejecuta igual,
que es lo que detecta que se rompa el test mismo.

Cuando un cambio del lector es a propósito: repasar la lista de diferencias y volver a lanzarlo
con `FCC_FIXTURE_UPDATE=1` para apuntar la lectura nueva.

---

## Arranque: que nunca se cierre en silencio — *hecho*

**.NET no hace falta instalarlo, y comprobado sobre el binario publicado** (14/11/2026): el
`.exe` de la release contiene `coreclr`, `hostpolicy`, `System.Private.CoreLib` y
`Microsoft.AspNetCore`, y **no** contiene la cadena `You must install .NET` que llevaría si
dependiera de un .NET externo. La diferencia de tamaño lo confirma: 0,6 MB el `.dll` de solo
código frente a 58,7 MB el `.exe` publicado.

**Un aviso nuestro de «falta .NET» no puede existir**, y conviene no volver a intentarlo: si el
runtime faltara, nuestro código en C# nunca llegaría a ejecutarse para avisar. Lo único que puede
hablar en ese caso es el arrancador nativo, que es de Microsoft. Por eso la protección se puso
donde sí sirve: un `Target` en `ChestTracker.csproj` **falla la publicación** si alguien publica
en modo single-file sin `--self-contained`. Probado en los tres casos: publicación mala rechazada,
publicación buena a 58,7 MB, y el build de desarrollo intacto.

Lo que sí era un agujero real: al ser `WinExe` **no hay consola**, así que cualquier excepción al
arrancar (puerto tomado, permisos, antivirus, datos corruptos) cerraba el programa **sin ventana
ni mensaje**. Desde fuera es idéntico a hacer doble clic y que no pase nada. Ahora `objApp.Run()`
va dentro de un `try` que lo apunta en el registro y enseña un `MessageBox` en los tres idiomas
con el error y la ruta del log (`Native.ShowStartupFailure`).

## Ajustes: qué va dentro — *decidido el 15/11/2026*

Estaba lleno de cosas nuestras, no del usuario. Queda: **Repeticiones** (con la carpeta dentro),
Escuadrones (solo un enlace a su pestaña), Registro de Fortnite, Tu ID de Epic, Actualizaciones,
Copia de seguridad y Borrar datos.

Fuera, y por qué: **«Cómo está ahora»** (las mismas cifras ya están en la cabecera de la página),
**«Qué cuenta como cada cofre»** (clasificación interna; `ClassOverrides` sigue funcionando si
algún día hace falta tocarlo) y **«Dónde se guardan los datos»** (irrelevante a nivel usuario).

## Dos cosas de maquetación que conviene no volver a romper

- **El cajón de ajustes bloquea el scroll de la página** (`setDrawerOpen`, clase `body.drawer-open`).
  Antes salían dos barras a la vez y la rueda movía la que estuviera bajo el puntero. Quitar la
  barra ensancha la página 15 px, así que `setDrawerOpen` mide su ancho **antes** de bloquear y
  compensa con `padding-right` en el body: salto medido, 0 px. Abrir y cerrar pasa siempre por ahí;
  no quedan llamadas sueltas a `classList.add("open")`.
- **En vivo: la tarjeta del squad y la de cofres minuto a minuto van a media rejilla**
  (`.l-card.half`, 6 de 12), una al lado de la otra. Apiladas obligaban a scrollear sin motivo,
  habiendo sitio de sobra. Por debajo de 1100 px vuelven a ancho completo.

## Actualizarse no puede depender de borrar la copia anterior — *diagnosticado el 15/11/2026*

Una actualización falló con `Access to the path '...FortniteChestCounter.exe.old' is denied`.
La cadena de hechos, reconstruida de las fechas de los archivos y de los procesos vivos:

1. A las **01:28:10** la copia v1.3.0 en marcha lanzó un lector (`--dump`), que cargó la v1.3.0
   y se quedó en el bucle infinito del parser (arreglado en 1.4.0, pero esa copia era anterior).
2. A las **01:28:26** terminó la actualización a 1.4.0: el `.exe` se renombró a `.exe.old`.
   El lector colgado se fue con él, y pasó a tener abierto el `.exe.old`.
3. Su padre se reinició en esos 16 segundos, así que **nadie llegó a matarlo**: el tiempo límite
   del lector lo aplica el padre, y ese padre ya no existía. Siguió vivo más de una hora.
4. La siguiente actualización intentó `File.Delete(.old)`, no pudo, y se abortó entera.

Dos arreglos, cada uno suficiente por su cuenta:

- **`Updater`**: si el `.old` no se puede borrar, la copia actual se aparta con un nombre único
  (`.old.<fecha>`) en vez de abortar. La limpieza del arranque barre todos los `.old*`. Un
  archivo que nadie puede borrar no es motivo para rechazar una actualización.
- **`ParseProcess` + `Native.TieReaderToThisProgram`**: los lectores entran en un *job object* de
  Windows con `KILL_ON_JOB_CLOSE`, así que **mueren con el programa pase lo que pase** — cierre
  normal, reinicio por actualización o cuelgue. No puede volver a quedar un huérfano. Verificado
  con un arnés que ata un hijo de larga duración y mata al padre con `FailFast`: el hijo muere.

## Lección: mover marcado fuera del cajón rompe sus estilos

Al sacar Escuadrón a su propia pestaña, el marcado dejó de estar dentro de `.drawer` y **todas
sus reglas CSS dejaron de aplicar**: los compañeros salían como texto corrido
(`HttpDefitú162 partidas15/11/2026`). Las 26 reglas compartidas se escriben ahora
`.drawer X, .squad X`. Si algún día se mueve a otro sitio, hay que repetir la operación.

## Cuándo llegan los cofres del squad — *revisado el 15/11/2026*

La queja fue no llegar a ver los cofres de los compañeros antes de empezar la partida siguiente.
La cadena es: tu partida se guarda → la publicas → **OneDrive sube y baja** → su programa lee.
De esos cuatro pasos solo controlamos el primero y el último, y el del medio es el lento.

Lo que se ha apretado:

- **Publicar: de 5 s a 1 s.** Cuando no hay nada nuevo el tick cuesta una comparación de enteros,
  así que esperar no ahorraba nada; lo que compra es publicar en el primer segundo.
- **Leer: 2 s durante los 4 minutos siguientes a terminar una partida, 8 s el resto del tiempo.**
  Ese es el único momento en que se esperan sus cifras y el único en que están llegando.
- **La comparación ya no desaparece al empezar la partida siguiente.** El hueco entre partidas de
  este jugador es de unos 10 s de mediana, bastante menos de lo que tarda la nube, así que la
  tarjeta de la partida anterior se queda en *En vivo* después de terminarla. Antes se esfumaba
  justo antes de que llegaran los datos, que es el peor momento posible.
- **Y ya no se queda toda la partida** *(16/11/2026)*. Quince minutos fijos eran casi toda la
  partida siguiente mirando la anterior, y jugando con desconocidos eran quince minutos de un
  giro esperando fichas que nadie iba a publicar. Ahora el hueco lo decide **quién juega
  contigo**, no el reloj:
  - Alguien de tu carpeta compartida está en la partida anterior y su ficha sigue llegando → se
    queda la comparación.
  - Han llegado todas → se queda 90 s más, para que dé tiempo a leerla, y devuelve el hueco.
  - No juegas con ellos → **los compañeros de esta partida, en directo**, con el estimado que tu
    propia repetición va viendo (`≥4`). Nunca hubo nada en camino, así que no hay nada que
    esperar.

  Los 15 minutos siguen como tope absoluto. El relevo entra en la clave de etapa (`getStageKey`),
  así que ocurre al segundo y no cuando Fortnite escribe la repetición 20 s después. Para
  distinguir a un compañero del squad de uno que metió el emparejamiento hizo falta que la
  partida en curso pasara también por `ResolveSquad` en `/api/state`, con los totales apagados
  para no contar dos veces los cofres cuando esa partida se guarde.

- **La tarjeta «Por qué no hay contador en directo» está eliminada**, no apartada: ese hueco es
  ahora la comparación del squad de la partida anterior. Explicar por qué no hay contador importa
  las primeras veces y nunca más. Si no hay partida anterior reciente (más de 15 minutos) o no
  estás en ningún escuadrón, el hueco queda vacío y la rejilla se recoloca.
  **La tarjeta no decide su ancho**: lo recibe de quien la coloca, que es el único que conoce su
  fila. En la vista de partida terminada va a 6 de 12 (junto al gráfico); en la de partida en
  curso, a 4 (junto a «Tu ritmo habitual», que ocupa 8). Ponerla a 6 en esa fila sumaba 14 y
  descuadraba la rejilla entera.

**Cómo se renderiza el estado «partida en curso» para probarlo.** `getLiveContext` es un `const`
de ámbito de módulo, así que asignar `window.getLiveContext` **no lo sustituye** y la página sigue
usando el real. En la copia instrumentada del scratchpad hay que convertirlo primero
(`const getLiveContext = ` → `window.getLiveContext = `) y entonces ya se puede reemplazar. Sin
ese paso, las capturas salen del contexto real y aparentan funcionar.
- **El resumen anterior aguanta 60 s** en vez de 45 (`INT_HOLD_SECONDS`).

No se puede prometer que estén antes de la siguiente partida: eso lo decide OneDrive.

## Quién es alguien, y cómo se llama hoy — *15/11/2026*

Una compañera se cambió el nick (CHEESCA_97 → ParceNoDispare) y apareció como **dos personas**
en el historial. La identidad siempre fue el id de Epic (los archivos del escuadrón y las fichas
van por id), pero el **nombre mostrado** salía de la repetición de cada partida, que guarda el
nick con el que se jugó aquella.

Ahora `/api/state` arma un mapa `id -> nombre actual` y lo aplica a todas las partidas: gana lo
que publica ahora mismo en la carpeta compartida, y si no comparte, el nick de la partida más
reciente en que se le vio. Verificado con los datos reales: las 168 partidas, incluidas las del
8 de noviembre, muestran un único nombre.

Si se vuelve a tocar algo de nombres: **nunca agrupar por nick**, siempre por id.

## Las barras del squad tienen que medir lo mismo

La corona colgada al final de la fila robaba ancho, así que **el que más cofres había abierto
dibujaba la barra más corta**. Moverla a la izquierda no bastó: las etiquetas «TÚ» y «ESTIMADO»
hacían lo mismo, cada fila por su cuenta (465, 387, 465, 426 px medidos).

Las filas son ahora una rejilla de columnas fijas — corona (18px) · nombre y etiquetas (180px) ·
barra (el resto) · cifra — y la corona tiene su hueco reservado en **todas** las filas, la ocupe
o no. Medido después: las cuatro barras a 381 px, empezando todas en el mismo píxel.

## El asterisco de las partidas viejas — *16/11/2026*

«84 partidas conservan el recuento antiguo (marcadas con \*): su repetición ya no está» ocupaba
una línea permanente de la barra de estado para contar algo que **no tiene arreglo posible**: esas
repeticiones no están y no van a volver. Una línea de prosa siempre a la vista sobre algo que
nadie puede hacer es una línea que se deja de leer, y de paso tapaba a las que sí piden algo.

Ahora lo cuenta el propio asterisco: pasar el ratón lo dice en una línea, y pulsarlo lo dice
entero, con cuántas partidas están igual. El asterisco pasó de ser un carácter dentro de la
celda a un botón en dorado y en voladita, porque una marca que no parece pulsable no se pulsa.

## Pendientes menores y sospechas

- **`bTossedFromContainer` no significa «vino de un cofre».** El botín del suelo de la isla previa
  viene marcado con esa bandera por el propio Fortnite (104 casos en una partida, todos en el
  segundo 3, sin ningún cofre cerca). La atribución de «roto de rebote» da por hecho que sí.
  Hoy funciona, pero conviene revisarlo cuando haya tests.
- **«Tu ID de Epic» en Ajustes.** Es el respaldo de `Summarizer.GetOwnerId` cuando la repetición
  no dice quién grabó. En 126 partidas no ha hecho falta **ni una vez**. Propuesta: dejarlo
  visible como dato, quitar la caja de edición.
- **Enlazar con el visor de repeticiones** desde la lista de cofres: descartado a propósito. Esa
  lista existe para contrastar que el programa cuenta bien, no para quedarse.
- **Qué ha salido nuevo en Fortnite, de un vistazo.** `GET /v2/cosmetics/new` da los cosméticos
  que añadió el último parche, repartidos por familia (`br`, `cars`, `tracks`, `instruments`,
  `lego`) y con la versión del build dentro de `++Fortnite+Release-43.10-CL-…`. **No sirve para
  los tesoros de la semana** —son objetos exclusivos que no pasan por la tienda—, pero sí para
  una consulta rápida de novedades, que es información que hoy el programa no da. Sitio natural:
  un apartado propio, no la pestaña Tesoros. Se probó y funciona: el parche 43.10 trae 41 objetos
  de battle royale, 18 de coches y 7 pistas.
- **Los Gizmos están ocultos, no borrados.** La casilla sigue en los datos con `bHidden`, y lo que
  llevas contado (18 el 14/11/2026) no se ha tocado. Se escondió porque no interesa a nadie;
  queda pendiente darle otro uso a ese hueco.
