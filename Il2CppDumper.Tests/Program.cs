using Il2CppDumper;
using Mono.Cecil;

static class Tests
{
    static int checks;
    public static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    static void Main(string[] args)
    {
        IndexWidths();
        foreach (var version in new[] { 29, 31, 39 }) SyntheticRoundTrip(version);
        FailureDiagnostics();
        if (args.Length != 0 && args.Length != 2) throw new ArgumentException("Usage: [global-metadata.dat DummyDll-directory]");
        if (args.Length > 0) GameOutput(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
        Console.WriteLine($"PASS: {checks} checks.");
    }

    static void IndexWidths()
    {
        foreach (var width in new[] { 1, 2, 4 })
        {
            using var bytes = new MemoryStream();
            using (var w = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            {
                if (width == 1) { w.Write((byte)42); w.Write(byte.MaxValue); }
                if (width == 2) { w.Write((ushort)300); w.Write(ushort.MaxValue); }
                if (width == 4) { w.Write(70000); w.Write(-1); }
            }
            bytes.Position = 0;
            using var reader = new IndexReader(bytes);
            reader.Width(width);
            var value = reader.ReadClass<IndexRecord>();
            Check(value.Value == (width == 1 ? 42 : width == 2 ? 300 : 70000), $"{width}-byte index");
            Check(value.Missing == -1 && reader.Position == (ulong)(width * 2), $"{width}-byte sentinel/stride");
        }
        using var a = new IndexReader(new MemoryStream(new byte[] { 7 }));
        using var b = new IndexReader(new MemoryStream(new byte[] { 8, 0 }));
        a.Width(1); b.Width(2);
        Check(a.ReadIndex() == 7 && b.ReadIndex() == 8, "Widths must be per reader");
        try { a.Width(3); throw new Exception("Accepted invalid width"); }
        catch (InvalidDataException) { checks++; }
        Check(Il2CppCodeRegistration.CodeGenModulesOffset(39, 8) == 128, "v39 registration module offset");
        Check(Il2CppCodeRegistration.CodeGenModulesOffset(39, 4) == 64, "32-bit registration module offset");
    }

    static void SyntheticRoundTrip(int version)
    {
        using var metadata = new Metadata(Fixture.Build(version));
        using var native = new FixtureNative(version);
        var executor = new Il2CppExecutor(metadata, native);
        Check(metadata.typeDefs[1].parentIndex == 0 && metadata.typeDefs[1].elementTypeIndex == 3, $"v{version} enum normalization");
        Check(metadata.GetStringLiteralFromIndex(0) == "hello" && metadata.stringLiterals.Length == 1, $"v{version} string sentinel");
        var generator = new DummyAssemblyGenerator(executor, false);
        Check(generator.AttributeRestorationErrors == 0, $"v{version} attribute restoration");
        var assembly = generator.Assemblies.Single(a => a.Name.Name == "Fixture");
        using var bytes = new MemoryStream();
        assembly.Write(bytes);
        bytes.Position = 0;
        using var resolver = new MyAssemblyResolver();
        var attributesAssembly = generator.Assemblies.Single(a => a.Name.Name == "Attributes");
        using var attributeBytes = new MemoryStream();
        attributesAssembly.Write(attributeBytes); attributeBytes.Position = 0;
        using var attributeRoundTrip = AssemblyDefinition.ReadAssembly(attributeBytes, new ReaderParameters { AssemblyResolver = resolver });
        resolver.Register(attributeRoundTrip);
        using var roundTrip = AssemblyDefinition.ReadAssembly(bytes, new ReaderParameters { AssemblyResolver = resolver });
        resolver.Register(roundTrip);
        var signed = roundTrip.MainModule.Types.Single(t => t.Name == "SignedEnum");
        var unsigned = attributeRoundTrip.MainModule.Types.Single(t => t.Name == "UnsignedEnum");
        Check(signed.IsEnum && unsigned.IsEnum, $"v{version} enum base types");
        Check((int)signed.Fields.Single(f => f.Name == "MinusOne").Constant == -1, $"v{version} signed constant");
        Check((uint)unsigned.Fields.Single(f => f.Name == "HighBit").Constant == 0x80000000u, $"v{version} unsigned constant");
        Check(signed.CustomAttributes.Count == 2, $"v{version} multiple attributes");
        Check((int)signed.CustomAttributes[0].ConstructorArguments[0].Value == -1, $"v{version} signed enum attribute argument");
        Check((uint)signed.CustomAttributes[1].ConstructorArguments[0].Value == 0x80000000u, $"v{version} unsigned enum attribute argument");
        Check((int)signed.CustomAttributes[0].Fields.Single().Argument.Value == -1, $"v{version} named field");
        Check((uint)signed.CustomAttributes[0].Properties.Single().Argument.Value == 0x80000000u, $"v{version} named property");
        var host = attributeRoundTrip.MainModule.Types.Single(t => t.Name == "GenericHost`1");
        var method = host.Methods.Single();
        Check(method.GenericParameters.Count == 1 && host.GenericParameters.Count == 1, $"v{version} generic parameter counts");
        Check(((GenericParameter)method.Parameters[0].ParameterType).Type == GenericParameterType.Type, $"v{version} type generic owner");
        Check(((GenericParameter)method.Parameters[1].ParameterType).Type == GenericParameterType.Method, $"v{version} method generic owner");
        Check(((GenericParameter)method.ReturnType).Type == GenericParameterType.Method, $"v{version} generic return owner");
        foreach (var generated in generator.Assemblies) generated.Dispose();
        if (version == 39)
        {
            var malformed = Fixture.Build(version).ToArray();
            // Field section size no longer agrees with its count/record size.
            BitConverter.GetBytes(1).CopyTo(malformed, 8 + 11 * 12 + 4);
            try { using var bad = new Metadata(new MemoryStream(malformed)); throw new Exception("Accepted corrupt section"); }
            catch (InvalidDataException) { checks++; }
        }
    }

    static void FailureDiagnostics()
    {
        var bytes = Fixture.Build(39).ToArray();
        int blobOffset = BitConverter.ToInt32(bytes, 8 + 24 * 12);
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, blobOffset + 1);
        using var metadata = new Metadata(new MemoryStream(bytes));
        using var native = new FixtureNative(39);
        var executor = new Il2CppExecutor(metadata, native);
        var output = Path.Combine(Path.GetTempPath(), "Il2CppDumper-test-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        var oldOut = Console.Out; var oldError = Console.Error;
        using var messages = new StringWriter();
        using var diagnostics = new StringWriter();
        try
        {
            Console.SetOut(messages); Console.SetError(diagnostics);
            try { DummyAssemblyExporter.Export(executor, output, false); throw new Exception("Incomplete export reported success"); }
            catch (AggregateException) { checks++; }
            Check(messages.ToString().Contains("exported 3/3 assemblies; attribute restoration errors: 1"), "Incomplete export summary");
            foreach (var text in new[] { "assembly=Fixture.dll", "member=Example.SignedEnum", "token=0x02000002", "blobOffset=0x", "IndexOutOfRangeException" })
                Check(diagnostics.ToString().Contains(text), "Diagnostic context: " + text);
            Check(Directory.GetFiles(Path.Combine(output, "DummyDll")).Length == 3, "Remaining assemblies survive attribute failure");
        }
        finally
        {
            Console.SetOut(oldOut); Console.SetError(oldError);
            Directory.Delete(output, true);
        }
    }

    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes)) yield return nested;
        }
    }

    static void GameOutput(string metadataPath, string directory)
    {
        using var metadata = new Metadata(new MemoryStream(File.ReadAllBytes(metadataPath)));
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(directory);
        var expected = metadata.imageDefs.Select(i => metadata.GetStringFromIndex(i.nameIndex)).Append("Il2CppDummyDll.dll").ToHashSet();
        var actual = Directory.GetFiles(directory).Select(Path.GetFileName).ToHashSet();
        Check(expected.SetEquals(actual), "Every image must have an exported assembly");
        int attributes = 0, enums = 0;
        bool foundBuffer = false, foundFlags = false;
        Il2CppImageDefinition image = null;
        foreach (var file in Directory.GetFiles(directory))
        {
            using var assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver });
            image = metadata.imageDefs.SingleOrDefault(i => metadata.GetStringFromIndex(i.nameIndex) == assembly.MainModule.Name);
            foreach (var type in Types(assembly.MainModule.Types))
            {
                if (type.IsEnum)
                {
                    enums++;
                    Check(type.Fields.Where(f => f.IsLiteral).All(f => f.HasConstant && f.Constant != null), $"Enum constants: {type.FullName}");
                }
                ReadAttributes(type);
                foreach (var f in type.Fields) ReadAttributes(f);
                foreach (var p in type.Properties) ReadAttributes(p);
                foreach (var e in type.Events) ReadAttributes(e);
                foreach (var m in type.Methods)
                {
                    ReadAttributes(m);
                    foreach (var p in m.Parameters) ReadAttributes(p);
                }
                if (type.FullName == "BCGDataBuffer")
                {
                    foundBuffer = true;
                    Check(type.CustomAttributes.Any(a => a.AttributeType.FullName == "ProtoBuf.ProtoContractAttribute"), "BCGDataBuffer ProtoContract");
                    Check(type.Interfaces.Any(i => i.InterfaceType.FullName == "ProtoBuf.IExtensible"), "BCGDataBuffer IExtensible");
                    var attr = type.Properties.Single(p => p.Name == "stat_modifiers").CustomAttributes.Single(a => a.AttributeType.FullName == "ProtoBuf.ProtoMemberAttribute");
                    Check((int)attr.ConstructorArguments.Single().Value == 1, "stat_modifiers ProtoMember(1)");
                    Console.WriteLine("BCGDataBuffer: [ProtoContract], stat_modifiers [ProtoMember(1)], IExtensible");
                }
                if (type.FullName == "UltimatePlayerController/ActionFlags")
                {
                    foundFlags = true;
                    Check(type.IsEnum && type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute"), "ActionFlags enum/Flags");
                    foreach (var (name, value) in new[] { ("None", 0), ("InfiniteRange", 1), ("ForceOpponentRangeCheck", 2) })
                        Check((int)type.Fields.Single(f => f.Name == name).Constant == value, $"ActionFlags.{name}");
                    Console.WriteLine("ActionFlags: [Flags] enum, None=0, InfiniteRange=1, ForceOpponentRangeCheck=2");
                }
            }
        }
        Check(foundBuffer && foundFlags, "Both requested game types must be present");
        Check(enums == metadata.typeDefs.Count(t => t.IsEnum), "Every metadata enum must be exported as an enum");
        Console.WriteLine($"Reopened {actual.Count} DLLs, {enums} enums, {attributes} attributes.");
        void ReadAttributes(ICustomAttributeProvider provider)
        {
            if (image != null)
            {
                var tokenAttribute = provider.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "Il2CppDummyDll.TokenAttribute");
                if (tokenAttribute != null)
                {
                    var text = (string)tokenAttribute.Fields.Single(f => f.Name == "Token").Argument.Value;
                    var token = Convert.ToUInt32(text, 16);
                    var index = metadata.GetCustomAttributeIndex(image, 0, token);
                    uint count = 0;
                    if (index >= 0)
                    {
                        metadata.Position = metadata.header.attributeDataOffset + metadata.attributeDataRanges[index].startOffset;
                        count = metadata.ReadCompressedUInt32();
                    }
                    Check(provider.CustomAttributes.Count(a => a.AttributeType.Namespace != "Il2CppDummyDll") == count,
                        $"Attribute count must match source: {image.nameIndex}, token={text}");
                }
            }
            foreach (var a in provider.CustomAttributes)
            {
                // Access forces Cecil to decode the on-disk blob and resolve enum types.
                _ = a.ConstructorArguments.Count; _ = a.Fields.Count; _ = a.Properties.Count;
                Check(a.IsResolved, $"Attribute must decode: {a.AttributeType.FullName}");
                attributes++;
            }
        }
    }
}

