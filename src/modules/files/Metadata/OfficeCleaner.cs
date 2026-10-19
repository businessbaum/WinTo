using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MultiTool.Modules.Files.Metadata;

/// <summary>Rebuilds Open XML packages, removes provenance-bearing parts and
/// relationship targets, and sanitizes supported embedded images. Unknown binary
/// content is refused instead of silently carried to the output.</summary>
internal static class OfficeCleaner
{
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Content = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const long MaximumExpanded = 512L * 1024 * 1024;

    public static IReadOnlyList<MetadataEntry> Inspect(string path, CancellationToken token)
    {
        var parts = Read(path, token);
        List<MetadataEntry> entries = [];
        foreach (var name in parts.Keys.Where(RemovePart)) entries.Add(new("Office", name, "Extra part will be removed", true));
        entries.Add(new("Office", "Rebuild", "Properties, comments, revision marks, notes, links and extra parts are cleaned.", false));
        return entries;
    }

    private static Dictionary<string, byte[]> Read(string path, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("Office file contains more than 10000 parts.");
        Dictionary<string, byte[]> parts = new(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            string name = entry.FullName;
            if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(p => p is ".." or "." or "") || name.Contains('%'))
                throw new InvalidDataException("Office package contains an invalid part path.");
            total = checked(total + entry.Length);
            if (total > MaximumExpanded || entry.Length > ImageContainers.MaximumBytes) throw new InvalidDataException("Office package exceeds the size limit.");
            using var stream = entry.Open();
            var bytes = new byte[checked((int)entry.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 || !parts.TryAdd(name, bytes)) throw new InvalidDataException("Office package contains duplicate or inconsistent parts.");
        }
        if (!parts.ContainsKey("[Content_Types].xml") || !parts.ContainsKey("_rels/.rels")) throw new InvalidDataException("Not a valid Open XML document.");
        return parts;
    }

    private static bool RemovePart(string path)
    {
        string name = path.ToLowerInvariant();
        return name.StartsWith("docprops/") || name.StartsWith("customxml/") || name.StartsWith("_xmlsignatures/")
            || name.Contains("c2pa") || name.Contains("jumb") || name.Contains("/comments") || name.Contains("/threadedcomments")
            || name.Contains("/persons/") || name.Contains("/people") || name.Contains("/authors") || name.Contains("/commentauthors")
            || name.Contains("/notesslides/") || name.Contains("/notesmasters/") || name.Contains("/printersettings/")
            || name.Contains("/embeddings/") || name.Contains("/activex/") || name.Contains("vbaproject")
            || name.Contains("/revisions/") || name.Contains("/externallinks/") || name.EndsWith("/connections.xml")
            || name.Contains("/querytables/") || name.Contains("/webextensions/") || name.Contains("/customdata/");
    }

