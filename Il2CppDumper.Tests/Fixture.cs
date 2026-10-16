using System.Text;

// Independent binary fixture writer: explicit on-disk layouts, not the parser's
// reflection/size helpers. Small v39 tables use 1-byte indexes; TypeIndex is 4.
static class Fixture
{
    public static MemoryStream Build(int version)
    {
        bool compact = version == 39;
        var sections = new (byte[] Bytes, int Count)[31];
        using var strings = new MemoryStream();
        var names = new Dictionary<string, uint>();
        uint S(string text)
        {
            if (names.TryGetValue(text, out var n)) return n;
            n = (uint)strings.Position; names.Add(text, n);
            strings.Write(Encoding.UTF8.GetBytes(text)); strings.WriteByte(0); return n;
        }
        void Section(int id, int count, Action<BinaryWriter> write)
        {
            using var data = new MemoryStream();
            using var writer = new BinaryWriter(data);
            write(writer); sections[id] = (data.ToArray(), count);
        }
        void Index(BinaryWriter w, int value)
        {
            if (compact) w.Write(value == -1 ? byte.MaxValue : checked((byte)value));
            else w.Write(value);
        }
        void Type(BinaryWriter w, string name, int byval, int underlying, int fields = -1,
            ushort fieldCount = 0, int methods = -1, ushort methodCount = 0,
            int properties = -1, ushort propertyCount = 0, int generic = -1, bool isEnum = false, int row = 0)
        {
            w.Write(S(name)); w.Write(S(name == "Enum" ? "System" : "Example"));
            w.Write(byval); w.Write(-1); // TypeIndex byval, declaring
            w.Write(isEnum ? (compact ? underlying : 0) : -1);
            if (!compact) w.Write(isEnum ? underlying : -1);
            Index(w, generic); w.Write((uint)(isEnum ? 0x101 : 1));
            foreach (var i in new[] { fields, methods, -1, properties, -1, -1, -1, -1 }) w.Write(i);
            foreach (var c in new ushort[] { methodCount, propertyCount, fieldCount, 0, 0, 0, 0, 0 }) w.Write(c);
            w.Write(isEnum ? 3u : 0u); w.Write(0x02000001u + (uint)row);
        }
        Section(19, 5, w => {
            Type(w, "Enum", 0, -1);
            Type(w, "SignedEnum", 1, 3, fields: 0, fieldCount: 2, isEnum: true, row: 1);
            Type(w, "UnsignedEnum", 2, 4, fields: 2, fieldCount: 2, isEnum: true, row: 2);
            Type(w, "FixtureAttribute", 5, -1, fields: 4, fieldCount: 1, methods: 0, methodCount: 4, properties: 0, propertyCount: 1, row: 3);
            Type(w, "GenericHost`1", 6, -1, methods: 4, methodCount: 1, generic: 0, row: 4);
        });
        Section(20, 2, w => {
            for (int i = 0; i < 2; i++)
            {
                w.Write(S(i == 0 ? "Fixture.dll" : "Attributes.dll")); w.Write(i);
                Index(w, i == 0 ? 0 : 2); w.Write(i == 0 ? 2u : 3u);
                Index(w, -1); w.Write(0u); w.Write(-1); w.Write(1u);
                w.Write(i == 0 ? 0 : 1); w.Write(i == 0 ? 1u : 0u);
            }
        });
        Section(21, 2, w => {
            for (int i = 0; i < 2; i++)
            {
                w.Write(i); w.Write(0x20000001u + (uint)i); if (compact) w.Write(1u);
                w.Write(0); w.Write(0);
                w.Write(S(i == 0 ? "Fixture" : "Attributes")); w.Write(S("")); w.Write(S(""));
                for (int j = 0; j < 3; j++) w.Write(0);
                w.Write(1); w.Write(0); w.Write(0); w.Write(0); w.Write(new byte[8]);
            }
        });
        Section(11, 5, w => {
            int row = 0;
            foreach (var (name, type) in new[] { ("value__", 3), ("MinusOne", 1), ("value__", 4), ("HighBit", 2), ("NamedField", 10) })
            { w.Write(S(name)); w.Write(type); w.Write(0x04000001u + (uint)row++); }
        });
        void Method(BinaryWriter w, string name, int owner, int returns, int start, ushort count, int row, int generic = -1)
        {
            w.Write(S(name)); Index(w, owner); w.Write(returns);
            if (version >= 31) w.Write(0);
            Index(w, start); Index(w, generic); w.Write(0x06000001u + (uint)row);
            w.Write((ushort)(name == ".ctor" ? 0x1886 : 0x86));
            w.Write((ushort)0); w.Write(ushort.MaxValue); w.Write(count);
        }
        Section(5, 5, w => {
            Method(w, ".ctor", 3, 7, 0, 1, 0);
            Method(w, ".ctor", 3, 7, 1, 1, 1);
            Method(w, "get_NamedProperty", 3, 2, -1, 0, 2);
            Method(w, "set_NamedProperty", 3, 7, 2, 1, 3);
            Method(w, "Convert", 4, 9, 3, 2, 4, generic: 1);
        });
        Section(10, 5, w => {
            int row = 0;
            foreach (var (name, type) in new[] { ("value", 1), ("value", 2), ("value", 2), ("t", 8), ("u", 9) })
            { w.Write(S(name)); w.Write(0x08000001u + (uint)row++); w.Write(type); }
        });
        Section(4, 1, w => { w.Write(S("NamedProperty")); w.Write(2); w.Write(3); w.Write(0u); w.Write(0x17000001u); });
        Section(14, 2, w => {
            w.Write(4); w.Write(1); w.Write(0); w.Write(0);
            w.Write(4); w.Write(1); w.Write(1); w.Write(1);
        });
        Section(12, 2, w => {
            for (int i = 0; i < 2; i++)
            { Index(w, i); w.Write(S(i == 0 ? "T" : "U")); w.Write((short)-1); w.Write((short)0); w.Write((ushort)0); w.Write((ushort)0); }
        });
        // Compressed -1 and unsigned 0x80000000 (5-byte encoding).
        Section(8, 6, w => { w.Write((byte)1); w.Write((byte)0xf0); w.Write(0x80000000u); });
        Section(7, 2, w => { w.Write(1); w.Write(3); w.Write(0); w.Write(3); w.Write(4); w.Write(1); });
        Section(24, 0, w => {
            w.Write((byte)2); w.Write(0); w.Write(1); // count + ctor indexes
            w.Write(new byte[] { 1, 1, 1, 0x55, 2, 1 }); // arg/field/property counts; SignedEnum(-1)
            w.Write(new byte[] { 0x55, 2, 1, 0 }); // NamedField = SignedEnum(-1), local index 0
            w.Write(new byte[] { 0x55, 4, 0xf0 }); w.Write(0x80000000u); w.Write((byte)0); // NamedProperty, index 0
            w.Write(new byte[] { 1, 0, 0, 0x55, 4, 0xf0 }); w.Write(0x80000000u);
        });
        sections[24].Count = sections[24].Bytes.Length;
        Section(25, 2, w => { w.Write(0x02000002u); w.Write(0u); w.Write(0u); w.Write((uint)sections[24].Bytes.Length); });
        Section(0, compact ? 2 : 1, w => { if (!compact) w.Write(5u); w.Write(0); if (compact) w.Write(5); });
        Section(1, 1, w => w.Write(Encoding.UTF8.GetBytes("hello")));
        sections[2] = (strings.ToArray(), names.Count);
        using var output = new MemoryStream();
        using var header = new BinaryWriter(output, Encoding.UTF8, true);
        header.Write(0xfab11bafu); header.Write(version);
        int offset = 8 + sections.Length * (compact ? 12 : 8);
        foreach (var (bytes, count) in sections)
        {
            header.Write(offset); header.Write(bytes?.Length ?? 0);
            if (compact) header.Write(count);
            offset += bytes?.Length ?? 0;
        }
        foreach (var (bytes, _) in sections) if (bytes != null) header.Write(bytes);
        return new MemoryStream(output.ToArray());
    }
}
