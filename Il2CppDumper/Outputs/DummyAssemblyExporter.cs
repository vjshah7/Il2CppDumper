using System;
using System.Collections.Generic;
using System.IO;

namespace Il2CppDumper
{
    public static class DummyAssemblyExporter
    {
        public static void Export(Il2CppExecutor il2CppExecutor, string outputDir, bool addToken)
        {
            var dllDirectory = Path.Combine(outputDir, "DummyDll");
            if (Directory.Exists(dllDirectory))
                Directory.Delete(dllDirectory, true);
            Directory.CreateDirectory(dllDirectory);
            var dummy = new DummyAssemblyGenerator(il2CppExecutor, addToken);
            var failures = new List<Exception>();
            var exported = 0;
            try
            {
                foreach (var assembly in dummy.Assemblies)
                {
                    try
                    {
                        using var stream = new MemoryStream();
                        assembly.Write(stream);
                        File.WriteAllBytes(Path.Combine(dllDirectory, assembly.MainModule.Name), stream.ToArray());
                        exported++;
                    }
                    catch (Exception exception)
                    {
                        var failure = new InvalidDataException($"Failed to export {assembly.MainModule.Name}.", exception);
                        failures.Add(failure);
                        Console.Error.WriteLine($"ERROR: {failure}");
                    }
                }
                Console.WriteLine($"DummyDll: exported {exported}/{dummy.Assemblies.Count} assemblies; attribute restoration errors: {dummy.AttributeRestorationErrors}.");
                if (dummy.AttributeRestorationErrors > 0)
                    failures.Add(new InvalidDataException($"Failed to restore attributes for {dummy.AttributeRestorationErrors} metadata entries."));
                if (failures.Count > 0)
                    throw new AggregateException("DummyDll export is incomplete. See the preceding diagnostics.", failures);
            }
            finally
            {
                foreach (var assembly in dummy.Assemblies) assembly.Dispose();
            }
        }
    }
}
