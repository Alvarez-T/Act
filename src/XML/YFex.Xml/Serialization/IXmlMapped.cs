using System.Xml;

namespace YFex.Xml.Serialization;

/// <summary>
/// AOT-safe POCO ↔ XML contract. Generated POCOs implement this with
/// statically-dispatched read/write — no runtime reflection on the hot path.
/// The generator emits <see cref="WriteXml"/>/<c>ReadXml</c> bodies driven by
/// the schema, not by walking properties at runtime.
/// </summary>
public interface IXmlMapped<TSelf> where TSelf : IXmlMapped<TSelf>
{
    /// <summary>The literal XML element name for this type.</summary>
    static abstract string XmlName { get; }

    /// <summary>Writes this instance as the body of an already-opened element.</summary>
    void WriteXml(XmlWriter writer);

    /// <summary>Reads an instance positioned on its start element.</summary>
    static abstract TSelf ReadXml(XmlReader reader);
}

/// <summary>
/// Thin façade over <see cref="IXmlMapped{TSelf}"/> providing whole-document
/// (de)serialization with sensible XML settings.
/// </summary>
public static class XmlMap
{
    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new System.Text.UTF8Encoding(false),
    };

    public static string Serialize<T>(T value, string? defaultNamespace = null) where T : IXmlMapped<T>
    {
        using var sw = new System.IO.StringWriter();
        using (var writer = XmlWriter.Create(sw, WriterSettings))
        {
            writer.WriteStartElement(T.XmlName, defaultNamespace);
            value.WriteXml(writer);
            writer.WriteEndElement();
        }
        return sw.ToString();
    }

    public static T Deserialize<T>(string xml) where T : IXmlMapped<T>
    {
        using var sr = new System.IO.StringReader(xml);
        using var reader = XmlReader.Create(sr, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
        reader.MoveToContent();
        return T.ReadXml(reader);
    }
}
