using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

// Read the assembly as data. No Ntilde code is loaded or executed.
byte[] image = File.ReadAllBytes(args.Single());
using var pe = new PEReader(new MemoryStream(image));
MetadataReader metadata = pe.GetMetadataReader();
AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
if (metadata.GetString(assembly.Name) != "Ntilde.VT")
    throw new InvalidDataException("Expected the Ntilde.VT assembly.");

var version = assembly.GetCustomAttributes().Select(metadata.GetCustomAttribute).Single(attribute =>
{
    if (attribute.Constructor.Kind != HandleKind.MemberReference)
        return false;
    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    if (constructor.Parent.Kind != HandleKind.TypeReference || metadata.GetString(constructor.Name) != ".ctor")
        return false;
    var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
    return metadata.GetString(type.Namespace) == "System.Reflection"
        && metadata.GetString(type.Name) == "AssemblyInformationalVersionAttribute";
});

// Retain the exact file range selected by the metadata API for offline verification.
BlobReader root = pe.GetMetadata().GetReader();
root.Offset = metadata.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(version.Value);
int size = root.ReadCompressedInteger();
byte[] blob = metadata.GetBlobBytes(version.Value);
if (size != blob.Length || !root.ReadBytes(size).SequenceEqual(blob))
    throw new InvalidDataException("Informational version attribute bytes differ from the blob heap.");
int fileOffset = pe.PEHeaders.MetadataStartOffset + root.Offset - size;

Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(image)));
Console.WriteLine(fileOffset);
Console.WriteLine(Convert.ToBase64String(blob));
