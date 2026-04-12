using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using MixDbg.Services;

namespace MixDbg.Engine.Sos;

/// <summary>
/// Reads portable PDB files to map (method token, IL offset) to (source file, line).
/// Registered as a singleton — caches PDB readers for the session lifetime.
/// Implements <see cref="IPdbSourceMapper"/>.
/// </summary>
internal sealed class PdbSourceMapperService : IPdbSourceMapper, IDisposable
{
    private readonly Dictionary<string, MetadataReaderProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MetadataReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PEReader Pe, MetadataReader Reader)> _peReaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> _peStreams = [];

    /// <summary>Last error from PDB/PE loading, for diagnostics.</summary>
    internal string? LastError { get; private set; }

    /// <summary>
    /// Resolves a method token and IL offset to a source file and line number
    /// by reading the portable PDB associated with the given assembly path.
    /// </summary>
    /// <returns>Source file path and line, or <c>null</c> if not found.</returns>
    public (string File, int Line)? GetSourceLocation(string assemblyPath, int methodToken, int ilOffset)
    {
        MetadataReader? reader = GetOrLoadReader(assemblyPath);
        if (reader == null)
            return null;

        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (handle.IsNil)
            return null;

        MethodDebugInformation debugInfo;
        try
        {
            debugInfo = reader.GetMethodDebugInformation(handle.ToDebugInformationHandle());
        }
        catch
        {
            return null;
        }

        if (debugInfo.SequencePointsBlob.IsNil)
            return null;

        // Walk sequence points to find the one covering the IL offset.
        string? bestFile = null;
        int bestLine = 0;
        int bestOffset = -1;

        foreach (SequencePoint sp in debugInfo.GetSequencePoints())
        {
            // Skip hidden sequence points (line 0xFEEFEE).
            if (sp.IsHidden)
                continue;

            // Find the sequence point at or just before the IL offset.
            if (sp.Offset <= ilOffset && sp.Offset > bestOffset)
            {
                bestOffset = sp.Offset;
                bestLine = sp.StartLine;
                bestFile = reader.GetString(reader.GetDocument(sp.Document).Name);
            }
        }

        return bestFile != null ? (bestFile, bestLine) : null;
    }

    /// <summary>
    /// Finds the method token and assembly name for a given source file and line,
    /// searching all loaded PDBs.
    /// </summary>
    /// <returns>Assembly name and full method name, or <c>null</c> if not found.</returns>
    public (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? FindMethodAtLine(
        string assemblyPath, string sourceFile, int line)
    {
        MetadataReader? reader = GetOrLoadReader(assemblyPath);
        if (reader == null)
            return null;

        // We need the corresponding assembly metadata reader for type/method names.
        (PEReader Pe, MetadataReader Reader)? peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return null;

        MetadataReader peReader = peReaderAndStream.Value.Reader;
        string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

        foreach (MethodDebugInformationHandle mdHandle in reader.MethodDebugInformation)
        {
            MethodDebugInformation debugInfo = reader.GetMethodDebugInformation(mdHandle);
            if (debugInfo.SequencePointsBlob.IsNil)
                continue;

            foreach (SequencePoint sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden)
                    continue;

                string docName = reader.GetString(reader.GetDocument(sp.Document).Name);
                if (!Path.GetFullPath(docName).Equals(Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sp.StartLine <= line && line <= sp.EndLine)
                {
                    MethodDefinitionHandle methodHandle = mdHandle.ToDefinitionHandle();
                    int token = MetadataTokens.GetToken(methodHandle);

                    string? methodName = GetFullMethodName(peReader, methodHandle);
                    if (methodName != null)
                        return (assemblyName, methodName, token, sp.Offset);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a method token to its fully qualified name (Namespace.Type.Method)
    /// by reading the PE metadata of the given assembly.
    /// </summary>
    /// <returns>The method name, or <c>null</c> if the token cannot be resolved.</returns>
    public string? GetMethodName(string assemblyPath, int methodToken)
    {
        (PEReader Pe, MetadataReader Reader)? peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return null;

        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        return GetFullMethodName(peReaderAndStream.Value.Reader, handle);
    }

    private static string? GetFullMethodName(MetadataReader peReader, MethodDefinitionHandle handle)
    {
        try
        {
            MethodDefinition method = peReader.GetMethodDefinition(handle);
            string methodName = peReader.GetString(method.Name);

            TypeDefinitionHandle typeHandle = method.GetDeclaringType();
            TypeDefinition type = peReader.GetTypeDefinition(typeHandle);
            string typeName = peReader.GetString(type.Name);
            string namespaceName = peReader.GetString(type.Namespace);

            // Build nested type chain if needed.
            TypeDefinitionHandle declaringType = type.GetDeclaringType();
            while (!declaringType.IsNil)
            {
                TypeDefinition outer = peReader.GetTypeDefinition(declaringType);
                typeName = peReader.GetString(outer.Name) + "+" + typeName;
                declaringType = outer.GetDeclaringType();
            }

            return string.IsNullOrEmpty(namespaceName)
                ? $"{typeName}.{methodName}"
                : $"{namespaceName}.{typeName}.{methodName}";
        }
        catch
        {
            return null;
        }
    }

    private MetadataReader? GetOrLoadReader(string assemblyPath)
    {
        if (_readers.TryGetValue(assemblyPath, out MetadataReader? cached))
            return cached;

        // Look for the portable PDB next to the assembly.
        string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
            return null;

        try
        {
            FileStream stream = File.OpenRead(pdbPath);
            MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            MetadataReader reader = provider.GetMetadataReader();

            _providers[assemblyPath] = provider;
            _readers[assemblyPath] = reader;
            return reader;
        }
        catch (Exception ex)
        {
            // Not a portable PDB, or corrupted — skip.
            LastError = $"PDB load failed for {pdbPath}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private (PEReader Pe, MetadataReader Reader)? GetOrLoadPeReader(string assemblyPath)
    {
        if (_peReaders.TryGetValue(assemblyPath, out (PEReader Pe, MetadataReader Reader) cached))
            return cached;

        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            FileStream stream = File.OpenRead(assemblyPath);
            _peStreams.Add(stream);
            PEReader pe = new(stream);
            MetadataReader reader = pe.GetMetadataReader();
            _peReaders[assemblyPath] = (pe, reader);
            return (pe, reader);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns all non-hidden sequence points for a method, sorted by IL offset.
    /// </summary>
    public (int ILOffset, string File, int Line)[] GetMethodSequencePoints(string assemblyPath, int methodToken)
    {
        MetadataReader? reader = GetOrLoadReader(assemblyPath);
        if (reader == null)
            return [];

        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (handle.IsNil)
            return [];

        MethodDebugInformation debugInfo;
        try
        {
            debugInfo = reader.GetMethodDebugInformation(handle.ToDebugInformationHandle());
        }
        catch
        {
            return [];
        }

        if (debugInfo.SequencePointsBlob.IsNil)
            return [];

        List<(int ILOffset, string File, int Line)> result = [];
        foreach (SequencePoint sp in debugInfo.GetSequencePoints())
        {
            if (sp.IsHidden)
                continue;

            string file = reader.GetString(reader.GetDocument(sp.Document).Name);
            result.Add((sp.Offset, file, sp.StartLine));
        }

        // Sort by IL offset (usually already sorted, but be safe).
        result.Sort((a, b) => a.ILOffset.CompareTo(b.ILOffset));
        return [.. result];
    }

    /// <summary>
    /// Reads the portable PDB's local scope table and returns (name, slot index) pairs
    /// for local variables in scope at the given IL offset.
    /// </summary>
    public (string Name, int Index)[] GetLocalVariableNames(string assemblyPath, int methodToken, int ilOffset)
    {
        MetadataReader? reader = GetOrLoadReader(assemblyPath);
        if (reader == null)
            return [];

        MethodDefinitionHandle methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (methodHandle.IsNil)
            return [];

        try
        {
            List<(string Name, int Index)> result = [];
            foreach (LocalScopeHandle scopeHandle in reader.GetLocalScopes(methodHandle))
            {
                LocalScope scope = reader.GetLocalScope(scopeHandle);

                // Filter: only scopes that contain the IL offset.
                if (ilOffset < scope.StartOffset || ilOffset >= scope.EndOffset)
                    continue;

                foreach (LocalVariableHandle varHandle in scope.GetLocalVariables())
                {
                    LocalVariable localVar = reader.GetLocalVariable(varHandle);
                    string name = reader.GetString(localVar.Name);
                    result.Add((name, localVar.Index));
                }
            }
            return [.. result];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the PE metadata to return parameter names for the given method, in order.
    /// </summary>
    public string[] GetParameterNames(string assemblyPath, int methodToken)
    {
        (PEReader Pe, MetadataReader Reader)? peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return [];

        MetadataReader peReader = peReaderAndStream.Value.Reader;
        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (handle.IsNil)
            return [];

        try
        {
            MethodDefinition method = peReader.GetMethodDefinition(handle);
            List<string> names = [];
            foreach (ParameterHandle paramHandle in method.GetParameters())
            {
                Parameter param = peReader.GetParameter(paramHandle);
                // Skip the return parameter (sequence 0).
                if (param.SequenceNumber == 0)
                    continue;
                names.Add(peReader.GetString(param.Name));
            }
            return [.. names];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Decodes the method signature to return parameter type names in order.
    /// </summary>
    public string[] GetParameterTypes(string assemblyPath, int methodToken)
    {
        (PEReader Pe, MetadataReader Reader)? peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return [];

        MetadataReader peReader = peReaderAndStream.Value.Reader;
        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (handle.IsNil)
            return [];

        try
        {
            MethodDefinition method = peReader.GetMethodDefinition(handle);
            MethodSignature<string> sig = method.DecodeSignature(new SimpleTypeProvider(), genericContext: null);
            return [.. sig.ParameterTypes];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Decodes the local variable signature to return type names in slot order.
    /// </summary>
    public string[] GetLocalVariableTypes(string assemblyPath, int methodToken)
    {
        (PEReader Pe, MetadataReader Reader)? peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return [];

        MetadataReader peReader = peReaderAndStream.Value.Reader;
        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (handle.IsNil)
            return [];

        try
        {
            MethodDefinition method = peReader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
                return [];

            MethodBodyBlock body = peReaderAndStream.Value.Pe.GetMethodBody(method.RelativeVirtualAddress);
            if (body.LocalSignature.IsNil)
                return [];

            StandaloneSignature localSig = peReader.GetStandaloneSignature(body.LocalSignature);
            ImmutableArray<string> types = localSig.DecodeLocalSignature(new SimpleTypeProvider(), genericContext: null);
            return [.. types];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Finds a method token by a relative virtual address (RVA) that falls inside the
    /// method body. dbgeng's <c>GetOffsetByLine</c> may return an address past the IL
    /// method header (e.g. +12 bytes into the body), so we find the method whose RVA
    /// is the largest value that is still ≤ the target RVA.
    /// </summary>
    public int? FindTokenByRva(string assemblyPath, int rva)
    {
        (PEReader Pe, MetadataReader Reader)? peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return null;

        MetadataReader reader = peReaderAndStream.Value.Reader;
        try
        {
            int bestRva = -1;
            MethodDefinitionHandle bestHandle = default;
            foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
            {
                MethodDefinition method = reader.GetMethodDefinition(handle);
                int methodRva = method.RelativeVirtualAddress;
                if (methodRva > 0 && methodRva <= rva && methodRva > bestRva)
                {
                    bestRva = methodRva;
                    bestHandle = handle;
                }
            }
            if (bestRva >= 0 && !bestHandle.IsNil)
                return MetadataTokens.GetToken(bestHandle);
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        foreach ((PEReader? pe, MetadataReader _) in _peReaders.Values)
            pe.Dispose();

        foreach (MetadataReaderProvider provider in _providers.Values)
            provider.Dispose();

        foreach (FileStream stream in _peStreams)
            stream.Dispose();

        _peReaders.Clear();
        _providers.Clear();
        _readers.Clear();
        _peStreams.Clear();
    }

    /// <summary>
    /// Minimal <see cref="ISignatureTypeProvider{TType,TGenericContext}"/> that maps
    /// ECMA-335 type signatures to friendly C# type name strings.
    /// </summary>
    private sealed class SimpleTypeProvider
        : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.IntPtr => "nint",
            PrimitiveTypeCode.UIntPtr => "nuint",
            PrimitiveTypeCode.Void => "void",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition td = r.GetTypeDefinition(handle);
            return r.GetString(td.Name);
        }

        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference tr = r.GetTypeReference(handle);
            return r.GetString(tr.Name);
        }

        public string GetTypeFromSpecification(MetadataReader r, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            TypeSpecification ts = r.GetTypeSpecification(handle);
            return ts.DecodeSignature(this, genericContext);
        }

        public string GetSZArrayType(string elementType) => $"{elementType}[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => $"ref {elementType}";
        public string GetPointerType(string elementType) => $"{elementType}*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
    }
}