    private static XDocument Xml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ImageContainers.MaximumBytes });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static byte[] Bytes(XDocument document)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false })) document.Save(writer);
        return output.ToArray();
    }

    private static string Owner(string relationshipPart)
    {
        if (relationshipPart == "_rels/.rels") return "";
        int index = relationshipPart.LastIndexOf("/_rels/", StringComparison.Ordinal);
        if (index < 0 || !relationshipPart.EndsWith(".rels", StringComparison.Ordinal)) throw new InvalidDataException("Invalid relationship path.");
        return relationshipPart[..(index + 1)] + relationshipPart[(index + 7)..^5];
    }

    private static string Resolve(string owner, string target)
    {
        if (target.Contains('\\') || target.Contains('%') || target.Contains(':') || target.Contains('#') || target.Contains('?'))
            throw new InvalidDataException("Unsupported Office relationship target.");
        string directory = owner.Contains('/') ? owner[..(owner.LastIndexOf('/') + 1)] : "";
        List<string> pieces = [];
        foreach (string piece in (target.StartsWith('/') ? target[1..] : directory + target).Split('/'))
        {
            if (piece == "..") { if (pieces.Count == 0) throw new InvalidDataException("Relationship target outside the package."); pieces.RemoveAt(pieces.Count - 1); }
            else if (piece is not ("" or ".")) pieces.Add(piece);
        }
        return string.Join('/', pieces);
    }

    public static void Clean(string source, string target, CancellationToken token)
    {
        var parts = Read(source, token);
        foreach (var name in parts.Keys.Where(RemovePart).ToArray()) parts.Remove(name);
        var xml = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var (name, data) in parts.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                var document = Xml(data);
                ScrubXml(document);
                xml.Add(name, document);
            }
            else
            {
                var kind = Cleaner.KindOf(name);
                if (!Cleaner.IsNativeImage(kind)) throw new InvalidDataException($"Office extra part can't be cleaned safely: {name}.");
                parts[name] = ImageContainers.Clean(data, kind, token).Bytes;
            }
        }
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, document) in xml.Where(p => p.Key.EndsWith(".rels", StringComparison.Ordinal)))
        {
            string owner = Owner(name);
            if (document.Root?.Name != Rel + "Relationships") throw new InvalidDataException("Invalid Office relationships.");
            List<string> targets = [];
            HashSet<string> removedIds = [], ids = [];
            foreach (var relation in document.Root.Elements().ToArray())
            {
                string id = (string?)relation.Attribute("Id") ?? throw new InvalidDataException("Relationship without id.");
                if (!ids.Add(id)) throw new InvalidDataException("Duplicate relationship id.");
                string destination = (string?)relation.Attribute("Target") ?? "";
                bool external = string.Equals((string?)relation.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
                string resolved = external ? "" : Resolve(owner, destination);
                if (external || !parts.ContainsKey(resolved)) { removedIds.Add(id); relation.Remove(); }
                else targets.Add(resolved);
            }
            edges[owner] = targets;
            if (xml.TryGetValue(owner, out var ownerXml))
            {
                foreach (var attribute in ownerXml.Descendants().Attributes().Where(a => a.Name.NamespaceName.EndsWith("/relationships", StringComparison.Ordinal) && removedIds.Contains(a.Value)).ToArray())
                {
                    var element = attribute.Parent!;
                    if (element.Name.LocalName == "hyperlink") { attribute.Remove(); continue; }
                    // An OLE object or a reference to removed notes/comments must not
                    // keep a dangling relationship. The document's ordinary text stays.
                    element.Remove();
                }
            }
        }
        HashSet<string> reachable = ["[Content_Types].xml", "_rels/.rels"];
        void Visit(string owner)
        {
            if (!edges.TryGetValue(owner, out var targets)) return;
            foreach (string destination in targets)
                if (reachable.Add(destination)) Visit(destination);
        }
        Visit("");
        foreach (var name in xml.Keys.Where(n => n.EndsWith(".rels", StringComparison.Ordinal)))
            if (reachable.Contains(Owner(name))) reachable.Add(name);
        var types = xml["[Content_Types].xml"];
        if (types.Root?.Name != Content + "Types") throw new InvalidDataException("Invalid Office content types.");
        foreach (var item in types.Root.Elements(Content + "Override").ToArray())
            if (!reachable.Contains(((string?)item.Attribute("PartName") ?? "").TrimStart('/'))) item.Remove();
        if (!reachable.Any(n => n is "word/document.xml" or "xl/workbook.xml" or "ppt/presentation.xml")) throw new InvalidDataException("Office main document missing.");
        using (var output = ZipFile.Open(target, ZipArchiveMode.Update))
        {
            foreach (string name in reachable.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!parts.ContainsKey(name)) continue;
                var entry = output.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(xml.TryGetValue(name, out var document) ? Bytes(document) : parts[name]);
            }
        }
        var verified = Read(target, token);
        if (verified.Keys.Any(RemovePart)) throw new InvalidDataException("Office output contains extra parts.");
        foreach (var (name, data) in verified.Where(p => p.Key.EndsWith(".rels", StringComparison.Ordinal)))
            foreach (var relationship in Xml(data).Root!.Elements())
                if (relationship.Attribute("TargetMode")?.Value == "External" || !verified.ContainsKey(Resolve(Owner(name), relationship.Attribute("Target")!.Value)))
                    throw new InvalidDataException("Office output contains an unverified relationship.");
    }

    private static void ScrubXml(XDocument document)
    {
        foreach (var node in document.DescendantNodes().Where(n => n is XComment or XProcessingInstruction).ToArray()) node.Remove();
        foreach (var element in document.Descendants().Reverse().ToArray())
        {
            string name = element.Name.LocalName;
            if (name is "extLst" or "custDataLst" or "commentRangeStart" or "commentRangeEnd" or "commentReference"
                or "del" or "moveFrom" or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd"
                or "rsids" or "docVars" or "attachedTemplate" or "trackRevisions" or "personalInformation" or "externalReferences"
                or "connections" or "legacyDrawing" or "legacyDrawingHF" or "notesMasterIdLst" or "notesSz"
                || name.EndsWith("PrChange", StringComparison.Ordinal) || name.Contains("c2pa", StringComparison.OrdinalIgnoreCase))
            { element.Remove(); continue; }
            if (name is "ins" or "moveTo" or "customXml" or "smartTag") { element.ReplaceWith(element.Nodes()); continue; }
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
            {
                string key = attribute.Name.LocalName;
                if (key.StartsWith("rsid", StringComparison.Ordinal) || key is "author" or "initials" or "date" or "dateUtc" or "userId"
                    or "paraId" or "textId" or "creationId" or "editId" or "lastEdited" or "lastModifiedBy" or "creator"
                    or "descr" or "title") attribute.Remove();
                else if (name == "cNvPr" && key == "name") attribute.Value = "Object";
            }
        }
    }
}
