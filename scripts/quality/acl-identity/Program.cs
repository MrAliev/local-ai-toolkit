using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("Usage: AclIdentity <baseline-directory> <candidate-directory> <output.json>");
var reports = new List<object>();
foreach (var library in new[] { "codesearch-mcp.dll", "CodeSearch.Core.dll", "LocalAi.Broker.Client.dll" })
{
    var baseline = Inspect(Path.Combine(args[0], library));
    var candidate = Inspect(Path.Combine(args[1], library));
    if (baseline.CodeSha256 != candidate.CodeSha256)
        throw new InvalidOperationException("Method bodies, literals or references differ: " + library);
    reports.Add(new { library, baseline, candidate });
}
await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("All method bodies, signatures, user-string literals and assembly references match in the three non-ACL libraries.");

static Identity Inspect(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    var content = new StringBuilder();
    foreach (var handle in reader.MethodDefinitions)
    {
        var method = reader.GetMethodDefinition(handle);
        var type = reader.GetTypeDefinition(method.GetDeclaringType());
        content.Append(reader.GetString(type.Namespace)).Append('.').Append(reader.GetString(type.Name))
            .Append(':').Append(reader.GetString(method.Name)).Append(':').Append(method.Attributes).Append(':').Append(method.ImplAttributes)
            .Append(':').Append(Convert.ToHexString(reader.GetBlobBytes(method.Signature)));
        if (method.RelativeVirtualAddress != 0)
        {
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            content.Append(':').Append(Convert.ToHexString(body.GetILBytes()!))
                .Append(':').Append(body.MaxStack).Append(':').Append(body.LocalVariablesInitialized)
                .Append(':').Append(JsonSerializer.Serialize(body.ExceptionRegions));
            if (!body.LocalSignature.IsNil)
                content.Append(':').Append(Convert.ToHexString(reader.GetBlobBytes(reader.GetStandaloneSignature(body.LocalSignature).Signature)));
        }
        content.AppendLine();
    }
    for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = reader.GetNextHandle(handle))
        content.Append(JsonSerializer.Serialize(reader.GetUserString(handle))).AppendLine();
    foreach (var handle in reader.AssemblyReferences)
    {
        var reference = reader.GetAssemblyReference(handle);
        content.Append(reader.GetString(reference.Name)).Append(':').Append(reference.Version).Append(':').Append(reference.Flags)
            .Append(':').Append(reader.GetString(reference.Culture))
            .Append(':').Append(Convert.ToHexString(reader.GetBlobBytes(reference.PublicKeyOrToken))).AppendLine();
    }
    return new(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))), reader.MethodDefinitions.Count);
}

sealed record Identity(string FileSha256, string CodeSha256, int MethodCount);
