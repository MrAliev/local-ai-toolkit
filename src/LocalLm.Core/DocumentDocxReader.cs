using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

internal sealed record DocxText(string Sha256, DocxParagraph[] Paragraphs, DocumentContentNotice[] Notices, int AdditionalNotices);
internal sealed record DocxParagraph(string Text, DocumentDocxSource Source);

internal static class DocumentDocxReader
{
    internal const int MaximumParagraphs = 4096, MaximumNotices = 64;
    internal const string TransitionalNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string StrictNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const int MaximumPartBytes = 8 * 1024 * 1024, MaximumExpandedBytes = 32 * 1024 * 1024;
    private static readonly XNamespace PackageNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static bool ValidPart(string? part) => part is { Length: > 0 and <= 240 } &&
        !part.Contains('\\') && !part.Contains(':') && !part.Contains('?') && !part.Contains('#') && !part.Contains('%') &&
        !part.Any(char.IsControl) && part.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));

    public static async Task<DocxText> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumPartBytes) throw Limit(path, "archive_bytes", MaximumPartBytes);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        input.Position = 0;
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > 256) throw Limit(path, "archive_entries", 256);
            var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // No archive entry is ever extracted to a filesystem path.
                var name = entry.FullName.TrimEnd('/');
                if (!ValidPart(name) || !parts.TryAdd(entry.FullName, entry)) throw Unsupported(path);
                if (entry.Length > MaximumPartBytes) throw Limit(path, "part_bytes", MaximumPartBytes);
                expanded += entry.Length;
                if (expanded > MaximumExpandedBytes) throw Limit(path, "expanded_bytes", MaximumExpandedBytes);
            }
            var notices = new List<DocumentContentNotice>();
            var additional = 0;
            void Notice(string part, string? location, string reason, string? element = null)
            {
                if (notices.Count < MaximumNotices) notices.Add(new(part, location, reason, element));
                else additional++;
            }
            long consumed = 0;
            async Task<XDocument> ReadXml(string part)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!parts.TryGetValue(part, out var entry)) throw Unsupported(path);
                using var content = new MemoryStream();
                await using (var compressed = entry.Open())
                {
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await compressed.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        consumed += count;
                        if (content.Length + count > MaximumPartBytes) throw Limit(path, "part_bytes", MaximumPartBytes);
                        if (consumed > MaximumExpandedBytes) throw Limit(path, "expanded_bytes", MaximumExpandedBytes);
                        content.Write(buffer, 0, count);
                    }
                }
                if (content.Length != entry.Length) throw new InvalidDataException();
                content.Position = 0;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = MaximumPartBytes, MaxCharactersFromEntities = 0 };
                // Bound tree depth/node count before allocating the LINQ-to-XML tree.
                using (var reader = XmlReader.Create(content, settings))
                {
                    var nodes = 0;
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++nodes > 100_000 || reader.Depth > 64 || reader.Name.Length > 256 || reader.NamespaceURI.Length > 256)
                            throw new DocumentIndexException("docx_limit", LocalLmText.Catalogue.Format("DocumentsDocxXmlTooComplex", path));
                    }
                }
                content.Position = 0;
                settings.Async = true;
                using var xml = XmlReader.Create(content, settings);
                return await XDocument.LoadAsync(xml, LoadOptions.None, cancellationToken);
            }
            var relationships = new Dictionary<string, XDocument>(StringComparer.Ordinal);
            foreach (var part in parts.Keys.Where(name => name.EndsWith(".rels", StringComparison.Ordinal)))
            {
                var xml = await ReadXml(part);
                if (xml.Root?.Name != PackageNamespace + "Relationships") throw Unsupported(path);
                relationships.Add(part, xml);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var relation in xml.Root.Elements())
                {
                    if (relation.Name != PackageNamespace + "Relationship" ||
                        relation.Attribute("Id")?.Value is not { Length: > 0 and <= 256 } id || !ids.Add(id) ||
                        relation.Attribute("Type") is null || relation.Attribute("Target") is null) throw Unsupported(path);
                    if ((string?)relation.Attribute("TargetMode") == "External")
                        Notice(part, null, "external_relationship_not_loaded");
                }
            }
            if (!relationships.TryGetValue("_rels/.rels", out var package)) throw Unsupported(path);
            var main = package.Root!.Elements().Where(element => (string?)element.Attribute("Type") is
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" or
                "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument").ToArray();
            if (main.Length != 1 || main[0].Attribute("TargetMode")?.Value is not (null or "Internal")) throw Unsupported(path);
            var mainPart = ((string)main[0].Attribute("Target")!).TrimStart('/');
            if (!ValidPart(mainPart)) throw Unsupported(path);
            XNamespace typesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
            var types = await ReadXml("[Content_Types].xml");
            if (types.Root?.Name != typesNamespace + "Types") throw Unsupported(path);
            var declared = types.Root.Elements(typesNamespace + "Override")
                .Where(element => (string?)element.Attribute("PartName") == "/" + mainPart).ToArray();
            if (declared.Length != 1 || (string?)declared[0].Attribute("ContentType") !=
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml") throw Unsupported(path);
            var document = await ReadXml(mainPart);
            var namespaceUri = document.Root?.Name.NamespaceName;
            if (namespaceUri is not (TransitionalNamespace or StrictNamespace)) throw Unsupported(path);
            XNamespace word = namespaceUri;
            if (document.Root!.Name != word + "document" || document.Root.Elements(word + "body").Count() != 1) throw Unsupported(path);
            foreach (var part in parts.Keys)
                if (part != mainPart && part != "[Content_Types].xml" && !relationships.ContainsKey(part) && !part.EndsWith('/'))
                    Notice(part, null, "unindexed_part");

            var paragraphs = new List<DocxParagraph>();
            var paragraphCount = 0;
            long characters = 0;
            void ReadParagraph(XElement paragraph, DocumentDocxSource source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++paragraphCount > MaximumParagraphs) throw Limit(path, "paragraphs", MaximumParagraphs);
                var text = new StringBuilder();
                XElement? unsupported = null;
                void ReadChildren(XElement container)
                {
                    foreach (var child in container.Elements())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (child.Name.Namespace != word) { unsupported = child; return; }
                        var name = child.Name.LocalName;
                        if (container.Name == word + "r")
                        {
                            if (name == "rPr") continue;
                            if (child.HasElements) { unsupported = child; return; }
                            switch (name)
                            {
                                case "t": text.Append(child.Value); break;
                                case "tab": text.Append('\t'); break;
                                case "br": case "cr": text.Append('\n'); break;
                                default: unsupported = child; return;
                            }
                        }
                        else if (name is "r" or "hyperlink") ReadChildren(child);
                        else if (name is not ("pPr" or "bookmarkStart" or "bookmarkEnd" or "proofErr" or "commentRangeStart" or "commentRangeEnd"))
                        { unsupported = child; return; }
                        if (unsupported is not null) return;
                        if (text.Length > 64_000) throw Limit(path, "paragraph_characters", 64_000);
                    }
                }
                ReadChildren(paragraph);
                if (unsupported is not null)
                {
                    Notice(mainPart, source.XPath, "unsupported_paragraph", unsupported.Name.ToString());
                    return;
                }
                characters += text.Length;
                if (characters > 8_000_000) throw Limit(path, "text_characters", 8_000_000);
                paragraphs.Add(new(text.ToString(), source));
            }
            var body = document.Root.Element(word + "body")!;
            var bodyParagraph = 0;
            var tableNumber = 0;
            var bodyElement = 0;
            foreach (var block in body.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bodyElement++;
                if (block.Name == word + "p") ReadParagraph(block, new(mainPart, namespaceUri, ++bodyParagraph));
                else if (block.Name == word + "tbl")
                {
                    var table = ++tableNumber;
                    var row = 0;
                    foreach (var tableChild in block.Elements())
                    {
                        if (tableChild.Name == word + "tblPr" || tableChild.Name == word + "tblGrid") continue;
                        if (tableChild.Name != word + "tr")
                        { Notice(mainPart, $"/w:document/w:body/w:tbl[{table}]", "unsupported_block", tableChild.Name.ToString()); continue; }
                        row++;
                        var cell = 0;
                        foreach (var rowChild in tableChild.Elements())
                        {
                            if (rowChild.Name == word + "trPr") continue;
                            if (rowChild.Name != word + "tc")
                            { Notice(mainPart, $"/w:document/w:body/w:tbl[{table}]/w:tr[{row}]", "unsupported_block", rowChild.Name.ToString()); continue; }
                            cell++;
                            var paragraph = 0;
                            foreach (var cellChild in rowChild.Elements())
                            {
                                if (cellChild.Name == word + "tcPr") continue;
                                if (cellChild.Name == word + "p") ReadParagraph(cellChild, new(mainPart, namespaceUri, ++paragraph, table, row, cell));
                                else Notice(mainPart, $"/w:document/w:body/w:tbl[{table}]/w:tr[{row}]/w:tc[{cell}]", "unsupported_block", cellChild.Name.ToString());
                            }
                        }
                    }
                }
                else if (block.Name != word + "sectPr")
                    Notice(mainPart, $"/w:document/w:body/*[{bodyElement}]", "unsupported_block", block.Name.ToString());
            }
            return new(hash, paragraphs.ToArray(), notices.ToArray(), additional);
        }
        catch (Exception error) when (error is InvalidDataException or XmlException)
        { throw new DocumentIndexException("docx_unreadable", LocalLmText.Catalogue.Format("DocumentsDocxUnreadable", path)); }
    }
    private static DocumentIndexException Unsupported(string path) => new("docx_unsupported", LocalLmText.Catalogue.Format("DocumentsDocxUnsupported", path));
    private static DocumentIndexException Limit(string path, string budget, int maximum) => new("docx_limit", LocalLmText.Catalogue.Format("DocumentsDocxStructureLimit", path, budget, maximum));
}

public sealed record DocumentDocxSource(string Part, string NamespaceUri, int Paragraph, int? Table = null, int? Row = null, int? Cell = null)
{
    [JsonPropertyName("xpath")]
    public string XPath => Table is null ? FormattableString.Invariant($"/w:document/w:body/w:p[{Paragraph}]") :
        FormattableString.Invariant($"/w:document/w:body/w:tbl[{Table}]/w:tr[{Row}]/w:tc[{Cell}]/w:p[{Paragraph}]");
    internal bool IsValid => DocumentDocxReader.ValidPart(Part) &&
        NamespaceUri is DocumentDocxReader.TransitionalNamespace or DocumentDocxReader.StrictNamespace &&
        Paragraph is > 0 and <= DocumentDocxReader.MaximumParagraphs &&
        (Table is null && Row is null && Cell is null || Table is > 0 and <= 100_000 && Row is > 0 and <= 100_000 && Cell is > 0 and <= 100_000);
}
public sealed record DocumentContentNotice(string Part, string? Location, string Reason, string? Element = null);
public sealed record DocumentExtractionNotice(string Path, string Part, string? Location, string Reason, string? Element = null);
