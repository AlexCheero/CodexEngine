using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CodexNetworking.Serialization.Generation
{
    public sealed class GeneratorOptions
    {
        /// <summary>Namespace of the generated classes.</summary>
        public string Namespace = "CodexNetworking.Generated";

        /// <summary>Appended to the component's name for its serializer class.</summary>
        public string ClassSuffix = "NetSerializer";

        /// <summary>Static class holding RegisterAll().</summary>
        public string RegistryClass = "GeneratedNetSerializers";
    }

    /// <summary>
    /// Emits C# source for an <see cref="INetSerializer{T}"/> per replicated struct, from reflection
    /// over the compiled types, applying the field attributes: one change bit per top-level field
    /// in the delta form, <c>[Quantize]</c> floats and Fix64 values as bounded integers,
    /// <c>[NetBits]</c> and <c>[NetRange]</c> integers at their width, bools as one bit, enums as
    /// their underlying integer, <c>Entity</c> fields through the entity map, nested structs field
    /// by field (an attribute on the outer field applies to inner fields without their own), and
    /// anything else as raw bytes. Types and attributes are matched by name so the generator can
    /// run against a game assembly loaded in another context (the standalone tool) as well as
    /// inside the editor. Runs at build time only; the output is plain allocation-free code.
    /// </summary>
    public static class NetSerializerGenerator
    {
        private const string ReplicatedAttributeName = "CodexNetworking.Serialization.ReplicatedAttribute";
        private const string QuantizeAttributeName = "CodexNetworking.Serialization.QuantizeAttribute";
        private const string NetBitsAttributeName = "CodexNetworking.Serialization.NetBitsAttribute";
        private const string NetRangeAttributeName = "CodexNetworking.Serialization.NetRangeAttribute";
        private const string NetEntityAttributeName = "CodexNetworking.Serialization.NetEntityAttribute";
        private const string Fix64Name = "CodexNetworking.FixedPoint.Fix64";
        private const string EntityName = "CodexECS.Entity";
        private const int Fix64FractionBits = 16;

        /// <summary>Per-field encoding settings gathered from attributes, inherited by nested fields.</summary>
        private struct Encoding
        {
            public bool HasQuantize;
            public float Min, Max, Precision;
            public int Bits;      // [NetBits], 0 when absent
            public bool HasRange;
            public int RangeMin, RangeMax;

            public Encoding Inherit(FieldInfo field)
            {
                Encoding result = this;
                foreach (CustomAttributeData attribute in field.CustomAttributes)
                {
                    string name = attribute.AttributeType.FullName;
                    if (name == QuantizeAttributeName)
                    {
                        result.HasQuantize = true;
                        result.Min = Convert.ToSingle(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                        result.Max = Convert.ToSingle(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
                        result.Precision = Convert.ToSingle(attribute.ConstructorArguments[2].Value, CultureInfo.InvariantCulture);
                    }
                    else if (name == NetBitsAttributeName)
                    {
                        result.Bits = Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                        result.HasRange = false;
                    }
                    else if (name == NetRangeAttributeName)
                    {
                        result.HasRange = true;
                        result.RangeMin = Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                        result.RangeMax = Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
                        result.Bits = 0;
                    }
                }
                return result;
            }
        }

        /// <summary>Structs carrying [Replicated] in the assembly, in a stable order.</summary>
        public static List<Type> FindReplicated(Assembly assembly)
        {
            var result = new List<Type>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (Type type in types)
            {
                if (type != null && type.IsValueType && !type.IsEnum && !type.IsGenericType && HasAttribute(type, ReplicatedAttributeName))
                    result.Add(type);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return result;
        }

        /// <summary>Generates one source file with a serializer per type and the registry class.</summary>
        public static string Generate(IReadOnlyList<Type> types, GeneratorOptions options = null)
        {
            options = options ?? new GeneratorOptions();
            var source = new StringBuilder();
            source.AppendLine("// <auto-generated>");
            source.AppendLine("//     Generated by CodexNetworking NetSerializerGenerator. Do not edit; regenerate instead.");
            source.AppendLine("// </auto-generated>");
            source.AppendLine("using System;");
            source.AppendLine("using System.Runtime.InteropServices;");
            source.AppendLine("using CodexNetworking.Serialization;");
            source.AppendLine();
            source.Append("namespace ").AppendLine(options.Namespace);
            source.AppendLine("{");
            foreach (Type type in types)
                EmitSerializer(source, type, options);
            source.Append("    public static class ").AppendLine(options.RegistryClass);
            source.AppendLine("    {");
            source.AppendLine("        /// <summary>Assigns every generated serializer to its component type. Call once at startup.</summary>");
            source.AppendLine("        public static void RegisterAll()");
            source.AppendLine("        {");
            foreach (Type type in types)
                source.Append("            NetSerializer<").Append(CSharpName(type)).Append(">.Instance = ").Append(ClassName(type, options)).AppendLine(".Instance;");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("}");
            return source.ToString();
        }

        private static void EmitSerializer(StringBuilder s, Type type, GeneratorOptions options)
        {
            if (!type.IsValueType || type.IsEnum)
                throw new InvalidOperationException(type.FullName + " is not a struct");
            string t = CSharpName(type);
            string cls = ClassName(type, options);
            List<FieldInfo> fields = Fields(type);
            s.Append("    public sealed class ").Append(cls).Append(" : INetSerializer<").Append(t).AppendLine(">");
            s.AppendLine("    {");
            s.Append("        public static readonly ").Append(cls).Append(" Instance = new ").Append(cls).AppendLine("();");
            s.AppendLine();

            // Full form.
            s.Append("        public void Write(ref BitWriter writer, in ").Append(t).AppendLine(" value, INetEntityMap entities)");
            s.AppendLine("        {");
            foreach (FieldInfo field in fields)
                EmitWrite(s, field, "value." + field.Name, default(Encoding).Inherit(field), 12);
            s.AppendLine("        }");
            s.AppendLine();
            s.Append("        public bool Read(ref BitReader reader, out ").Append(t).AppendLine(" value, INetEntityMap entities)");
            s.AppendLine("        {");
            EmitReadStruct(s, type, "value", default(Encoding), 12, true);
            s.AppendLine("            return true;");
            s.AppendLine("        }");
            s.AppendLine();

            // Delta form: one change bit per top-level field, changed fields in full.
            s.Append("        public void WriteDelta(ref BitWriter writer, in ").Append(t).Append(" baseline, in ").Append(t).AppendLine(" value, INetEntityMap entities)");
            s.AppendLine("        {");
            for (int i = 0; i < fields.Count; i++)
            {
                s.Append("            bool changed").Append(i).Append(" = ");
                EmitDiffers(s, fields[i].FieldType, "baseline." + fields[i].Name, "value." + fields[i].Name);
                s.AppendLine(";");
            }
            for (int i = 0; i < fields.Count; i++)
                s.Append("            writer.WriteBool(changed").Append(i).AppendLine(");");
            for (int i = 0; i < fields.Count; i++)
            {
                s.Append("            if (changed").Append(i).AppendLine(")");
                s.AppendLine("            {");
                EmitWrite(s, fields[i], "value." + fields[i].Name, default(Encoding).Inherit(fields[i]), 16);
                s.AppendLine("            }");
            }
            s.AppendLine("        }");
            s.AppendLine();
            s.Append("        public bool ReadDelta(ref BitReader reader, in ").Append(t).Append(" baseline, out ").Append(t).AppendLine(" value, INetEntityMap entities)");
            s.AppendLine("        {");
            s.AppendLine("            value = baseline;");
            for (int i = 0; i < fields.Count; i++)
                s.Append("            if (!reader.ReadBool(out bool changed").Append(i).AppendLine(")) return false;");
            for (int i = 0; i < fields.Count; i++)
            {
                s.Append("            if (changed").Append(i).AppendLine(")");
                s.AppendLine("            {");
                EmitReadField(s, fields[i], "value." + fields[i].Name, default(Encoding).Inherit(fields[i]), 16, "f" + i);
                s.AppendLine("            }");
            }
            s.AppendLine("            return true;");
            s.AppendLine("        }");
            s.AppendLine();
            s.AppendLine("        private static bool RawEquals<TRaw>(TRaw a, TRaw b) where TRaw : unmanaged");
            s.AppendLine("        {");
            s.AppendLine("            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref a, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref b, 1)));");
            s.AppendLine("        }");
            s.AppendLine("    }");
            s.AppendLine();
        }

        // ---- writing -------------------------------------------------------------------------------

        private static void EmitWrite(StringBuilder s, FieldInfo field, string expr, Encoding encoding, int indent)
        {
            Type type = field.FieldType;
            string pad = new string(' ', indent);
            switch (Kind(field, type))
            {
                case LeafKind.Bool:
                    s.Append(pad).Append("writer.WriteBool(").Append(expr).AppendLine(");");
                    return;
                case LeafKind.Integer:
                    EmitWriteInteger(s, type, expr, encoding, pad);
                    return;
                case LeafKind.Float:
                    if (encoding.HasQuantize)
                    {
                        int bits = Quantize.BitsForPrecision(encoding.Min, encoding.Max, encoding.Precision);
                        s.Append(pad).Append("writer.WriteBits(Quantize.FloatToBits(").Append(type == typeof(double) ? "(float)" : "").Append(expr)
                            .Append(", ").Append(F(encoding.Min)).Append(", ").Append(F(encoding.Max)).Append(", ").Append(bits).Append("), ").Append(bits).AppendLine(");");
                    }
                    else if (type == typeof(float))
                        s.Append(pad).Append("writer.WriteInt32(BitConverter.SingleToInt32Bits(").Append(expr).AppendLine("));");
                    else
                        s.Append(pad).Append("writer.WriteInt64(BitConverter.DoubleToInt64Bits(").Append(expr).AppendLine("));");
                    return;
                case LeafKind.Fix64:
                    if (encoding.HasQuantize)
                    {
                        FixedRange(encoding, out long minRaw, out long maxRaw, out int fraction, out int bits);
                        s.Append(pad).Append("writer.WriteBits(Quantize.FixedToBits(").Append(expr).Append(", global::CodexNetworking.FixedPoint.Fix64.FromRaw(").Append(minRaw)
                            .Append("L), global::CodexNetworking.FixedPoint.Fix64.FromRaw(").Append(maxRaw).Append("L), ").Append(fraction).Append("), ").Append(bits).AppendLine(");");
                    }
                    else
                        s.Append(pad).Append("writer.WriteInt64(").Append(expr).AppendLine(".Raw);");
                    return;
                case LeafKind.Entity:
                    s.Append(pad).Append("EntitySerialization.WriteEntity(ref writer, ").Append(expr).AppendLine(", entities);");
                    return;
                case LeafKind.Struct:
                    foreach (FieldInfo inner in Fields(type))
                        EmitWrite(s, inner, expr + "." + inner.Name, encoding.Inherit(inner), indent);
                    return;
                default:
                    s.Append(pad).Append("{ var raw = ").Append(expr).AppendLine("; writer.WriteBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref raw, 1))); }");
                    return;
            }
        }

        private static void EmitWriteInteger(StringBuilder s, Type type, string expr, Encoding encoding, string pad)
        {
            Type underlying = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
            bool signed = underlying == typeof(sbyte) || underlying == typeof(short) || underlying == typeof(int) || underlying == typeof(long);
            int natural = NaturalBits(underlying);
            if (encoding.HasRange)
            {
                s.Append(pad).Append("writer.WriteRange((int)").Append(expr).Append(", ").Append(encoding.RangeMin).Append(", ").Append(encoding.RangeMax).AppendLine(");");
                return;
            }
            int bits = encoding.Bits > 0 ? Math.Min(encoding.Bits, natural) : natural;
            if (bits > 32)
            {
                s.Append(pad).Append("writer.WriteUInt64((ulong)").Append(expr).AppendLine(");");
                return;
            }
            if (signed)
                s.Append(pad).Append("writer.WriteSignedBits((int)").Append(expr).Append(", ").Append(bits).AppendLine(");");
            else
                s.Append(pad).Append("writer.WriteBits((uint)").Append(expr).Append(", ").Append(bits).AppendLine(");");
        }

        // ---- reading -------------------------------------------------------------------------------

        // Reads every field of a struct into `target` (a variable or field path). Structs whose
        // fields are all assignable are filled in place; others are built through a constructor
        // whose parameters mirror the fields.
        private static void EmitReadStruct(StringBuilder s, Type type, string target, Encoding encoding, int indent, bool initialize)
        {
            string pad = new string(' ', indent);
            List<FieldInfo> fields = Fields(type);
            if (initialize)
                s.Append(pad).Append(target).AppendLine(" = default;");
            if (AllAssignable(fields))
            {
                for (int i = 0; i < fields.Count; i++)
                    EmitReadField(s, fields[i], target + "." + fields[i].Name, encoding.Inherit(fields[i]), indent, Local(target) + i);
                return;
            }
            ConstructorInfo constructor = MatchingConstructor(type, fields);
            if (constructor == null)
                throw new InvalidOperationException(type.FullName + " has read-only fields and no constructor taking them in order; make the fields assignable or add such a constructor");
            var names = new string[fields.Count];
            for (int i = 0; i < fields.Count; i++)
            {
                names[i] = Local(target) + i;
                s.Append(pad).Append(CSharpName(fields[i].FieldType)).Append(' ').Append(names[i]).AppendLine(" = default;");
                EmitReadField(s, fields[i], names[i], encoding.Inherit(fields[i]), indent, names[i] + "_");
            }
            s.Append(pad).Append(target).Append(" = new ").Append(CSharpName(type)).Append('(').Append(string.Join(", ", names)).AppendLine(");");
        }

        private static void EmitReadField(StringBuilder s, FieldInfo field, string target, Encoding encoding, int indent, string local)
        {
            Type type = field.FieldType;
            string pad = new string(' ', indent);
            switch (Kind(field, type))
            {
                case LeafKind.Bool:
                    s.Append(pad).Append("if (!reader.ReadBool(out bool ").Append(local).Append(")) return false; ").Append(target).Append(" = ").Append(local).AppendLine(";");
                    return;
                case LeafKind.Integer:
                    EmitReadInteger(s, type, target, encoding, pad, local);
                    return;
                case LeafKind.Float:
                    if (encoding.HasQuantize)
                    {
                        int bits = Quantize.BitsForPrecision(encoding.Min, encoding.Max, encoding.Precision);
                        s.Append(pad).Append("if (!reader.ReadBits(").Append(bits).Append(", out uint ").Append(local).Append(")) return false; ").Append(target)
                            .Append(" = Quantize.BitsToFloat(").Append(local).Append(", ").Append(F(encoding.Min)).Append(", ").Append(F(encoding.Max)).Append(", ").Append(bits).AppendLine(");");
                    }
                    else if (type == typeof(float))
                        s.Append(pad).Append("if (!reader.ReadInt32(out int ").Append(local).Append(")) return false; ").Append(target).Append(" = BitConverter.Int32BitsToSingle(").Append(local).AppendLine(");");
                    else
                        s.Append(pad).Append("if (!reader.ReadInt64(out long ").Append(local).Append(")) return false; ").Append(target).Append(" = BitConverter.Int64BitsToDouble(").Append(local).AppendLine(");");
                    return;
                case LeafKind.Fix64:
                    if (encoding.HasQuantize)
                    {
                        FixedRange(encoding, out long minRaw, out _, out int fraction, out int bits);
                        s.Append(pad).Append("if (!reader.ReadBits(").Append(bits).Append(", out uint ").Append(local).Append(")) return false; ").Append(target)
                            .Append(" = Quantize.BitsToFixed(").Append(local).Append(", global::CodexNetworking.FixedPoint.Fix64.FromRaw(").Append(minRaw).Append("L), ").Append(fraction).AppendLine(");");
                    }
                    else
                        s.Append(pad).Append("if (!reader.ReadInt64(out long ").Append(local).Append(")) return false; ").Append(target).Append(" = global::CodexNetworking.FixedPoint.Fix64.FromRaw(").Append(local).AppendLine(");");
                    return;
                case LeafKind.Entity:
                    s.Append(pad).Append("if (!EntitySerialization.ReadEntity(ref reader, entities, out global::CodexECS.Entity ").Append(local).Append(")) return false; ").Append(target).Append(" = ").Append(local).AppendLine(";");
                    return;
                case LeafKind.Struct:
                    EmitReadStruct(s, type, target, encoding, indent, false);
                    return;
                default:
                    s.Append(pad).Append("{ ").Append(CSharpName(type)).Append(" raw = default; if (!reader.ReadBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref raw, 1)))) return false; ").Append(target).AppendLine(" = raw; }");
                    return;
            }
        }

        private static void EmitReadInteger(StringBuilder s, Type type, string target, Encoding encoding, string pad, string local)
        {
            Type underlying = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
            bool signed = underlying == typeof(sbyte) || underlying == typeof(short) || underlying == typeof(int) || underlying == typeof(long);
            int natural = NaturalBits(underlying);
            string cast = "(" + CSharpName(type) + ")";
            if (encoding.HasRange)
            {
                s.Append(pad).Append("if (!reader.ReadRange(").Append(encoding.RangeMin).Append(", ").Append(encoding.RangeMax).Append(", out int ").Append(local).Append(")) return false; ")
                    .Append(target).Append(" = ").Append(cast).Append(local).AppendLine(";");
                return;
            }
            int bits = encoding.Bits > 0 ? Math.Min(encoding.Bits, natural) : natural;
            if (bits > 32)
                s.Append(pad).Append("if (!reader.ReadUInt64(out ulong ").Append(local).Append(")) return false; ").Append(target).Append(" = ").Append(cast).Append(local).AppendLine(";");
            else if (signed)
                s.Append(pad).Append("if (!reader.ReadSignedBits(").Append(bits).Append(", out int ").Append(local).Append(")) return false; ").Append(target).Append(" = ").Append(cast).Append(local).AppendLine(";");
            else
                s.Append(pad).Append("if (!reader.ReadBits(").Append(bits).Append(", out uint ").Append(local).Append(")) return false; ").Append(target).Append(" = ").Append(cast).Append(local).AppendLine(";");
        }

        // ---- change detection ----------------------------------------------------------------------

        private static void EmitDiffers(StringBuilder s, Type type, string a, string b)
        {
            switch (Kind(null, type))
            {
                case LeafKind.Bool:
                case LeafKind.Integer:
                    s.Append(a).Append(" != ").Append(b);
                    return;
                case LeafKind.Float:
                    if (type == typeof(float))
                        s.Append("BitConverter.SingleToInt32Bits(").Append(a).Append(") != BitConverter.SingleToInt32Bits(").Append(b).Append(')');
                    else
                        s.Append("BitConverter.DoubleToInt64Bits(").Append(a).Append(") != BitConverter.DoubleToInt64Bits(").Append(b).Append(')');
                    return;
                case LeafKind.Fix64:
                    s.Append(a).Append(".Raw != ").Append(b).Append(".Raw");
                    return;
                case LeafKind.Entity:
                    s.Append(a).Append(".Val != ").Append(b).Append(".Val");
                    return;
                case LeafKind.Struct:
                    List<FieldInfo> fields = Fields(type);
                    if (fields.Count == 0)
                    {
                        s.Append("false");
                        return;
                    }
                    s.Append('(');
                    for (int i = 0; i < fields.Count; i++)
                    {
                        if (i > 0)
                            s.Append(" || ");
                        EmitDiffers(s, fields[i].FieldType, a + "." + fields[i].Name, b + "." + fields[i].Name);
                    }
                    s.Append(')');
                    return;
                default:
                    s.Append("!RawEquals(").Append(a).Append(", ").Append(b).Append(')');
                    return;
            }
        }

        // ---- classification ------------------------------------------------------------------------

        private enum LeafKind
        {
            Bool,
            Integer,
            Float,
            Fix64,
            Entity,
            Struct,
            Raw,
        }

        private static LeafKind Kind(FieldInfo field, Type type)
        {
            if (type == typeof(bool))
                return LeafKind.Bool;
            if (type.IsEnum || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
                return LeafKind.Integer;
            if (type == typeof(float) || type == typeof(double))
                return LeafKind.Float;
            if (type.FullName == Fix64Name)
                return LeafKind.Fix64;
            if (type.FullName == EntityName)
                return LeafKind.Entity;
            if (type.IsPointer || type.IsPrimitive || !type.IsValueType)
                return LeafKind.Raw;
            if (field != null && HasAttribute(field, "System.Runtime.CompilerServices.FixedBufferAttribute"))
                return LeafKind.Raw;
            if (type.IsNestedPrivate || type.IsNestedAssembly || type.IsNotPublic)
                return LeafKind.Raw;
            return LeafKind.Struct;
        }

        private static List<FieldInfo> Fields(Type type)
        {
            FieldInfo[] all = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var result = new List<FieldInfo>(all.Length);
            foreach (FieldInfo field in all)
            {
                if (!field.IsPublic)
                    throw new InvalidOperationException(type.FullName + "." + field.Name + " is not public; generated serializers read and write public fields only");
                result.Add(field);
            }
            result.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            return result;
        }

        private static bool AllAssignable(List<FieldInfo> fields)
        {
            foreach (FieldInfo field in fields)
            {
                if (field.IsInitOnly)
                    return false;
            }
            return true;
        }

        private static ConstructorInfo MatchingConstructor(Type type, List<FieldInfo> fields)
        {
            foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                if (parameters.Length != fields.Count)
                    continue;
                bool match = true;
                for (int i = 0; i < parameters.Length && match; i++)
                    match = parameters[i].ParameterType == fields[i].FieldType;
                if (match)
                    return constructor;
            }
            return null;
        }

        private static bool HasAttribute(MemberInfo member, string fullName)
        {
            foreach (CustomAttributeData attribute in member.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == fullName)
                    return true;
            }
            return false;
        }

        private static int NaturalBits(Type type)
        {
            if (type == typeof(byte) || type == typeof(sbyte))
                return 8;
            if (type == typeof(short) || type == typeof(ushort))
                return 16;
            if (type == typeof(long) || type == typeof(ulong))
                return 64;
            return 32;
        }

        private static void FixedRange(Encoding encoding, out long minRaw, out long maxRaw, out int fraction, out int bits)
        {
            minRaw = (long)Math.Round(encoding.Min * (1 << Fix64FractionBits));
            maxRaw = (long)Math.Round(encoding.Max * (1 << Fix64FractionBits));
            if (maxRaw <= minRaw)
                throw new InvalidOperationException("[Quantize] max must exceed min");
            // Fraction bits kept: enough that the step is at most the requested precision.
            fraction = 0;
            while (fraction < Fix64FractionBits && 1.0 / (1 << fraction) > encoding.Precision)
                fraction++;
            long span = (maxRaw - minRaw) >> (Fix64FractionBits - fraction);
            if (span > uint.MaxValue)
                throw new InvalidOperationException("[Quantize] range too wide for 32 bits at this precision");
            bits = Math.Max(1, BitWriter.BitsRequired((uint)span));
        }

        // ---- naming ----------------------------------------------------------------------------------

        private static string ClassName(Type type, GeneratorOptions options)
        {
            string name = type.FullName ?? type.Name;
            if (type.Namespace != null)
                name = name.Substring(type.Namespace.Length + 1);
            return name.Replace('+', '_').Replace('.', '_') + options.ClassSuffix;
        }

        /// <summary>Fully qualified, global::-rooted C# name of a non-generic type.</summary>
        public static string CSharpName(Type type)
        {
            if (type == typeof(bool)) return "bool";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(sbyte)) return "sbyte";
            if (type == typeof(short)) return "short";
            if (type == typeof(ushort)) return "ushort";
            if (type == typeof(int)) return "int";
            if (type == typeof(uint)) return "uint";
            if (type == typeof(long)) return "long";
            if (type == typeof(ulong)) return "ulong";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type.IsPointer)
                return CSharpName(type.GetElementType()) + "*";
            return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
        }

        private static string Local(string target)
        {
            var name = new StringBuilder("v");
            foreach (char c in target)
                name.Append(char.IsLetterOrDigit(c) ? c : '_');
            return name.ToString();
        }

        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }
}
