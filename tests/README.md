# Tests

Un solo test, y es el que hacía falta: **leer una repetición de verdad y comprobar que sigue
dando lo mismo que cuando se apuntó**.

```
dotnet test src/ChestTracker.Tests/ChestTracker.Tests.csproj
```

## Por qué así

El lector de repeticiones es la pieza crítica y la única que no se puede revisar a ojo: saca unos
miles de datos de un formato binario sin documentar. Al tocarlo, la pregunta nunca es «¿este
número es 24?», sino **«¿ha cambiado algo que yo no quería cambiar?»**. Eso lo contesta un archivo
de referencia y cuesta una repetición.

Cuando algo cambia, el fallo dice **dónde**, no «los dos archivos son distintos»:

```
openings[412].category: cofre -> municion
players[7].kills: 3 -> no estaba
eliminations: 94 elementos -> 93
```

## Poner una repetición de prueba

1. Copia un `.replay` a `tests/fixtures/`. Vale cualquiera de
   `%LOCALAPPDATA%\FortniteGame\Saved\Demos`. **Mejor uno pequeño**: el test lo lee entero cada
   vez, y los hay de 6 MB y de 17 MB. Una partida en la que pasaran cosas variadas (cofres
   normales, especiales, de espíritu, alguna eliminación) vale más que una larga.
2. Lanza los tests. La primera vez no hay nada con que comparar, así que **apunta la lectura de
   hoy** en `<nombre>.expected.json` y pasa diciéndolo.
3. A partir de ahí, cualquier cambio en el lector sale en el fallo.

Se pueden poner varias: se leen todas.

## Cuando el cambio es a propósito

Repasa la lista de diferencias —ese es el momento de mirarla, no después— y apunta la lectura
nueva:

```
# PowerShell
$env:FCC_FIXTURE_UPDATE = "1"; dotnet test src/ChestTracker.Tests/ChestTracker.Tests.csproj; $env:FCC_FIXTURE_UPDATE = ""
```

`ParserVersion` sube cuando el lector cambia de forma que obliga a releer, así que verlo en la
lista de diferencias es normal y es justo lo que se quería ver.

## Por qué los archivos no están en el repositorio

Una repetición lleva dentro el **id de Epic y el nick de las ochenta y tantas personas** que
jugaron esa partida, y su lectura también. Ninguna de ellas ha dado permiso para salir en un
repositorio público. Por eso `tests/fixtures/.gitignore` deja fuera `*.replay` y
`*.expected.json`, y por eso el test **no falla** cuando no hay ninguna: en cualquier PC que no
sea el tuyo eso sería una compilación en rojo por un archivo que no debe estar. Lo dice por
escrito en la salida del test y sigue.
