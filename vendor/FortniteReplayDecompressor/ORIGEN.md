# FortniteReplayDecompressor (copia incluida)

Código de https://github.com/Shiqan/FortniteReplayDecompressor (licencia MIT, ver LICENSE),
commit 76f74c1f640069afdba71b6468e2f819327893c2 (rama master, septiembre de 2026).

Solo se incluyen las cuatro bibliotecas que hacen falta: Unreal.Core, Unreal.Encryption,
OozSharp y FortniteReplayReader.

Cambios respecto al original:

- Unreal.Core.csproj y FortniteReplayReader.csproj: la referencia al paquete NuGet
  Microsoft.Extensions.Logging.Abstractions se sustituye por
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, que ya lo trae.
  Así compilar no necesita descargar nada de NuGet.
- Directory.Build.props: se quitan los ajustes de los proyectos de test y se silencian avisos.

Para actualizar tras un parche de Fortnite: sustituir las cuatro carpetas de `src` por las
del repositorio y volver a aplicar el cambio de los dos .csproj.
