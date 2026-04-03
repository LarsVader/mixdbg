using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MixDbg.Engine.Sos;

/// <summary>
/// Reads portable PDB files to map (method token, IL offset) to (source file, line).
/// Used for C# code where dbgeng cannot resolve source locations natively.
/// C++/CLI uses Windows PDBs which dbgeng handles directly.
/// </summary>
internal sealed class PdbSourceMapper : IDisposable
{
    private readonly Dictionary<string, MetadataReaderProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MetadataReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PEReader Pe, MetadataReader Reader)> _peReaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> _peStreams = new();

    /// <summary>Last error from PDB/PE loading, for diagnostics.</summary>
    internal string? LastError => _lastError;
    private string? _lastError;

    /// <summary>
    /// Resolves a method token and IL offset to a source file and line number
    /// by reading the portable PDB associated with the given assembly path.
    /// </summary>
    /// <returns>Source file path and line, or <c>null</c> if not found.</returns>
    public (string File, int Line)? GetSourceLocation(string assemblyPath, int methodToken, int ilOffset)
    {
        var reader = GetOrLoadReader(assemblyPath);
        if (reader == null)
            return null;

        var handle = MetadataTokens.MethodDefinitionHandle(methodToken);
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

        foreach (var sp in debugInfo.GetSequencePoints())
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

        if (bestFile != null)
            return (bestFile, bestLine);

        return null;
    }

    /// <summary>
    /// Finds the method token and assembly name for a given source file and line,
    /// searching all loaded PDBs.
    /// </summary>
    /// <returns>Assembly name and full method name, or <c>null</c> if not found.</returns>
    public (string AssemblyName, string MethodName, int MethodToken, int ILOffset)? FindMethodAtLine(
        string assemblyPath, string sourceFile, int line)
    {
        var reader = GetOrLoadReader(assemblyPath);
        if (reader == null)
            return null;

        // We need the corresponding assembly metadata reader for type/method names.
        var peReaderAndStream = GetOrLoadPeReader(assemblyPath);
        if (peReaderAndStream == null)
            return null;

        var peReader = peReaderAndStream.Value.Reader;
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

        foreach (var mdHandle in reader.MethodDebugInformation)
        {
            var debugInfo = reader.GetMethodDebugInformation(mdHandle);
            if (debugInfo.SequencePointsBlob.IsNil)
                continue;

            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden)
                    continue;

                var docName = reader.GetString(reader.GetDocument(sp.Document).Name);
                if (!Path.GetFullPath(docName).Equals(Path.GetFullPath(sourceFile), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sp.StartLine <= line && line <= sp.EndLine)
                {
                    var methodHandle = mdHandle.ToDefinitionHandle();
                    var token = MetadataTokens.GetToken(methodHandle);

                    var methodName = GetFullMethodName(peReader, methodHandle);
                    if (methodName != null)
                        return (assemblyName, methodName, token, sp.Offset);
                }
            }
        }

        return null;
    }

    private static string? GetFullMethodName(MetadataReader peReader, MethodDefinitionHandle handle)
    {
        try
        {
            var method = peReader.GetMethodDefinition(handle);
            var methodName = peReader.GetString(method.Name);

            var typeHandle = method.GetDeclaringType();
            var type = peReader.GetTypeDefinition(typeHandle);
            var typeName = peReader.GetString(type.Name);
            var namespaceName = peReader.GetString(type.Namespace);

            // Build nested type chain if needed.
            var declaringType = type.GetDeclaringType();
            while (!declaringType.IsNil)
            {
                var outer = peReader.GetTypeDefinition(declaringType);
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
        if (_readers.TryGetValue(assemblyPath, out var cached))
            return cached;

        // Look for the portable PDB next to the assembly.
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(pdbPath))
            return null;

        try
        {
            var stream = File.OpenRead(pdbPath);
            var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var reader = provider.GetMetadataReader();

            _providers[assemblyPath] = provider;
            _readers[assemblyPath] = reader;
            return reader;
        }
        catch (Exception ex)
        {
            // Not a portable PDB, or corrupted — skip.
            _lastError = $"PDB load failed for {pdbPath}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private (PEReader Pe, MetadataReader Reader)? GetOrLoadPeReader(string assemblyPath)
    {
        if (_peReaders.TryGetValue(assemblyPath, out var cached))
            return cached;

        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            var stream = File.OpenRead(assemblyPath);
            _peStreams.Add(stream);
            var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            _peReaders[assemblyPath] = (pe, reader);
            return (pe, reader);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var (pe, _) in _peReaders.Values)
            pe.Dispose();

        foreach (var provider in _providers.Values)
            provider.Dispose();

        foreach (var stream in _peStreams)
            stream.Dispose();

        _peReaders.Clear();
        _providers.Clear();
        _readers.Clear();
        _peStreams.Clear();
    }
}
