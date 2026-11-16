using System.Text.Json;
using Xunit.Abstractions;

namespace ChestTracker.Tests;

/// <summary>
/// The one test the parser needed. It is not a unit test and does not try to be: it reads a real
/// replay with the real reader and compares the result against the reading that was agreed to be
/// right, field by field.
///
/// Why this and not assertions about chest counts: the parser's output is a few thousand facts
/// pulled out of an undocumented binary format, and the question that matters when touching it is
/// never "is this number 24" but "did anything I did not mean to change, change". A golden file
/// answers exactly that and costs one replay.
///
/// The replays and their readings live outside the repository, in tests/fixtures, and git ignores
/// them. A replay carries the Epic id and the nickname of the eighty-odd people who were in that
/// match, and none of them agreed to appear in a public repository.
/// </summary>
public sealed class ParserFixtureTests
{
    /// <summary>Same options as --dump, so what is compared is what the program really reads.</summary>
    private static readonly JsonSerializerOptions objJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ITestOutputHelper _objOutput;

    public ParserFixtureTests(ITestOutputHelper objOutput) => _objOutput = objOutput;

    [Fact]
    public void Cada_repeticion_de_prueba_se_sigue_leyendo_igual()
    {
        var strFolder = FindFixtureFolder();
        var arrReplays = Directory.Exists(strFolder)
            ? Directory.GetFiles(strFolder, "*.replay").OrderBy(strPath => strPath, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

        if (arrReplays.Length == 0)
        {
            // Nothing to compare is not a pass to be proud of, so it says out loud what is
            // missing and how to put it there. The test cannot fail over it: on any PC but the
            // one with the replays, that would be a red build for a file that is not meant to
            // be in the repository.
            _objOutput.WriteLine($"No hay ninguna repetición en {strFolder}.");
            _objOutput.WriteLine("Copia ahí un .replay y vuelve a lanzar los tests: la primera vez se apunta cómo se lee");
            _objOutput.WriteLine("hoy, y a partir de entonces cualquier cambio del lector sale aquí. Ver tests/README.md.");
            return;
        }

        var bUpdate = Environment.GetEnvironmentVariable("FCC_FIXTURE_UPDATE") == "1";
        var lstBroken = new List<string>();

        foreach (var strReplay in arrReplays)
        {
            var strExpected = Path.ChangeExtension(strReplay, ".expected.json");
            var strName = Path.GetFileName(strReplay);

            string strActual;
            try
            {
                // The same call the program makes for every replay, large stack included: the
                // library reads some structures recursively and a 1 MB stack is not enough
                var objRecord = ParseProcess.ParseWithLargeStack(strReplay);
                strActual = JsonSerializer.Serialize(objRecord, objJson);
            }
            catch (Exception objError)
            {
                lstBroken.Add($"{strName}: ya no se puede leer. {objError.Message}");
                continue;
            }

            if (!File.Exists(strExpected) || bUpdate)
            {
                File.WriteAllText(strExpected, strActual);
                _objOutput.WriteLine($"{strName}: apuntada la lectura de hoy en {Path.GetFileName(strExpected)} ({strActual.Length / 1024} KB).");
                continue;
            }

            using var objWas = JsonDocument.Parse(File.ReadAllText(strExpected));
            using var objIs = JsonDocument.Parse(strActual);
            var lstDifferences = JsonDiff.Compare(objWas.RootElement, objIs.RootElement);

            if (lstDifferences.Count > 0)
            {
                lstBroken.Add($"{strName}: {lstDifferences.Count} diferencias con lo apuntado.\n{JsonDiff.Describe(lstDifferences)}");
            }
            else
            {
                _objOutput.WriteLine($"{strName}: igual que lo apuntado.");
            }
        }

        Assert.True(lstBroken.Count == 0,
            "El lector ya no da lo mismo que cuando se apuntó:\n\n"
            + string.Join("\n", lstBroken)
            + "\nSi el cambio es a propósito, repasa la lista de arriba y vuelve a lanzarlo con FCC_FIXTURE_UPDATE=1 para apuntar la lectura nueva.");
    }

    /// <summary>
    /// tests/fixtures, found by walking up from wherever the test runner put the binaries. The
    /// folder is next to the solution and not next to the .dll, which is four levels down inside
    /// bin, and that distance is the whole reason this is not a constant.
    /// </summary>
    private static string FindFixtureFolder()
    {
        var objFolder = new DirectoryInfo(AppContext.BaseDirectory);
        while (objFolder != null)
        {
            var strCandidate = Path.Combine(objFolder.FullName, "tests", "fixtures");
            if (Directory.Exists(strCandidate))
            {
                return strCandidate;
            }
            objFolder = objFolder.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "tests", "fixtures");
    }
}