class IndexReader : BinaryStream
{
    public IndexReader(Stream s) : base(s) { }
    public void Width(int n) => SetVariableIndexWidth(VariableIndexKind.Type, n);
    public int ReadIndex() => ReadVariableIndex(VariableIndexKind.Type);
}
class IndexRecord
{
    [VariableIndex(VariableIndexKind.Type)] public int Value = 0;
    [VariableIndex(VariableIndexKind.Type)] public int Missing = 0;
}

class FixtureNative : Il2Cpp
{
    public FixtureNative(int version) : base(new MemoryStream())
    {
        Version = version;
        types = new[] {
            T(Il2CppTypeEnum.IL2CPP_TYPE_CLASS, 0),
            T(Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, 1, 0x8056),
            T(Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, 2, 0x8056),
            T(Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, 0x606),
            T(Il2CppTypeEnum.IL2CPP_TYPE_U4, 0, 0x606),
            T(Il2CppTypeEnum.IL2CPP_TYPE_CLASS, 3),
            T(Il2CppTypeEnum.IL2CPP_TYPE_CLASS, 4),
            T(Il2CppTypeEnum.IL2CPP_TYPE_VOID),
            T(Il2CppTypeEnum.IL2CPP_TYPE_VAR, 0),
            T(Il2CppTypeEnum.IL2CPP_TYPE_MVAR, 1),
            T(Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, 1, 6)
        };
        codeGenModuleMethodPointers = new() { ["Fixture.dll"] = new ulong[5], ["Attributes.dll"] = new ulong[5] };
    }
    static Il2CppType T(Il2CppTypeEnum type, ulong data = 0, uint attrs = 0) => new() { type = type, data = new Il2CppType.Union { dummy = data }, attrs = attrs };
    public override ulong MapVATR(ulong addr) => addr;
    public override ulong MapRTVA(ulong addr) => addr;
    public override bool Search() => false;
    public override bool PlusSearch(int a, int b, int c) => false;
    public override bool SymbolSearch() => false;
    public override bool CheckDump() => false;
    public override SectionHelper GetSectionHelper(int a, int b, int c) => throw new NotSupportedException();
}
