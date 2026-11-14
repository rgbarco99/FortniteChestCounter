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

## Estado de los seis puntos acordados

| # | Punto | Estado |
|---|---|---|
| 1 | Guardar dónde está cada cofre, y releer lo que quede en la carpeta | **Hecho** · 100 % de las aperturas propias posicionadas |
| 2 | Copia de seguridad (guardar, CSV y restaurar) | **Hecho** |
| 3 | Pestaña Mapa | **Primera versión hecha** · calor, zonas y reparto |
| 4 | Comparativa de mejora por días/semanas/meses | **Hecho** |
| 5 | `matches.json` partido por partida | Pendiente |
| 6 | Tests del parser | Pendiente |

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

**Cómo, por orden de dificultad:**

- **A — Archivo suelto (empezar por aquí).** Exportar las tarjetas, mandarlas por Discord e
  importarlas. Cero infraestructura: la fontanería de importar y exportar ya está hecha en el
  punto 2, solo hay que añadir el formato tarjeta y que al importar se fusione por `SessionId`
  en vez de ignorarse. Fricción: manual.
- **B — Carpeta compartida (el objetivo real).** Cada uno apunta el programa a una carpeta de
  OneDrive, Drive o Dropbox. Cada copia escribe `squad/<id-de-epic>.json` y lee las de los demás.
  Automático, sin servidor, sin cuentas, sin coste, y se configura una vez. Es la opción que da
  el 90 % del valor por el 10 % del trabajo.
- **C — Servidor propio.** Sincronización de verdad, pero trae hosting, cuentas, privacidad y
  mantenimiento permanente. Solo si esto llega a tener usuarios que no se conozcan entre ellos.

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

## 2. Tesoros

El corazón de la app. Hoy es la parte con más mantenimiento manual y la que menos engancha.

### 2.1 Imágenes y títulos automáticos

*Comprobado el 14/11/2026.* Hoy pegas una captura a mano. No hace falta: `fortnite-api.com` sirve
el icono oficial y el nombre ya traducido.

```
https://fortnite-api.com/v2/cosmetics/br/search
    ?language=es&searchLanguage=es&matchMethod=contains&name=<lo que escribas>
```

Devuelve nombre localizado, tipo, rareza y varias imágenes (`icon`, `smallIcon`, `featured`).
Comprobado: buscando `Banano` en español sale «Banano · Traje», y `pirata` por coincidencia
parcial encuentra «Pantera pirata». Sin `searchLanguage=es` el nombre en español no encuentra
nada, así que ese parámetro es obligatorio.

Cómo: al escribir el nombre del objeto de la semana, buscar y ofrecer los resultados con su
imagen; al elegir uno, guardarlo como hasta ahora. Dejar siempre la opción de pegar una captura,
porque un objeto recién salido puede tardar en aparecer en la API.

También hay `GET /v2/cosmetics/new`, que da los cosméticos añadidos en el último parche con sus
fechas (41 el día de la comprobación). Sirve para **proponer** los objetos de la semana en vez de
que los busques a ciegas.

### 2.2 Objetos de la semana y Horas del tesoro

Esto **no tiene API**: Epic lo publica en su blog y dentro del juego. El proceso se queda manual
y acordado: cuando salgan, avisas, se leen, se meten en los datos de la pestaña y salen en una
release. Conviene que el programa ayude recordando que toca: avisar cuando el cambio de semana
haya pasado y queden casillas sin revisar.

### 2.3 Lo que falta: que enganche

Hoy la pestaña informa. Debería **motivar**. Ideas, de más a menos clara:

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

Lo que falta: que enganche

Hoy la pestaña informa. Debería **motivar**. Ideas, de más a menos clara:

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
- **Qué es más óptimo.** Cuando esté el mapa (punto 3), cruzar zona con cofres por minuto
  responde «dónde caer para abrir más», que es la pregunta de quien farmea.

---

## 3. Pestaña Mapa — **primera versión hecha**

Ya está: mapa de calor de tus cofres sobre el mapa de la temporada, lista de zonas con cuántos
cofres abres en cada una, y el reparto por porcentaje. Se puede ver todo tu histórico o una sola
partida.

**La calibración, que era el riesgo, está medida.** La imagen cubre 1.350 m a cada lado del origen
del mundo, sin invertir la Y, lo que deja el píxel en 1,32 m:

```
izquierda = (x + 1350) / 2700        arriba = (y + 1350) / 2700
```

Comprobado superponiendo los sitios publicados sobre la imagen que lleva los nombres impresos:
Arlong Park, Rubber Resort y Loguetown caen sobre el sitio exacto; Plunder Port se va unos 80 m,
y es un barco, así que su coordenada publicada no está en su centro visual. Ojo: la primera
hipótesis (Y invertida, que es lo habitual en estos mapas) daba un resultado plausible pero
**mal**, y un ajuste automático por silueta de tierra también engaña, porque agrandar la escala
mete todos los puntos en la isla y parece que mejora.

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

## 6. Tests del parser

El parser es la pieza crítica y la única que no se puede verificar a ojo: hoy cualquier cambio se
hace a ciegas. Es lo que más frena a tocarlo, y el mapa y los tesoros van a obligar a tocarlo.

Qué hacer: guardar una repetición real como fixture, con el JSON que debe salir, y comparar la
salida de `--dump` contra él. Barato y desactiva el miedo. **Hacerlo antes del siguiente cambio
del parser, no después.**

---

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
